// VamDlssNr WorkScale -- native half.
//
// Two D3D11 passes (see vws.hlsl) run on Unity's render thread, around the Neural Rendering
// evaluate that VamDlssNr itself issues:
//
//     frame --PSDown--> model input --[VamDlssNr: NR at the reduced size]--> model output
//     frame + model input + model output --PSResolve--> frame-size result
//
// Threading is the whole design. VaM's device is created D3D11_CREATE_DEVICE_SINGLETHREADED, so
// nothing here may touch a D3D11 interface from the main thread -- not even AddRef. The managed
// side only ever *describes* work: it copies a command into a slot and hands Unity the slot's
// number through GL.IssuePluginEvent, and everything real happens in OnRenderEvent, on the render
// thread, in command-stream order.
//
// Texture lifetime follows from the same rule. A raw pointer from GetNativeTexturePtr is only
// dereferenced by a REGISTER command, which runs in stream order right behind the sync that
// produced the pointer -- so the texture is alive when it is taken. REGISTER takes its own
// reference, and every later pass uses that; a render texture Unity has since replaced then gives
// a stale picture until the managed side re-registers, never a dangling pointer.
//
// A set is registered in two halves because its textures become known at two different moments of
// VamDlssNr's frame: the frame and the model input before its evaluate is set up, the model output
// and the result after.
//
// When the model works on a WINDOW of the frame rather than all of it, its guides -- motion
// vectors, depth, the control mask -- have to be cut to the same window. Those are VamDlssNr's own
// textures, three pairs of them (what it has, what the network is given), registered one pair at a
// time as they are met and copied by a third pass, PSGuide.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>

#include "vws_vs.h"
#include "vws_ps_down.h"
#include "vws_ps_resolve.h"
#include "vws_ps_guide.h"

#define VWS_EXPORT extern "C" __declspec(dllexport)

namespace
{
const uint32_t kAbi = 5;
const int kEventMagic = 0x57530000; // 'WS'
const int kEventMask = 0x7FFF0000;
const int kSlots = 64;
const uint32_t kSets = 16;
const uint32_t kGuides = 3;

enum Op : uint32_t
{
    OpRegister = 1,
    OpDownsample = 2,
    OpResolve = 3,
    OpRelease = 4,
    OpReleaseAll = 5,
    OpRegisterGuide = 6, // `which` = which guide; frame = its source, proxy = the model's copy
    OpGuide = 7,         // `which` = which guide
};

enum Error : int
{
    ErrNone = 0,
    ErrBadArgs = -1,      // a null or non-texture pointer
    ErrBadShape = -2,     // multisampled, an array, or sizes that do not pair up
    ErrViewFailed = -3,   // a view could not be created
    ErrDeviceFailed = -4, // the pipeline objects could not be created
};

const uint32_t kFrame = 1, kProxy = 2, kModel = 4, kResult = 8;
const int kReadyDown = 1, kReadyResolve = 2, kReadyGuide = 4; // kReadyGuide << n for guide n

#pragma pack(push, 8)
struct VwsCmd
{
    uint32_t op;
    uint32_t set;
    void* frame;       // register: the full-size frame the model is to work on (read)
    void* proxy;       // register: the model-size input (written by PSDown, read by PSResolve)
    void* model;       // register: the model-size output VamDlssNr's evaluate writes (read)
    void* result;      // register: the frame-size target (written by PSResolve)
    uint32_t srgbMask; // register: which of the four hold sRGB data (kFrame.. bits)
    uint32_t eyes;     // passes: 1, or 2 for a double-wide stereo frame
    float strength;    // resolve
    uint32_t mode;     // resolve: 0 matched residual, 1 classic, 2 edit view, 3 frame untouched
    uint32_t which;    // register: which of the four this command (re)opens (kFrame.. bits); guides: which guide
    uint32_t token;    // register: echoed in the status once this command has run
    uint32_t reserved[2];
    float window[8];   // passes: per eye, the origin (xy) and size (zw) of the model's window, as fractions of the eye
    float feather;     // resolve: how far in from the window's edge the edit fades, as a fraction of its half-size
    uint32_t windowed; // passes: 1 when `window` is in force, 0 for the whole of each eye
    float prev[8];     // guide: per eye, the window as it was a frame ago (origin xy, size zw), as fractions of the eye
    uint32_t moved;    // guide: 1 when these are motion vectors and `prev` differs from `window`
    uint32_t topDown;  // guide: 1 when the textures' first row is the top of the picture
};

struct VwsStatus
{
    int32_t ready;  // kReadyDown | kReadyResolve
    int32_t error;  // 0, or why the last registration or pass was refused
    uint32_t token; // of the last register command that has run
    int32_t lastHr;
    uint32_t frameW, frameH;
    uint32_t workW, workH;
    uint32_t frameFormat, proxyFormat, modelFormat, resultFormat;
    uint32_t registrations;
    uint32_t downsamples;
    uint32_t resolves;
    uint32_t skipped;
};
#pragma pack(pop)

struct Params
{
    float frameSize[4];
    float dstSize[4];
    float workSize[4];
    uint32_t eyes;
    uint32_t flags;
    float strength;
    uint32_t mode;
    float window[2][4];
    float fade[4];
    float prev[2][4];
};

const uint32_t kFlagFrameEncoded = 1;
const uint32_t kFlagProxyEncoded = 2;
const uint32_t kFlagModelEncoded = 4;
const uint32_t kFlagOutEncoded = 8;
const uint32_t kFlagWindow = 16;
const uint32_t kFlagMoved = 32;
const uint32_t kFlagTopDown = 64;

enum Pass
{
    PassDown,
    PassResolve,
    PassGuide,
};

template <typename T> void SafeRelease(T*& p)
{
    if (p != nullptr)
    {
        p->Release();
        p = nullptr;
    }
}

struct Surface
{
    ID3D11Texture2D* tex = nullptr;
    ID3D11ShaderResourceView* srv = nullptr;
    ID3D11RenderTargetView* rtv = nullptr;
    D3D11_TEXTURE2D_DESC desc {};
    bool srgbData = false;      // the texture holds sRGB-encoded values
    bool hardwareCodec = false; // ...and its views convert them, because its format leaves no choice

    void Release()
    {
        SafeRelease(rtv);
        SafeRelease(srv);
        SafeRelease(tex);
        desc = {};
        srgbData = hardwareCodec = false;
    }

    // What the shader sees through this surface's views: the encoded values themselves, or linear.
    bool Encoded() const { return srgbData && !hardwareCodec; }
};

struct Set
{
    Surface frame, proxy, model, result;
    Surface guideSrc[kGuides], guideDst[kGuides];
    VwsStatus status {};
    bool shapeReported = false;

    void Release()
    {
        frame.Release();
        proxy.Release();
        model.Release();
        result.Release();

        for (uint32_t i = 0; i < kGuides; ++i)
        {
            guideSrc[i].Release();
            guideDst[i].Release();
        }

        status = {};
        shapeReported = false;
    }

    bool CanDownsample() const { return frame.srv != nullptr && proxy.rtv != nullptr; }

    bool CanGuide(uint32_t i) const { return i < kGuides && guideSrc[i].srv != nullptr && guideDst[i].rtv != nullptr; }

    bool Complete() const
    {
        return CanDownsample() && proxy.srv != nullptr && model.srv != nullptr && result.rtv != nullptr;
    }

    bool Paired() const
    {
        return proxy.desc.Width == model.desc.Width && proxy.desc.Height == model.desc.Height &&
               frame.desc.Width == result.desc.Width && frame.desc.Height == result.desc.Height;
    }

    void Refresh()
    {
        status.ready = (CanDownsample() ? kReadyDown : 0) | (Complete() && Paired() ? kReadyResolve : 0);

        for (uint32_t i = 0; i < kGuides; ++i)
            status.ready |= CanGuide(i) ? (kReadyGuide << i) : 0;

        status.frameW = frame.desc.Width;
        status.frameH = frame.desc.Height;
        status.workW = proxy.desc.Width;
        status.workH = proxy.desc.Height;
        status.frameFormat = (uint32_t) frame.desc.Format;
        status.proxyFormat = (uint32_t) proxy.desc.Format;
        status.modelFormat = (uint32_t) model.desc.Format;
        status.resultFormat = (uint32_t) result.desc.Format;
    }
};

// --- shared between the two threads, under g_lock -------------------------------------------
SRWLOCK g_lock = SRWLOCK_INIT;
VwsCmd g_cmds[kSlots];
uint32_t g_nextSlot = 0;
VwsStatus g_published[kSets];
char g_log[16384];
size_t g_logLen = 0;
uint32_t g_events = 0;

// --- render thread only ---------------------------------------------------------------------
ID3D11Device* g_device = nullptr;
ID3D11DeviceContext* g_ctx = nullptr;
ID3D11VertexShader* g_vs = nullptr;
ID3D11PixelShader* g_psDown = nullptr;
ID3D11PixelShader* g_psResolve = nullptr;
ID3D11PixelShader* g_psGuide = nullptr;
ID3D11SamplerState* g_sampler = nullptr;
ID3D11Buffer* g_cb = nullptr;
ID3D11RasterizerState* g_raster = nullptr;
ID3D11BlendState* g_blend = nullptr;
ID3D11DepthStencilState* g_depth = nullptr;
Set g_sets[kSets];

void Log(const char* fmt, ...)
{
    char line[512];
    va_list args;
    va_start(args, fmt);
    const int n = vsnprintf(line, sizeof(line) - 2, fmt, args);
    va_end(args);

    if (n <= 0)
        return;

    size_t len = (size_t) n < sizeof(line) - 2 ? (size_t) n : sizeof(line) - 2;
    line[len++] = '\n';

    AcquireSRWLockExclusive(&g_lock);

    if (g_logLen + len < sizeof(g_log))
    {
        memcpy(g_log + g_logLen, line, len);
        g_logLen += len;
    }

    ReleaseSRWLockExclusive(&g_lock);
}

void Publish(uint32_t set)
{
    AcquireSRWLockExclusive(&g_lock);
    g_published[set] = g_sets[set].status;
    ReleaseSRWLockExclusive(&g_lock);
}

const char* FormatName(DXGI_FORMAT f)
{
    switch (f)
    {
    case DXGI_FORMAT_UNKNOWN: return "none";
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: return "RGBA8_TYPELESS";
    case DXGI_FORMAT_R8G8B8A8_UNORM: return "RGBA8_UNORM";
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: return "RGBA8_UNORM_SRGB";
    case DXGI_FORMAT_B8G8R8A8_TYPELESS: return "BGRA8_TYPELESS";
    case DXGI_FORMAT_B8G8R8A8_UNORM: return "BGRA8_UNORM";
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: return "BGRA8_UNORM_SRGB";
    case DXGI_FORMAT_R16G16B16A16_TYPELESS: return "RGBA16_TYPELESS";
    case DXGI_FORMAT_R16G16B16A16_FLOAT: return "RGBA16_FLOAT";
    case DXGI_FORMAT_R32G32B32A32_TYPELESS: return "RGBA32_TYPELESS";
    case DXGI_FORMAT_R32G32B32A32_FLOAT: return "RGBA32_FLOAT";
    case DXGI_FORMAT_R10G10B10A2_TYPELESS: return "RGB10A2_TYPELESS";
    case DXGI_FORMAT_R10G10B10A2_UNORM: return "RGB10A2_UNORM";
    case DXGI_FORMAT_R11G11B10_FLOAT: return "R11G11B10_FLOAT";
    case DXGI_FORMAT_R16G16_TYPELESS: return "RG16_TYPELESS";
    case DXGI_FORMAT_R16G16_FLOAT: return "RG16_FLOAT";
    case DXGI_FORMAT_R32_TYPELESS: return "R32_TYPELESS";
    case DXGI_FORMAT_R32_FLOAT: return "R32_FLOAT";
    case DXGI_FORMAT_R16_TYPELESS: return "R16_TYPELESS";
    case DXGI_FORMAT_R16_FLOAT: return "R16_FLOAT";
    default: return "other";
    }
}

// The format a view of this texture should use: always the RAW one where there is a choice.
//
// Unity allocates a render texture that may be read as sRGB as TYPELESS and picks per view. The
// passes want the stored values themselves (see vws.hlsl for why the resolve filters them encoded),
// so a typeless 8-bit texture is viewed UNORM and the shader converts. Only a texture typed *_SRGB
// outright forces the hardware conversion, and `hardwareCodec` reports that so the shader does not
// convert a second time.
DXGI_FORMAT ViewFormat(DXGI_FORMAT f, bool* hardwareCodec)
{
    *hardwareCodec = false;

    switch (f)
    {
    case DXGI_FORMAT_R8G8B8A8_TYPELESS: return DXGI_FORMAT_R8G8B8A8_UNORM;
    case DXGI_FORMAT_B8G8R8A8_TYPELESS: return DXGI_FORMAT_B8G8R8A8_UNORM;
    case DXGI_FORMAT_B8G8R8X8_TYPELESS: return DXGI_FORMAT_B8G8R8X8_UNORM;
    case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
    case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
    case DXGI_FORMAT_B8G8R8X8_UNORM_SRGB:
        *hardwareCodec = true;
        return f;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
    case DXGI_FORMAT_R32G32B32A32_TYPELESS: return DXGI_FORMAT_R32G32B32A32_FLOAT;
    case DXGI_FORMAT_R10G10B10A2_TYPELESS: return DXGI_FORMAT_R10G10B10A2_UNORM;
    case DXGI_FORMAT_R16G16_TYPELESS: return DXGI_FORMAT_R16G16_FLOAT; // the guides: motion,
    case DXGI_FORMAT_R32_TYPELESS: return DXGI_FORMAT_R32_FLOAT;       // depth
    case DXGI_FORMAT_R16_TYPELESS: return DXGI_FORMAT_R16_FLOAT;
    default: return f;
    }
}

void ReleaseDevice()
{
    for (Set& s : g_sets)
        s.Release();

    SafeRelease(g_depth);
    SafeRelease(g_blend);
    SafeRelease(g_raster);
    SafeRelease(g_cb);
    SafeRelease(g_sampler);
    SafeRelease(g_psGuide);
    SafeRelease(g_psResolve);
    SafeRelease(g_psDown);
    SafeRelease(g_vs);
    SafeRelease(g_ctx);
    SafeRelease(g_device);
}

HRESULT EnsureDevice(ID3D11Device* device)
{
    if (g_device == device && g_vs != nullptr)
        return S_OK;

    if (g_device != nullptr)
    {
        Log("device changed -- dropping everything built on the old one");
        ReleaseDevice();

        // Every set went with it, and the managed side has to be able to see that.
        AcquireSRWLockExclusive(&g_lock);
        memset(g_published, 0, sizeof(g_published));
        ReleaseSRWLockExclusive(&g_lock);
    }

    g_device = device;
    g_device->AddRef();
    g_device->GetImmediateContext(&g_ctx);

    HRESULT hr = g_device->CreateVertexShader(g_vwsVs, sizeof(g_vwsVs), nullptr, &g_vs);

    if (SUCCEEDED(hr))
        hr = g_device->CreatePixelShader(g_vwsPsDown, sizeof(g_vwsPsDown), nullptr, &g_psDown);

    if (SUCCEEDED(hr))
        hr = g_device->CreatePixelShader(g_vwsPsResolve, sizeof(g_vwsPsResolve), nullptr, &g_psResolve);

    if (SUCCEEDED(hr))
        hr = g_device->CreatePixelShader(g_vwsPsGuide, sizeof(g_vwsPsGuide), nullptr, &g_psGuide);

    if (SUCCEEDED(hr))
    {
        D3D11_SAMPLER_DESC sd {};
        sd.Filter = D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;
        sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sd.ComparisonFunc = D3D11_COMPARISON_NEVER;
        sd.MaxAnisotropy = 1;
        sd.MaxLOD = D3D11_FLOAT32_MAX;
        hr = g_device->CreateSamplerState(&sd, &g_sampler);
    }

    if (SUCCEEDED(hr))
    {
        D3D11_BUFFER_DESC bd {};
        bd.ByteWidth = sizeof(Params);
        bd.Usage = D3D11_USAGE_DYNAMIC;
        bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        hr = g_device->CreateBuffer(&bd, nullptr, &g_cb);
    }

    if (SUCCEEDED(hr))
    {
        D3D11_RASTERIZER_DESC rd {};
        rd.FillMode = D3D11_FILL_SOLID;
        rd.CullMode = D3D11_CULL_NONE;
        rd.DepthClipEnable = TRUE;
        hr = g_device->CreateRasterizerState(&rd, &g_raster);
    }

    if (SUCCEEDED(hr))
    {
        D3D11_BLEND_DESC bd {};
        bd.RenderTarget[0].BlendEnable = FALSE;
        bd.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_ALL;
        hr = g_device->CreateBlendState(&bd, &g_blend);
    }

    if (SUCCEEDED(hr))
    {
        D3D11_DEPTH_STENCIL_DESC dd {};
        dd.DepthEnable = FALSE;
        dd.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ZERO;
        dd.DepthFunc = D3D11_COMPARISON_ALWAYS;
        dd.StencilEnable = FALSE;
        hr = g_device->CreateDepthStencilState(&dd, &g_depth);
    }

    if (FAILED(hr))
    {
        Log("pipeline objects could not be created (hr=0x%08X)", (unsigned) hr);
        ReleaseDevice();
    }

    return hr;
}

// The device, named by whichever texture this command brought. Null when it brought none usable.
ID3D11Device* DeviceOf(void* raw)
{
    if (raw == nullptr)
        return nullptr;

    ID3D11Texture2D* tex = nullptr;

    if (FAILED(((IUnknown*) raw)->QueryInterface(__uuidof(ID3D11Texture2D), (void**) &tex)) || tex == nullptr)
        return nullptr;

    ID3D11Device* device = nullptr;
    tex->GetDevice(&device);
    tex->Release();
    return device;
}

// Takes a reference on the texture behind `raw` and builds the views this pass needs of it.
int OpenSurface(Surface& s, void* raw, bool srgb, bool wantSrv, bool wantRtv, const char* what, HRESULT* hrOut)
{
    s.Release();

    if (raw == nullptr)
    {
        Log("register: %s is null", what);
        return ErrBadArgs;
    }

    ID3D11Texture2D* tex = nullptr;
    HRESULT hr = ((IUnknown*) raw)->QueryInterface(__uuidof(ID3D11Texture2D), (void**) &tex);

    if (FAILED(hr) || tex == nullptr)
    {
        *hrOut = hr;
        Log("register: %s is not a 2D texture (hr=0x%08X)", what, (unsigned) hr);
        return ErrBadArgs;
    }

    s.tex = tex;
    tex->GetDesc(&s.desc);

    if (s.desc.SampleDesc.Count != 1 || s.desc.ArraySize != 1)
    {
        Log("register: %s is %ux%u samples=%u array=%u -- only plain 2D textures are handled", what,
            s.desc.Width, s.desc.Height, s.desc.SampleDesc.Count, s.desc.ArraySize);
        s.Release();
        return ErrBadShape;
    }

    bool hardwareCodec = false;
    const DXGI_FORMAT viewFormat = ViewFormat(s.desc.Format, &hardwareCodec);
    s.srgbData = srgb;
    s.hardwareCodec = hardwareCodec;

    if (wantSrv)
    {
        D3D11_SHADER_RESOURCE_VIEW_DESC vd {};
        vd.Format = viewFormat;
        vd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        vd.Texture2D.MostDetailedMip = 0;
        vd.Texture2D.MipLevels = 1;
        hr = g_device->CreateShaderResourceView(tex, &vd, &s.srv);

        if (FAILED(hr))
        {
            *hrOut = hr;
            Log("register: no shader view of %s (%s as %s, bind 0x%X, hr=0x%08X)", what,
                FormatName(s.desc.Format), FormatName(viewFormat), s.desc.BindFlags, (unsigned) hr);
            s.Release();
            return ErrViewFailed;
        }
    }

    if (wantRtv)
    {
        D3D11_RENDER_TARGET_VIEW_DESC vd {};
        vd.Format = viewFormat;
        vd.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
        vd.Texture2D.MipSlice = 0;
        hr = g_device->CreateRenderTargetView(tex, &vd, &s.rtv);

        if (FAILED(hr))
        {
            *hrOut = hr;
            Log("register: no target view of %s (%s as %s, bind 0x%X, hr=0x%08X)", what,
                FormatName(s.desc.Format), FormatName(viewFormat), s.desc.BindFlags, (unsigned) hr);
            s.Release();
            return ErrViewFailed;
        }
    }

    return ErrNone;
}

void Register(const VwsCmd& cmd)
{
    Set& set = g_sets[cmd.set];
    const uint32_t registrations = set.status.registrations + 1;
    set.shapeReported = false;

    int error = ErrNone;
    HRESULT hr = S_OK;

    // Whichever listed texture comes first names the device; they all came from the same Unity.
    void* const listed[4] = { (cmd.which & kFrame) ? cmd.frame : nullptr, (cmd.which & kProxy) ? cmd.proxy : nullptr,
                              (cmd.which & kModel) ? cmd.model : nullptr,
                              (cmd.which & kResult) ? cmd.result : nullptr };
    ID3D11Device* device = nullptr;

    for (void* p : listed)
    {
        device = DeviceOf(p);

        if (device != nullptr)
            break;
    }

    if (device == nullptr)
    {
        Log("register %u: no listed pointer is a D3D11 texture (which=0x%X)", cmd.set, cmd.which);
        error = ErrBadArgs;
    }
    else
    {
        hr = EnsureDevice(device);
        device->Release();

        if (FAILED(hr))
            error = ErrDeviceFailed;
    }

    // A device change above released every set, this one included, so the surfaces not listed here
    // are simply absent until their half is registered again.
    auto open = [&](uint32_t bit, Surface& s, void* raw, bool srv, bool rtv, const char* what) {
        if ((cmd.which & bit) == 0)
            return;

        if (g_device == nullptr)
        {
            s.Release();
            return;
        }

        const int e = OpenSurface(s, raw, (cmd.srgbMask & bit) != 0, srv, rtv, what, &hr);

        if (e != ErrNone && error == ErrNone)
            error = e;
    };

    open(kFrame, set.frame, cmd.frame, true, false, "frame");
    open(kProxy, set.proxy, cmd.proxy, true, true, "model input");
    open(kModel, set.model, cmd.model, true, false, "model output");
    open(kResult, set.result, cmd.result, false, true, "result");

    // Written last: a device change on the way here wiped the set's status along with the set.
    set.status.registrations = registrations;
    set.status.token = cmd.token;
    set.status.error = error;
    set.status.lastHr = (int32_t) hr;
    set.Refresh();

    if (error == ErrNone)
    {
        Log("set %u: %s%s%s%s registered -- frame %ux%u %s%s, model %ux%u in %s%s / out %s%s, result %s%s%s",
            cmd.set, (cmd.which & kFrame) ? "frame " : "", (cmd.which & kProxy) ? "input " : "",
            (cmd.which & kModel) ? "output " : "", (cmd.which & kResult) ? "result " : "", set.frame.desc.Width,
            set.frame.desc.Height, FormatName(set.frame.desc.Format), set.frame.Encoded() ? " [sRGB, raw]" : "",
            set.proxy.desc.Width, set.proxy.desc.Height, FormatName(set.proxy.desc.Format),
            set.proxy.Encoded() ? " [sRGB, raw]" : "", FormatName(set.model.desc.Format),
            set.model.Encoded() ? " [sRGB, raw]" : "", FormatName(set.result.desc.Format),
            set.result.Encoded() ? " [sRGB, raw]" : "",
            (set.status.ready & kReadyResolve) ? " -- ready" : ((set.status.ready & kReadyDown) ? " -- input half ready" : ""));
    }

    Publish(cmd.set);
}

// One guide pair: what VamDlssNr has (read) and the copy the network is given (written). A null
// source lets the pair go.
void RegisterGuide(const VwsCmd& cmd)
{
    Set& set = g_sets[cmd.set];
    const uint32_t i = cmd.which;

    if (i >= kGuides)
        return;

    const uint32_t registrations = set.status.registrations + 1;
    int error = ErrNone;
    HRESULT hr = S_OK;

    if (cmd.frame != nullptr)
    {
        ID3D11Device* device = DeviceOf(cmd.frame);

        if (device == nullptr)
        {
            Log("register %u: guide %u's source is not a D3D11 texture", cmd.set, i);
            error = ErrBadArgs;
        }
        else
        {
            hr = EnsureDevice(device);
            device->Release();

            if (FAILED(hr))
                error = ErrDeviceFailed;
        }
    }

    set.guideSrc[i].Release();
    set.guideDst[i].Release();

    if (cmd.frame != nullptr && error == ErrNone)
    {
        error = OpenSurface(set.guideSrc[i], cmd.frame, false, true, false, "guide source", &hr);

        if (error == ErrNone)
            error = OpenSurface(set.guideDst[i], cmd.proxy, false, false, true, "guide", &hr);

        if (error != ErrNone)
        {
            set.guideSrc[i].Release();
            set.guideDst[i].Release();
        }
    }

    // Written last, for the reason Register gives.
    set.status.registrations = registrations;
    set.status.token = cmd.token;
    set.status.error = error;
    set.status.lastHr = (int32_t) hr;
    set.Refresh();

    if (error == ErrNone && cmd.frame != nullptr)
    {
        Log("set %u: guide %u registered -- %ux%u %s cut to %ux%u %s", cmd.set, i, set.guideSrc[i].desc.Width,
            set.guideSrc[i].desc.Height, FormatName(set.guideSrc[i].desc.Format), set.guideDst[i].desc.Width,
            set.guideDst[i].desc.Height, FormatName(set.guideDst[i].desc.Format));
    }

    Publish(cmd.set);
}

// Everything the passes change on the context, put back exactly as it was found.
//
// Unity keeps its own picture of what is bound and skips calls it believes are redundant, so a
// plugin that leaves state behind corrupts draws that have nothing to do with it. The hull, domain
// and geometry stages are cleared for the draw and restored after: VaM tessellates skin and builds
// hair in a geometry shader, and either left bound would swallow a three-vertex triangle.
struct StateBackup
{
    UINT viewportCount = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
    D3D11_VIEWPORT viewports[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE] {};
    UINT scissorCount = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE;
    D3D11_RECT scissors[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE] {};
    ID3D11RasterizerState* raster = nullptr;
    ID3D11BlendState* blend = nullptr;
    FLOAT blendFactor[4] {};
    UINT sampleMask = 0;
    ID3D11DepthStencilState* depth = nullptr;
    UINT stencilRef = 0;
    ID3D11RenderTargetView* rtvs[D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT] {};
    ID3D11DepthStencilView* dsv = nullptr;
    ID3D11ShaderResourceView* srvs[3] {};
    ID3D11SamplerState* sampler = nullptr;
    ID3D11Buffer* cb = nullptr;
    ID3D11VertexShader* vs = nullptr;
    ID3D11PixelShader* ps = nullptr;
    ID3D11GeometryShader* gs = nullptr;
    ID3D11HullShader* hs = nullptr;
    ID3D11DomainShader* ds = nullptr;
    ID3D11InputLayout* layout = nullptr;
    D3D11_PRIMITIVE_TOPOLOGY topology = D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED;

    void Capture(ID3D11DeviceContext* c)
    {
        c->RSGetViewports(&viewportCount, viewports);
        c->RSGetScissorRects(&scissorCount, scissors);
        c->RSGetState(&raster);
        c->OMGetBlendState(&blend, blendFactor, &sampleMask);
        c->OMGetDepthStencilState(&depth, &stencilRef);
        c->OMGetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, rtvs, &dsv);
        c->PSGetShaderResources(0, 3, srvs);
        c->PSGetSamplers(0, 1, &sampler);
        c->PSGetConstantBuffers(0, 1, &cb);
        c->VSGetShader(&vs, nullptr, nullptr);
        c->PSGetShader(&ps, nullptr, nullptr);
        c->GSGetShader(&gs, nullptr, nullptr);
        c->HSGetShader(&hs, nullptr, nullptr);
        c->DSGetShader(&ds, nullptr, nullptr);
        c->IAGetInputLayout(&layout);
        c->IAGetPrimitiveTopology(&topology);
    }

    void Restore(ID3D11DeviceContext* c)
    {
        // Our views come off first: a texture cannot be put back as a render target while it is
        // still bound as an input, and the same texture can be either from one frame to the next.
        ID3D11ShaderResourceView* none[3] {};
        c->PSSetShaderResources(0, 3, none);

        c->OMSetRenderTargets(D3D11_SIMULTANEOUS_RENDER_TARGET_COUNT, rtvs, dsv);
        c->PSSetShaderResources(0, 3, srvs);
        c->PSSetSamplers(0, 1, &sampler);
        c->PSSetConstantBuffers(0, 1, &cb);
        c->VSSetShader(vs, nullptr, 0);
        c->PSSetShader(ps, nullptr, 0);
        c->GSSetShader(gs, nullptr, 0);
        c->HSSetShader(hs, nullptr, 0);
        c->DSSetShader(ds, nullptr, 0);
        c->IASetInputLayout(layout);
        c->IASetPrimitiveTopology(topology);
        c->RSSetState(raster);
        c->OMSetBlendState(blend, blendFactor, sampleMask);
        c->OMSetDepthStencilState(depth, stencilRef);
        c->RSSetViewports(viewportCount, viewports);
        c->RSSetScissorRects(scissorCount, scissors);

        SafeRelease(raster);
        SafeRelease(blend);
        SafeRelease(depth);

        for (auto& r : rtvs)
            SafeRelease(r);

        SafeRelease(dsv);

        for (auto& s : srvs)
            SafeRelease(s);

        SafeRelease(sampler);
        SafeRelease(cb);
        SafeRelease(vs);
        SafeRelease(ps);
        SafeRelease(gs);
        SafeRelease(hs);
        SafeRelease(ds);
        SafeRelease(layout);
    }
};

void SetSize(float out[4], uint32_t w, uint32_t h)
{
    out[0] = (float) w;
    out[1] = (float) h;
    out[2] = 1.0f / (float) w;
    out[3] = 1.0f / (float) h;
}

// Whether this pass may run on this set as it stands. A set that is merely incomplete is skipped
// quietly -- one half is registered before the other, every time. A set that is complete and does
// not fit together is an error, reported once per registration.
bool PassAllowed(uint32_t index, Set& set, Pass pass, uint32_t guide, uint32_t eyes)
{
    const bool complete = pass == PassResolve ? set.Complete() : (pass == PassDown ? set.CanDownsample() : set.CanGuide(guide));

    if (!complete || g_ctx == nullptr)
        return false;

    const char* why = nullptr;

    if (pass == PassGuide)
    {
        if (eyes == 2 && ((set.guideSrc[guide].desc.Width & 1) != 0 || (set.guideDst[guide].desc.Width & 1) != 0))
            why = "a double-wide guide needs even widths";
    }
    else if (pass == PassResolve && !set.Paired())
        why = "the model's input and output, or the frame and the result, differ in size";
    else if (eyes == 2 && ((set.frame.desc.Width & 1) != 0 || (set.proxy.desc.Width & 1) != 0))
        why = "a double-wide frame needs even widths";

    if (why == nullptr)
        return true;

    set.status.error = ErrBadShape;

    if (!set.shapeReported)
    {
        set.shapeReported = true;
        Log("set %u: %s refused -- %s (frame %ux%u, result %ux%u, model in %ux%u out %ux%u)", index,
            pass == PassResolve ? "resolve" : (pass == PassDown ? "downsample" : "guide"), why, set.frame.desc.Width,
            set.frame.desc.Height, set.result.desc.Width, set.result.desc.Height, set.proxy.desc.Width,
            set.proxy.desc.Height, set.model.desc.Width, set.model.desc.Height);
    }

    return false;
}

void DrawPass(Set& set, Pass pass, const VwsCmd& cmd)
{
    ID3D11DeviceContext* c = g_ctx;
    const bool resolve = pass == PassResolve;
    const bool guide = pass == PassGuide;
    Surface& target = resolve ? set.result : (guide ? set.guideDst[cmd.which] : set.proxy);
    Surface& source = guide ? set.guideSrc[cmd.which] : set.frame;
    Surface& work = guide ? target : set.proxy;

    Params params {};
    SetSize(params.frameSize, source.desc.Width, source.desc.Height);
    SetSize(params.dstSize, target.desc.Width, target.desc.Height);
    SetSize(params.workSize, work.desc.Width, work.desc.Height);
    params.eyes = cmd.eyes == 2 ? 2u : 1u;
    params.strength = cmd.strength;
    params.mode = cmd.mode;
    params.flags = guide ? 0u
                         : ((set.frame.Encoded() ? kFlagFrameEncoded : 0) | (set.proxy.Encoded() ? kFlagProxyEncoded : 0) |
                            (set.model.Encoded() ? kFlagModelEncoded : 0) | (target.Encoded() ? kFlagOutEncoded : 0));

    // The whole of each eye unless the command brought a window.
    for (int e = 0; e < 2; ++e)
    {
        params.window[e][0] = params.window[e][1] = 0.0f;
        params.window[e][2] = params.window[e][3] = 1.0f;
    }

    if (cmd.windowed != 0)
    {
        memcpy(params.window, cmd.window, sizeof(params.window));
        params.fade[0] = cmd.feather;
        params.flags |= kFlagWindow;
    }

    // Only a window can have moved, and only motion vectors care.
    if (guide && cmd.windowed != 0 && cmd.moved != 0)
    {
        memcpy(params.prev, cmd.prev, sizeof(params.prev));
        params.flags |= kFlagMoved | (cmd.topDown != 0 ? kFlagTopDown : 0);
    }

    D3D11_MAPPED_SUBRESOURCE mapped {};
    const HRESULT hr = c->Map(g_cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);

    if (FAILED(hr))
    {
        set.status.lastHr = (int32_t) hr;
        set.status.skipped++;
        return;
    }

    memcpy(mapped.pData, &params, sizeof(params));
    c->Unmap(g_cb, 0);

    StateBackup backup;
    backup.Capture(c);

    // Inputs off, target on, inputs back on -- in that order, for the reason Restore gives.
    ID3D11ShaderResourceView* none[3] {};
    c->PSSetShaderResources(0, 3, none);
    c->OMSetRenderTargets(1, &target.rtv, nullptr);

    D3D11_VIEWPORT vp {};
    vp.Width = (float) target.desc.Width;
    vp.Height = (float) target.desc.Height;
    vp.MaxDepth = 1.0f;
    c->RSSetViewports(1, &vp);
    c->RSSetState(g_raster);
    c->OMSetBlendState(g_blend, nullptr, 0xFFFFFFFF);
    c->OMSetDepthStencilState(g_depth, 0);

    c->IASetInputLayout(nullptr);
    c->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    c->VSSetShader(g_vs, nullptr, 0);
    c->HSSetShader(nullptr, nullptr, 0);
    c->DSSetShader(nullptr, nullptr, 0);
    c->GSSetShader(nullptr, nullptr, 0);
    c->PSSetShader(resolve ? g_psResolve : (guide ? g_psGuide : g_psDown), nullptr, 0);
    c->PSSetConstantBuffers(0, 1, &g_cb);
    c->PSSetSamplers(0, 1, &g_sampler);

    ID3D11ShaderResourceView* inputs[3] = { source.srv, resolve ? set.proxy.srv : nullptr,
                                            resolve ? set.model.srv : nullptr };
    c->PSSetShaderResources(0, 3, inputs);

    c->Draw(3, 0);

    backup.Restore(c);

    if (resolve)
        set.status.resolves++;
    else if (!guide)
        set.status.downsamples++;
}

void Execute(const VwsCmd& cmd)
{
    if (cmd.op == OpReleaseAll)
    {
        ReleaseDevice();

        AcquireSRWLockExclusive(&g_lock);
        memset(g_published, 0, sizeof(g_published));
        ReleaseSRWLockExclusive(&g_lock);
        return;
    }

    if (cmd.set >= kSets)
        return;

    Set& set = g_sets[cmd.set];

    switch (cmd.op)
    {
    case OpRegister:
        Register(cmd);
        break;

    case OpRelease:
        set.Release();
        Publish(cmd.set);
        break;

    case OpRegisterGuide:
        RegisterGuide(cmd);
        break;

    case OpDownsample:
    case OpResolve:
    case OpGuide:
    {
        const Pass pass = cmd.op == OpResolve ? PassResolve : (cmd.op == OpGuide ? PassGuide : PassDown);

        if (PassAllowed(cmd.set, set, pass, cmd.which, cmd.eyes))
            DrawPass(set, pass, cmd);
        else
            set.status.skipped++;

        Publish(cmd.set);
        break;
    }

    default:
        break;
    }
}

void __stdcall OnRenderEvent(int eventId)
{
    if ((eventId & kEventMask) != kEventMagic)
        return;

    VwsCmd cmd;

    AcquireSRWLockExclusive(&g_lock);
    cmd = g_cmds[eventId & (kSlots - 1)];
    g_events++;
    ReleaseSRWLockExclusive(&g_lock);

    Execute(cmd);
}

int Push(const VwsCmd& cmd)
{
    AcquireSRWLockExclusive(&g_lock);
    const uint32_t slot = g_nextSlot++ & (kSlots - 1);
    g_cmds[slot] = cmd;
    ReleaseSRWLockExclusive(&g_lock);

    return kEventMagic | (int) slot;
}
} // namespace

VWS_EXPORT uint32_t vws_abi()
{
    return kAbi;
}

VWS_EXPORT void* vws_event_func()
{
    return (void*) &OnRenderEvent;
}

// Every vws_push* copies a command into a slot and returns the event id that names it, for
// GL.IssuePluginEvent. Main thread; none of them touches a D3D11 object.
VWS_EXPORT int vws_push(const VwsCmd* cmd)
{
    return cmd != nullptr ? Push(*cmd) : -1;
}

VWS_EXPORT int vws_push_register(uint32_t set, void* frame, void* proxy, void* model, void* result, uint32_t which,
                                 uint32_t srgbMask, uint32_t token)
{
    VwsCmd cmd {};
    cmd.op = OpRegister;
    cmd.set = set;
    cmd.frame = frame;
    cmd.proxy = proxy;
    cmd.model = model;
    cmd.result = result;
    cmd.which = which;
    cmd.srgbMask = srgbMask;
    cmd.token = token;
    return Push(cmd);
}

VWS_EXPORT int vws_push_pass(uint32_t resolve, uint32_t set, uint32_t eyes, float strength, uint32_t mode)
{
    VwsCmd cmd {};
    cmd.op = resolve != 0 ? OpResolve : OpDownsample;
    cmd.set = set;
    cmd.eyes = eyes;
    cmd.strength = strength;
    cmd.mode = mode;
    return Push(cmd);
}

// The same two passes with the model working on a window of each eye: `window` is eight numbers,
// origin and size per eye as fractions of the eye, or null for the whole of it.
VWS_EXPORT int vws_push_pass_window(uint32_t resolve, uint32_t set, uint32_t eyes, float strength, uint32_t mode,
                                    const float* window, float feather)
{
    VwsCmd cmd {};
    cmd.op = resolve != 0 ? OpResolve : OpDownsample;
    cmd.set = set;
    cmd.eyes = eyes;
    cmd.strength = strength;
    cmd.mode = mode;

    if (window != nullptr)
    {
        memcpy(cmd.window, window, sizeof(cmd.window));
        cmd.feather = feather;
        cmd.windowed = 1;
    }

    return Push(cmd);
}

// One of the model's guides: the texture VamDlssNr holds and the copy the network is given. A null
// source lets that guide's pair go.
VWS_EXPORT int vws_push_register_guide(uint32_t set, uint32_t index, void* source, void* copy, uint32_t token)
{
    VwsCmd cmd {};
    cmd.op = OpRegisterGuide;
    cmd.set = set;
    cmd.which = index;
    cmd.frame = source;
    cmd.proxy = copy;
    cmd.token = token;
    return Push(cmd);
}

// Cuts that guide to the window (the whole of each eye when `window` is null). `previous`, eight
// numbers like `window` or null, is the window as it was a frame ago: given for the motion vectors
// of a window that has moved or changed size, which are then re-expressed for it (see PSGuide).
// `topDown` says the textures' first row is the top of the picture.
VWS_EXPORT int vws_push_guide(uint32_t set, uint32_t index, uint32_t eyes, const float* window, const float* previous,
                              uint32_t topDown)
{
    VwsCmd cmd {};
    cmd.op = OpGuide;
    cmd.set = set;
    cmd.which = index;
    cmd.eyes = eyes;

    if (window != nullptr)
    {
        memcpy(cmd.window, window, sizeof(cmd.window));
        cmd.windowed = 1;
    }

    if (window != nullptr && previous != nullptr)
    {
        memcpy(cmd.prev, previous, sizeof(cmd.prev));
        cmd.moved = 1;
        cmd.topDown = topDown;
    }

    return Push(cmd);
}

// set >= kSets releases everything, the device objects included.
VWS_EXPORT int vws_push_release(uint32_t set)
{
    VwsCmd cmd {};
    cmd.op = set < kSets ? OpRelease : OpReleaseAll;
    cmd.set = set < kSets ? set : 0;
    return Push(cmd);
}

VWS_EXPORT void vws_status(uint32_t set, VwsStatus* out)
{
    if (out == nullptr)
        return;

    if (set >= kSets)
    {
        *out = {};
        return;
    }

    AcquireSRWLockShared(&g_lock);
    *out = g_published[set];
    ReleaseSRWLockShared(&g_lock);
}

// The four numbers the managed side steers by, without a struct to marshal.
VWS_EXPORT int vws_poll(uint32_t set, int* ready, int* error, uint32_t* token, int* lastHr)
{
    VwsStatus s {};
    vws_status(set, &s);

    if (ready != nullptr)
        *ready = s.ready;

    if (error != nullptr)
        *error = s.error;

    if (token != nullptr)
        *token = s.token;

    if (lastHr != nullptr)
        *lastHr = s.lastHr;

    return set < kSets ? 1 : 0;
}

VWS_EXPORT uint32_t vws_events()
{
    AcquireSRWLockShared(&g_lock);
    const uint32_t n = g_events;
    ReleaseSRWLockShared(&g_lock);
    return n;
}

// Hands over whatever the render thread has logged since the last call, as newline-separated text.
VWS_EXPORT int vws_drain_log(char* buffer, int capacity)
{
    if (buffer == nullptr || capacity <= 1)
        return 0;

    AcquireSRWLockExclusive(&g_lock);
    const size_t n = g_logLen < (size_t) (capacity - 1) ? g_logLen : (size_t) (capacity - 1);
    memcpy(buffer, g_log, n);
    buffer[n] = '\0';
    memmove(g_log, g_log + n, g_logLen - n);
    g_logLen -= n;
    ReleaseSRWLockExclusive(&g_lock);

    return (int) n;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
        DisableThreadLibraryCalls(module);

    return TRUE;
}
