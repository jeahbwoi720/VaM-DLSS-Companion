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
#include <dxgi.h>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cmath>

#include "vws_vs.h"
#include "vws_ps_down.h"
#include "vws_ps_resolve.h"
#include "vws_ps_guide.h"
#include "vws_ps_sharpen.h"
#include "vws_ps_pass.h"
#include "vws_ps_matte.h"
#include "vws_ps_depthfill.h"
#include "vws_ps_steady.h"
#include "vws_ps_gather.h"
#include "vws_ps_overlay.h"
#include "vws_vs_overlay.h"
#include "vws_depth.h"
#include "vws_hand.h"

// NVIDIA's own interface to its driver, for variable rate shading (see "foveated shading" below).
// Its header is not written for /W4.
#pragma warning(push, 0)
#include "deps/nvapi/nvapi.h"
#pragma warning(pop)

#define VWS_EXPORT extern "C" __declspec(dllexport)

namespace
{
const uint32_t kAbi = 32;
const int kEventMagic = 0x57530000; // 'WS'
const int kEventFovea = 0x57460000; // 'WF': low byte 1 = foveated shading on for what is drawn next, 2 = off;
                                    // bits 8-11 which camera (0 the scene's), bit 12 its opaque pass
const int kEventMask = 0x7FFF0000;
const int kSlots = 64;
const uint32_t kSets = 16;
const uint32_t kGuides = 3;
const uint32_t kSteadySlots = 4; // the model's passes over one frame whose edits are each kept over frames

enum Op : uint32_t
{
    OpRegister = 1,
    OpDownsample = 2,
    OpResolve = 3,
    OpRelease = 4,
    OpReleaseAll = 5,
    OpRegisterGuide = 6, // `which` = which guide; frame = its source, proxy = the model's copy
    OpGuide = 7,         // `which` = which guide
    OpSharpen = 8,       // frame = the texture to sharpen where it lies; strength; eyes
    OpPassthrough = 9,   // frame = the texture whose key colour becomes the camera's picture; see Passthrough
    OpMatte = 10,        // frame = the texture whose key colour is written, as a matte, for the overlay; see Matte
    OpDepthFill = 11,    // frame = the scene's depth; window[0..3] = the part of it; strength = slack; topDown; mode; see DepthFill
    OpSteady = 12,       // frame = the model-size motion vectors; which = the pass over this frame; strength = how much of
                         // the new edit is taken; mode 1 = begin anew; eyes, window, topDown; see Steady
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
const uint32_t kFlagSquash = 128;
const uint32_t kFlagFollow = 256;
const uint32_t kFlagSteady = 512;
const uint32_t kFlagFull = 1024;
const uint32_t kFlagOutline = 2048;

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

    // The model's edit kept over frames (see Steady): for each of its passes over a frame two
    // textures of the model's size, written turn and turn about; which of the two holds the
    // latest; whether anything is in them yet; and the pass whose kept edit the next resolve reads.
    Surface kept[kSteadySlots][2];
    uint32_t keptAt[kSteadySlots] {};
    bool keptPrimed[kSteadySlots] {};
    int steadyReady = -1;
    bool steadyFull = false;    // ...which is kept at the frame's size (PSGather), not the model's

    void ReleaseKept()
    {
        for (uint32_t s = 0; s < kSteadySlots; ++s)
        {
            kept[s][0].Release();
            kept[s][1].Release();
            keptAt[s] = 0;
            keptPrimed[s] = false;
        }

        steadyReady = -1;
    }

    void Release()
    {
        ReleaseKept();
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
ID3D11PixelShader* g_psSharpen = nullptr;
ID3D11PixelShader* g_psPass = nullptr;
ID3D11PixelShader* g_psMatte = nullptr;
ID3D11PixelShader* g_psDepthFill = nullptr;
ID3D11PixelShader* g_psSteady = nullptr;
ID3D11PixelShader* g_psGather = nullptr;
float g_downShift[2] = {}; // the shift the next shrink is to be made with (main thread: set, then taken by the push)
volatile LONG g_steadyRuns = 0, g_steadyFresh = 0, g_steadyFailed = 0;
ID3D11DepthStencilState* g_depthWrite = nullptr; // every pixel's depth written, whatever is there
ID3D11BlendState* g_blendNone = nullptr;         // and no colour
void* g_fillRefused = nullptr;                   // a depth texture the fill could not open, so it is not asked again
volatile LONG g_fillDone = 0, g_fillNoTarget = 0, g_fillFailed = 0;
ID3D11Buffer* g_cbPass = nullptr;
void* g_sharpenRefused = nullptr; // a texture the sharpening pass could not open, so it is not asked again

// How a smaller model's edit is enlarged by the resolve (see EditFollowing in vws.hlsl): set from the
// main thread, read where the pass is drawn. Plain numbers; a frame drawn with the old ones is no harm.
volatile LONG g_resolveFollow = 0, g_resolveSaid = -1, g_resolveOutline = 0;
float g_resolveEdge = 0.08f, g_resolveSharpen = 0.0f, g_resolveHalo = 0.5f;
float g_downSharpen = 0.0f; // how much sharper than its exact average the model's input is made (see PSDown)
ID3D11SamplerState* g_sampler = nullptr;
ID3D11Buffer* g_cb = nullptr;
ID3D11RasterizerState* g_raster = nullptr;
ID3D11BlendState* g_blend = nullptr;
ID3D11DepthStencilState* g_depth = nullptr;
Set g_sets[kSets];
Surface g_scratch; // the sharpening pass reads a copy of what it writes

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

// ---- the headset's camera, for passthrough -------------------------------------------------------
//
// A worker thread asks SteamVR for the camera's newest frame and keeps one grey copy of it; the
// render thread uploads that copy when it draws. The numbers the pass needs -- the key colour, the
// lens, where the cameras sit in the head -- come from the managed side as one block of floats.
typedef int(__stdcall* CamFrameFn)(uint64_t handle, int frameType, void* buffer, uint32_t bufferSize, void* header,
                                   uint32_t headerSize);

const uint32_t kCamFloats = 96;

enum CamField : uint32_t
{
    kCfgKey = 0,        // 3: the key colour, sRGB-encoded 0..1
    kCfgTolerance = 3,  // how far from it still counts as key
    kCfgSoftness = 4,   // ...and how far beyond that the edge fades
    kCfgGain = 5,       // camera brightness
    kCfgDistance = 6,   // how far away the room is taken to be, metres
    kCfgFocal = 7,      // the fisheye's pixels per radian
    kCfgK = 8,          // 4: its polynomial
    kCfgCentre = 12,    // 4: its centre in each lens's half of the frame, pixels
    kCfgTan = 16,       // 8: each eye's left, right, top, bottom tangents (y down)
    kCfgCamToHead = 24, // 24: each camera in the head, 3x4
    kCfgEyeToHead = 48, // 24: each eye in the head, 3x4
    kCfgCompensate = 72, // 1: follow the head from the camera frame's moment to the picture's
    kCfgView = 73,      // 0 the picture, 1 the matte, 2 the camera everywhere
    kCfgMode = 74,      // 0 the room is drawn into the game's frame, 1 it is a SteamVR overlay of its own
    kCfgQuadTan = 75,   // overlay: how far the quad reaches to each side, as a tangent
    kCfgQuadDistance = 76, // overlay: how far in front of the head the quad stands, metres
    kCfgStereoRule = 77, // overlay: 0 SteamVR shapes a side-by-side overlay by one eye's picture, 1 by the whole texture
    kCfgSpace = 78,     // the tracking universe the camera's poses are in
    kCfgPoseIsCamera = 79, // 1: the pose a camera frame comes with is the first camera's, not the head's
    kCfgDepth = 80,     // overlay: 1 the room's depth is worked out and set against the scene's
    kCfgDepthMargin = 81, // ...how much nearer the room must be to show in front, in 1/metres
    kCfgDepthSoft = 82, // ...over how much more it fades in
};

struct PassParams
{
    float dst[4];    // target width, height, 1/width, 1/height
    float key[4];    // rgb, tolerance
    float tune[4];   // softness, gain, distance, focal
    float k[4];
    float centre[4];
    float cam[4];    // camera frame width, height, lens width, view
    float tangents[4];
    float row0[4], row1[4], row2[4]; // a point in the eye's space -> the camera's
    float misc[4];   // eye, eyes in the target (1|2), target holds encoded values, first row at the top
};

SRWLOCK g_camLock = SRWLOCK_INIT;
float g_camConfig[kCamFloats] {};
bool g_camConfigured = false;
uint8_t* g_camFront = nullptr; // the newest frame, grey
uint8_t* g_camSpare = nullptr; // the render thread's
float g_camFrontPose[12] {};
bool g_camFrontPoseValid = false;
bool g_camFresh = false;
uint32_t g_camW = 0, g_camH = 0;
volatile LONG g_camGen = 0; // which start the camera thread belongs to: a thread of an earlier one ends itself
HANDLE g_camThread = nullptr;
CamFrameFn g_camFn = nullptr;
uint64_t g_camHandle = 0;
volatile LONG g_camFrames = 0, g_camUploads = 0, g_camError = 0;

// render thread only
ID3D11Texture2D* g_camTex = nullptr;
ID3D11ShaderResourceView* g_camSrv = nullptr;
float g_camPose[12] {};
bool g_camPoseValid = false;
bool g_camHave = false;
void* g_passRefused = nullptr;

// The game's side of the overlay: the matte, in a texture the overlay's device can open, and the
// head's pose as the frame it was cut from was rendered.
LUID g_gameAdapter {};
bool g_gameAdapterKnown = false;
HANDLE g_matteHandle = nullptr;
uint32_t g_matteSerial = 0;
float g_gamePose[12] {};
bool g_gamePoseValid = false;
volatile LONG g_matteFrames = 0;
const uint32_t kMatteW = 2048, kMatteH = 1024;
const uint32_t kMatteTexH = kMatteH + 8, kMattePoseRow = kMatteH + 4; // below the matte, the row its pose is written in

// render thread only
ID3D11Texture2D* g_matteTex = nullptr;
ID3D11RenderTargetView* g_matteRtv = nullptr;
void* g_matteRefused = nullptr;
void* g_depthRefused = nullptr;

// the overlay's own thread
volatile LONG g_ovState = 0, g_ovFrames = 0, g_ovError = 0;
volatile LONG g_ovDebugWant = 0;
volatile LONG g_ovDepthRoom = -1, g_ovDepthScene = -1; // straight ahead, 1/metres in thousandths; -1 not read
uint8_t* g_ovDebug = nullptr;
uint32_t g_ovDebugW = 0, g_ovDebugH = 0;

void ReleaseMatte()
{
    SafeRelease(g_matteRtv);
    SafeRelease(g_matteTex);
    g_matteRefused = nullptr;

    AcquireSRWLockExclusive(&g_camLock);
    g_matteHandle = nullptr;
    g_matteSerial++;
    g_gameAdapterKnown = false;
    ReleaseSRWLockExclusive(&g_camLock);
}

void ReleaseCamera()
{
    SafeRelease(g_camSrv);
    SafeRelease(g_camTex);
    g_camHave = false;
    g_passRefused = nullptr;
}

void FoveaRelease();

void ReleaseDevice()
{
    for (Set& s : g_sets)
        s.Release();

    FoveaRelease();

    SafeRelease(g_depth);
    SafeRelease(g_blend);
    SafeRelease(g_raster);
    SafeRelease(g_cb);
    SafeRelease(g_sampler);
    g_scratch.Release();
    g_sharpenRefused = nullptr;
    ReleaseCamera();
    ReleaseMatte();
    SafeRelease(g_cbPass);
    SafeRelease(g_psMatte);
    SafeRelease(g_psDepthFill);
    SafeRelease(g_psSteady);
    SafeRelease(g_psGather);
    SafeRelease(g_depthWrite);
    SafeRelease(g_blendNone);
    g_fillRefused = nullptr;
    SafeRelease(g_psPass);
    SafeRelease(g_psSharpen);
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
        hr = g_device->CreatePixelShader(g_vwsPsSharpen, sizeof(g_vwsPsSharpen), nullptr, &g_psSharpen);

    if (SUCCEEDED(hr))
        hr = g_device->CreatePixelShader(g_vwsPsPass, sizeof(g_vwsPsPass), nullptr, &g_psPass);

    if (SUCCEEDED(hr))
        hr = g_device->CreatePixelShader(g_vwsPsMatte, sizeof(g_vwsPsMatte), nullptr, &g_psMatte);

    if (SUCCEEDED(hr))
        hr = g_device->CreatePixelShader(g_vwsPsDepthFill, sizeof(g_vwsPsDepthFill), nullptr, &g_psDepthFill);

    if (SUCCEEDED(hr))
        hr = g_device->CreatePixelShader(g_vwsPsSteady, sizeof(g_vwsPsSteady), nullptr, &g_psSteady);

    if (SUCCEEDED(hr))
        hr = g_device->CreatePixelShader(g_vwsPsGather, sizeof(g_vwsPsGather), nullptr, &g_psGather);

    // Which adapter this is: the overlay's own device has to be on the same one to share a texture.
    if (SUCCEEDED(hr))
    {
        IDXGIDevice* dxgi = nullptr;
        IDXGIAdapter* adapter = nullptr;
        DXGI_ADAPTER_DESC ad {};

        if (SUCCEEDED(g_device->QueryInterface(__uuidof(IDXGIDevice), (void**) &dxgi)) && SUCCEEDED(dxgi->GetAdapter(&adapter)) &&
            SUCCEEDED(adapter->GetDesc(&ad)))
        {
            AcquireSRWLockExclusive(&g_camLock);
            g_gameAdapter = ad.AdapterLuid;
            g_gameAdapterKnown = true;
            ReleaseSRWLockExclusive(&g_camLock);
        }

        SafeRelease(adapter);
        SafeRelease(dxgi);
    }

    if (SUCCEEDED(hr))
    {
        D3D11_BUFFER_DESC bd {};
        bd.ByteWidth = sizeof(PassParams);
        bd.Usage = D3D11_USAGE_DYNAMIC;
        bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        hr = g_device->CreateBuffer(&bd, nullptr, &g_cbPass);
    }

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

    if (SUCCEEDED(hr))
    {
        D3D11_DEPTH_STENCIL_DESC dd {};
        dd.DepthEnable = TRUE;
        dd.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL;
        dd.DepthFunc = D3D11_COMPARISON_ALWAYS;
        dd.StencilEnable = FALSE;
        hr = g_device->CreateDepthStencilState(&dd, &g_depthWrite);
    }

    if (SUCCEEDED(hr))
    {
        D3D11_BLEND_DESC bd {};
        bd.RenderTarget[0].BlendEnable = FALSE;
        bd.RenderTarget[0].RenderTargetWriteMask = 0;
        hr = g_device->CreateBlendState(&bd, &g_blendNone);
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

// ---- foveated shading -----------------------------------------------------------------------------
//
// An eye sees detail only where it is looking, and a headset's lens is sharp only near its middle.
// NVIDIA's cards can be told to run the pixel shader once for a block of two or four pixels instead
// of once a pixel, tile by tile of the render target ("variable rate shading"): edges and depth
// stay at full resolution, only the shading inside surfaces gets coarser. So: full rate where each
// eye looks, coarser in a ring around that, coarsest beyond.
//
// The game's camera is told to raise two events of ours around its own geometry -- on before the
// opaque and the transparent pass, off after each (Foveation.cs). Nothing else is to be shaded
// coarsely: not the shadow maps drawn before, nor the image effects, DLSS, the menu or the
// compositor's copy after. Each "on" looks at the render target bound at that moment, makes the
// map of rates for its size, and sets it; "off" takes it away.
const uint32_t kFovFloats = 16;

enum FovField : uint32_t
{
    kFovOn = 0,       // 1: wanted
    kFovCentre = 1,   // 4: where each eye looks in its own picture: left u, v, right u, v; v up from the bottom
    kFovInner = 5,    // full rate within this of it, as a share of the picture's height
    kFovOuter = 6,    // the coarsest rate beyond this
    kFovStrong = 7,   // 1: the coarsest rate is as coarse as the target's anti-aliasing allows, else as the ring's
    kFovShow = 8,     // 1: beyond the outer radius nothing is shaded at all -- to see where the zones are
    kFovTopDown = 9,  // 1: the target's first row is the top of the picture
    kFovWide = 10,    // the size of the target the scene is drawn into, as the game knows it (0: whatever is bound)
    kFovHigh = 11,
};

SRWLOCK g_fovLock = SRWLOCK_INIT;
float g_fovCfg[kFovFloats] {};
void* g_fovAnyTexture = nullptr; // a texture of the game's device: the way to that device
volatile LONG g_fovState = 0, g_fovW = 0, g_fovH = 0, g_fovSamples = 0, g_fovCoarse = 0, g_fovOns = 0;
volatile LONG g_fovAsked = 0, g_fovNoDevice = 0;
// What it does, measured: the pixel shader's runs over the scene camera's opaque pass [0] and its
// transparent pass [1] (where VaM draws people), with the rates in force [.][0] and, every so often
// for one frame, without [.][1]; and how often each has been counted.
volatile LONG64 g_fovPs[2][2] = {};
volatile LONG g_fovPsTimes[2][2] = {};
volatile LONG g_fovNoTarget = 0, g_fovSmall = 0, g_fovByDepth = 0; // asked for and not done: nothing bound, a small target; done by the depth target's size

// The render thread's.
struct Fovea
{
    ID3D11Device* askedOf = nullptr; // the device NVAPI was last asked about
    bool able = false;
    bool set = false;                // the context has our rates on it
    ID3D11Texture2D* map = nullptr;
    ID3D11NvShadingRateResourceView* view = nullptr;
    uint32_t w = 0, h = 0, samples = 0, tilesW = 0, tilesH = 0;
    float made[kFovFloats] {};
    uint8_t* tiles = nullptr;
    // the measuring: a pipeline-statistics query round each of the scene camera's two passes
    struct Meter
    {
        ID3D11Query* query = nullptr;
        int stage = 0;        // 0 free, 1 begun, 2 ended and waiting to be read
        bool control = false; // the pass being measured is drawn without the rates
    } meter[2];
    bool frameMeasure = false, frameControl = false; // decided at the opaque pass, for the frame
    uint32_t passes = 0, measures = 0;
} g_fov;

void FoveaRelease()
{
    SafeRelease(g_fov.view);
    SafeRelease(g_fov.map);
    for (Fovea::Meter& m : g_fov.meter)
    {
        SafeRelease(m.query);
        m.stage = 0;
    }

    g_fov.frameMeasure = g_fov.frameControl = false;
    delete[] g_fov.tiles;
    g_fov.tiles = nullptr;
    g_fov.w = g_fov.h = 0;
    g_fov.askedOf = nullptr;
    g_fov.able = false;
    g_fov.set = false;
}

void FoveaOff()
{
    for (Fovea::Meter& m : g_fov.meter)
    {
        if (g_ctx != nullptr && m.stage == 1 && m.query != nullptr)
        {
            g_ctx->End(m.query);
            m.stage = 2;
        }
    }

    if (!g_fov.set || g_ctx == nullptr)
        return;

    // Off for the viewport it was put on, by name: the rate table back to one shading a pixel.
    NV_D3D11_VIEWPORT_SHADING_RATE_DESC plain[NV_MAX_NUM_VIEWPORTS];

    for (NV_D3D11_VIEWPORT_SHADING_RATE_DESC& p : plain)
    {
        p = NV_D3D11_VIEWPORT_SHADING_RATE_DESC {};
        p.enableVariablePixelShadingRate = false;

        for (NV_PIXEL_SHADING_RATE& r : p.shadingRateTable)
            r = NV_PIXEL_X1_PER_RASTER_PIXEL;
    }

    NV_D3D11_VIEWPORTS_SHADING_RATE_DESC none {};
    none.version = NV_D3D11_VIEWPORTS_SHADING_RATE_DESC_VER;
    none.numViewports = NV_MAX_NUM_VIEWPORTS;
    none.pViewports = plain;
    NvAPI_D3D11_RSSetViewportsPixelShadingRates(g_ctx, &none);
    NvAPI_D3D11_RSSetShadingRateResourceView(g_ctx, nullptr);
    g_fov.set = false;
}

void FoveaOn(uint32_t camera, bool opaque)
{
    float cfg[kFovFloats];
    AcquireSRWLockShared(&g_fovLock);
    memcpy(cfg, g_fovCfg, sizeof(cfg));
    void* any = g_fovAnyTexture;
    ReleaseSRWLockShared(&g_fovLock);

    InterlockedIncrement(&g_fovAsked);

    if (cfg[kFovOn] < 0.5f)
    {
        FoveaOff();
        InterlockedExchange(&g_fovState, 0);
        return;
    }

    ID3D11Device* device = DeviceOf(any);

    if (device == nullptr)
    {
        InterlockedIncrement(&g_fovNoDevice);
        return;
    }

    const HRESULT ready = EnsureDevice(device);
    device->Release();

    if (FAILED(ready))
    {
        InterlockedIncrement(&g_fovNoDevice);
        return;
    }

    // Can this card do it? Asked once a device.
    if (g_fov.askedOf != g_device)
    {
        g_fov.askedOf = g_device;
        g_fov.able = false;
        static bool started = false, usable = false;

        if (!started)
        {
            started = true;
            usable = NvAPI_Initialize() == NVAPI_OK;
        }

        NV_D3D1x_GRAPHICS_CAPS caps {};

        if (usable && NvAPI_D3D1x_GetGraphicsCapabilities(g_device, NV_D3D1x_GRAPHICS_CAPS_VER, &caps) == NVAPI_OK)
            g_fov.able = caps.bVariablePixelRateShadingSupported != 0;

        Log("foveated shading: %s", g_fov.able ? "this card has variable rate shading" :
                                                 (usable ? "this card has no variable rate shading" : "no NVIDIA driver to ask (NVAPI)"));
    }

    if (!g_fov.able)
    {
        InterlockedExchange(&g_fovState, 2);
        return;
    }

    // What is being drawn into. Its size is all that is wanted of it, so where no colour target is
    // bound at this moment (a camera with a depth texture can be between its depth pass and its
    // picture) the depth target, which is the same size, does as well.
    ID3D11RenderTargetView* rtv = nullptr;
    ID3D11DepthStencilView* dsv = nullptr;
    g_ctx->OMGetRenderTargets(1, &rtv, &dsv);

    ID3D11Resource* resource = nullptr;
    ID3D11Texture2D* target = nullptr;

    if (rtv != nullptr)
    {
        rtv->GetResource(&resource);
    }
    else if (dsv != nullptr)
    {
        dsv->GetResource(&resource);
        InterlockedIncrement(&g_fovByDepth);
    }

    if (rtv) rtv->Release();
    if (dsv) dsv->Release();

    if (resource == nullptr)
    {
        InterlockedIncrement(&g_fovNoTarget);
        return;
    }

    if (resource != nullptr)
    {
        resource->QueryInterface(__uuidof(ID3D11Texture2D), (void**) &target);
        resource->Release();
    }

    if (target == nullptr)
        return;

    D3D11_TEXTURE2D_DESC td {};
    target->GetDesc(&td);
    target->Release();

    // The rates are laid over whatever is drawn into next, tile by tile, and the map has to be the
    // size of that target. What is bound at this moment is not always it: with DLSS upscaling in a
    // headset the scene is drawn into a smaller eye texture than the one still bound here, the map
    // was made for the wrong size, and nothing came of it (while at DLAA, where the two are the
    // same size, it worked). So the size the game gives for the scene's target is the one used.
    const uint32_t wantW = (uint32_t) cfg[kFovWide], wantH = (uint32_t) cfg[kFovHigh];

    if (wantW >= 256 && wantH >= 256 && (td.Width != wantW || td.Height != wantH))
    {
        static uint32_t saidW = 0, saidH = 0, saidWantW = 0;

        if (saidW != td.Width || saidH != td.Height || saidWantW != wantW)
        {
            saidW = td.Width;
            saidH = td.Height;
            saidWantW = wantW;
            Log("foveated shading: a %ux%u target is bound at its moment, the scene's is %ux%u: the rates are made for the scene's", td.Width, td.Height, wantW, wantH);
        }

        td.Width = wantW;
        td.Height = wantH;
        td.SampleDesc.Count = 1;
        InterlockedIncrement(&g_fovByDepth);
    }

    // (a shadow map, a probe: not the picture)
    if (td.Width < 256 || td.Height < 256)
    {
        InterlockedIncrement(&g_fovSmall);
        return;
    }

    const uint32_t tw = (td.Width + NV_VARIABLE_PIXEL_SHADING_TILE_WIDTH - 1) / NV_VARIABLE_PIXEL_SHADING_TILE_WIDTH;
    const uint32_t th = (td.Height + NV_VARIABLE_PIXEL_SHADING_TILE_HEIGHT - 1) / NV_VARIABLE_PIXEL_SHADING_TILE_HEIGHT;
    bool fresh = false;

    if (g_fov.map == nullptr || g_fov.w != td.Width || g_fov.h != td.Height)
    {
        SafeRelease(g_fov.view);
        SafeRelease(g_fov.map);
        delete[] g_fov.tiles;
        g_fov.tiles = new uint8_t[(size_t) tw * th];
        g_fov.w = td.Width;
        g_fov.h = td.Height;
        g_fov.tilesW = tw;
        g_fov.tilesH = th;

        D3D11_TEXTURE2D_DESC md {};
        md.Width = tw;
        md.Height = th;
        md.MipLevels = 1;
        md.ArraySize = 1;
        md.Format = DXGI_FORMAT_R8_UINT;
        md.SampleDesc.Count = 1;
        md.Usage = D3D11_USAGE_DEFAULT;
        md.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        HRESULT hr = g_device->CreateTexture2D(&md, nullptr, &g_fov.map);

        NV_D3D11_SHADING_RATE_RESOURCE_VIEW_DESC vd {};
        vd.version = NV_D3D11_SHADING_RATE_RESOURCE_VIEW_DESC_VER;
        vd.Format = DXGI_FORMAT_R8_UINT;
        vd.ViewDimension = NV_SRRV_DIMENSION_TEXTURE2D;
        vd.Texture2D.MipSlice = 0;

        Log("foveated shading: rate map for a %ux%u target (%u sample%s)", td.Width, td.Height, td.SampleDesc.Count, td.SampleDesc.Count == 1 ? "" : "s");

        if (FAILED(hr) || NvAPI_D3D11_CreateShadingRateResourceView(g_device, g_fov.map, &vd, &g_fov.view) != NVAPI_OK)
        {
            Log("foveated shading: no rate map for a %ux%u target (hr=0x%08X)", td.Width, td.Height, (unsigned) hr);
            SafeRelease(g_fov.view);
            SafeRelease(g_fov.map);
            g_fov.w = g_fov.h = 0;
            InterlockedExchange(&g_fovState, 3);
            return;
        }

        fresh = true;
    }

    g_fov.samples = td.SampleDesc.Count;

    if (fresh || memcmp(cfg, g_fov.made, sizeof(cfg)) != 0)
    {
        memcpy(g_fov.made, cfg, sizeof(cfg));

        // Two eyes side by side in one target, or one eye (or the monitor) in it.
        const bool pair = td.Width > td.Height + td.Height / 2;
        const float eyeW = pair ? td.Width * 0.5f : (float) td.Width, height = (float) td.Height;
        const float inner = cfg[kFovInner] * height, outer = cfg[kFovOuter] * height;
        const uint8_t last = cfg[kFovShow] > 0.5f ? 3 : 2;
        uint32_t coarse = 0;

        for (uint32_t y = 0; y < th; ++y)
        {
            const float py = (float) y * NV_VARIABLE_PIXEL_SHADING_TILE_HEIGHT + NV_VARIABLE_PIXEL_SHADING_TILE_HEIGHT * 0.5f;

            for (uint32_t x = 0; x < tw; ++x)
            {
                const float px = (float) x * NV_VARIABLE_PIXEL_SHADING_TILE_WIDTH + NV_VARIABLE_PIXEL_SHADING_TILE_WIDTH * 0.5f;
                const int eye = pair && px >= eyeW ? 1 : 0;
                const float u = cfg[kFovCentre + eye * 2], v = cfg[kFovCentre + eye * 2 + 1];
                const float dx = px - (eye * eyeW + u * eyeW), dy = py - (cfg[kFovTopDown] > 0.5f ? 1.0f - v : v) * height;
                const float away = sqrtf(dx * dx + dy * dy);
                const uint8_t rate = away < inner ? 0 : (away < outer ? 1 : last);
                g_fov.tiles[(size_t) y * tw + x] = rate;
                coarse += rate != 0 ? 1 : 0;
            }
        }

        g_ctx->UpdateSubresource(g_fov.map, 0, nullptr, g_fov.tiles, tw, 0);
        InterlockedExchange(&g_fovCoarse, (LONG) (coarse * 100 / (tw * th)));
    }

    // What each number in the map means. A coarse pixel may not hold more than sixteen samples,
    // so anti-aliasing limits how coarse it can be: 4x4 without, 2x2 at four samples a pixel.
    const uint32_t samples = td.SampleDesc.Count;
    const bool strong = cfg[kFovStrong] > 0.5f;
    NV_PIXEL_SHADING_RATE ring, beyond;

    if (samples >= 8)
    {
        ring = beyond = NV_PIXEL_X1_PER_2X1_RASTER_PIXELS;
    }
    else if (samples >= 4)
    {
        ring = NV_PIXEL_X1_PER_2X1_RASTER_PIXELS;
        beyond = strong ? NV_PIXEL_X1_PER_2X2_RASTER_PIXELS : ring;
    }
    else if (samples >= 2)
    {
        ring = NV_PIXEL_X1_PER_2X2_RASTER_PIXELS;
        beyond = strong ? NV_PIXEL_X1_PER_4X2_RASTER_PIXELS : ring;
    }
    else
    {
        ring = NV_PIXEL_X1_PER_2X2_RASTER_PIXELS;
        beyond = strong ? NV_PIXEL_X1_PER_4X4_RASTER_PIXELS : ring;
    }

    NV_D3D11_VIEWPORT_SHADING_RATE_DESC one {};
    one.enableVariablePixelShadingRate = true;

    for (NV_PIXEL_SHADING_RATE& r : one.shadingRateTable)
        r = NV_PIXEL_X1_PER_RASTER_PIXEL;

    one.shadingRateTable[1] = ring;
    one.shadingRateTable[2] = beyond;
    one.shadingRateTable[3] = NV_PIXEL_X0_CULL_RASTER_PIXELS;

    // The measuring. Every twentieth frame of the scene's camera has the pixel shader's runs
    // counted over its opaque pass and over its transparent pass (where VaM draws people), and one
    // in four of those frames is drawn WITHOUT the rates (not while the zones are being shown:
    // that one frame would fill the hole) -- the counts say what foveation saves here, in this
    // scene, at this size, and in which pass.
    bool measure = false, control = false;
    Fovea::Meter& meter = g_fov.meter[opaque ? 0 : 1];

    if (camera == 0)
    {
        if (meter.stage == 2 && meter.query != nullptr)
        {
            D3D11_QUERY_DATA_PIPELINE_STATISTICS counted {};

            if (g_ctx->GetData(meter.query, &counted, sizeof(counted), D3D11_ASYNC_GETDATA_DONOTFLUSH) == S_OK)
            {
                InterlockedExchange64(&g_fovPs[opaque ? 0 : 1][meter.control ? 1 : 0], (LONG64) counted.PSInvocations);
                InterlockedIncrement(&g_fovPsTimes[opaque ? 0 : 1][meter.control ? 1 : 0]);
                meter.stage = 0;
            }
        }

        if (opaque)
        {
            g_fov.frameMeasure = ++g_fov.passes % 20 == 0;
            g_fov.frameControl = g_fov.frameMeasure && cfg[kFovShow] < 0.5f && ++g_fov.measures % 4 == 0;
        }

        control = g_fov.frameControl;

        if (g_fov.frameMeasure && meter.stage == 0)
        {
            if (meter.query == nullptr)
            {
                D3D11_QUERY_DESC qd {};
                qd.Query = D3D11_QUERY_PIPELINE_STATISTICS;
                g_device->CreateQuery(&qd, &meter.query);
            }

            measure = meter.query != nullptr;
        }

        // (the transparent pass is the frame's last: what was decided for the frame ends with it)
        if (!opaque)
            g_fov.frameMeasure = g_fov.frameControl = false;
    }

    if (control)
    {
        g_fov.set = true;
        FoveaOff();

        if (measure)
        {
            meter.control = true;
            g_ctx->Begin(meter.query);
            meter.stage = 1;
        }

        return;
    }

    // The same for every viewport there can be: the game sets them as it goes.
    UINT count = NV_MAX_NUM_VIEWPORTS;
    NV_D3D11_VIEWPORT_SHADING_RATE_DESC all[NV_MAX_NUM_VIEWPORTS];

    for (UINT i = 0; i < count; ++i)
        all[i] = one;

    NV_D3D11_VIEWPORTS_SHADING_RATE_DESC desc {};
    desc.version = NV_D3D11_VIEWPORTS_SHADING_RATE_DESC_VER;
    desc.numViewports = count;
    desc.pViewports = all;

    if (NvAPI_D3D11_RSSetViewportsPixelShadingRates(g_ctx, &desc) != NVAPI_OK || NvAPI_D3D11_RSSetShadingRateResourceView(g_ctx, g_fov.view) != NVAPI_OK)
    {
        g_fov.set = true;
        FoveaOff();
        InterlockedExchange(&g_fovState, 3);
        return;
    }

    g_fov.set = true;

    if (measure)
    {
        meter.control = false;
        g_ctx->Begin(meter.query);
        meter.stage = 1;
    }

    InterlockedExchange(&g_fovState, 1);
    InterlockedExchange(&g_fovW, (LONG) td.Width);
    InterlockedExchange(&g_fovH, (LONG) td.Height);
    InterlockedExchange(&g_fovSamples, (LONG) samples);
    InterlockedIncrement(&g_fovOns);
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

    if (pass == PassDown)
    {
        params.fade[1] = g_downSharpen;
        params.fade[2] = cmd.prev[0];
        params.fade[3] = cmd.prev[1];
    }

    // The edit kept over frames, if the pass before this one made it for this resolve (see Steady).
    const Surface* keptEdit = nullptr;

    if (resolve && set.steadyReady >= 0)
    {
        const Surface& k = set.kept[set.steadyReady][set.keptAt[set.steadyReady]];

        const Surface& like = set.steadyFull ? set.result : set.model;

        if (k.srv != nullptr && k.desc.Width == like.desc.Width && k.desc.Height == like.desc.Height)
        {
            keptEdit = &k;
            params.flags |= set.steadyFull ? kFlagFull : kFlagSteady;
        }

        set.steadyReady = -1;
    }

    // (the sharpening goes with either way of enlarging; following the edges is the dearer part)
    if (resolve)
    {
        params.flags |= (g_resolveFollow != 0 ? kFlagFollow : 0u) | (g_resolveOutline != 0 && cmd.windowed != 0 ? kFlagOutline : 0u);
        params.fade[1] = g_resolveEdge;
        params.fade[2] = g_resolveSharpen;
        params.fade[3] = g_resolveHalo;
    }

    // Said when it changes, with the sizes: whether this is in force cannot be seen from outside.
    if (resolve)
    {
        const LONG now = (g_resolveFollow != 0 ? 100000 : 0) + (LONG) (g_resolveSharpen * 100.0f);

        if (now != g_resolveSaid)
        {
            g_resolveSaid = now;
            Log("resolve: a %ux%u model's edit onto %ux%u %s (tolerance %.2f, sharpened %.2f, past its neighbours by %.2f)", work.desc.Width, work.desc.Height,
                target.desc.Width, target.desc.Height, g_resolveFollow != 0 ? "along the frame's edges" : "enlarged plainly", g_resolveEdge, g_resolveSharpen, g_resolveHalo);
        }
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

    ID3D11ShaderResourceView* inputs[3] = { source.srv, resolve ? set.proxy.srv : nullptr, resolve ? set.model.srv : nullptr };
    c->PSSetShaderResources(0, 3, inputs);

    // (the kept edit is a fourth input, beyond what the backup keeps: what was there is put back after)
    ID3D11ShaderResourceView* fourth = nullptr;

    if (keptEdit != nullptr)
    {
        c->PSGetShaderResources(3, 1, &fourth);
        c->PSSetShaderResources(3, 1, &keptEdit->srv);
    }

    c->Draw(3, 0);

    if (keptEdit != nullptr)
    {
        ID3D11ShaderResourceView* nothing = nullptr;
        c->PSSetShaderResources(3, 1, &nothing);
    }

    backup.Restore(c);

    if (keptEdit != nullptr)
    {
        c->PSSetShaderResources(3, 1, &fourth);
        SafeRelease(fourth);
    }

    if (resolve)
        set.status.resolves++;
    else if (!guide)
        set.status.downsamples++;
}

// The model's edit of this pass, blended into what was kept of it from the frames before (see
// PSSteady), ready for the resolve that follows. The motion vectors are opened for this one call
// and let go: they are the model's own copies, and whose they are next frame is not ours to hold.
void Steady(Set& set, const VwsCmd& cmd)
{
    set.steadyReady = -1;

    if (!set.Complete() || !set.Paired() || cmd.frame == nullptr || g_ctx == nullptr || g_psSteady == nullptr)
        return;

    // (mode: 1 = nothing kept is to be used; 2 = kept at the frame's size, with the model's raster shifted -- PSGather)
    const bool full = (cmd.mode & 2) != 0 && g_psGather != nullptr;
    const uint32_t slot = cmd.which < kSteadySlots ? cmd.which : kSteadySlots - 1;
    const uint32_t w = full ? set.result.desc.Width : set.proxy.desc.Width, h = full ? set.result.desc.Height : set.proxy.desc.Height;
    HRESULT hr = S_OK;

    for (int k = 0; k < 2; ++k)
    {
        Surface& s = set.kept[slot][k];

        if (s.tex != nullptr && s.desc.Width == w && s.desc.Height == h)
            continue;

        s.Release();
        set.keptPrimed[slot] = false;

        D3D11_TEXTURE2D_DESC td {};
        td.Width = w;
        td.Height = h;
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;

        ID3D11Texture2D* made = nullptr;
        hr = g_device->CreateTexture2D(&td, nullptr, &made);
        const bool opened = SUCCEEDED(hr) && OpenSurface(s, made, false, true, true, "the edit kept over frames", &hr) == ErrNone;
        SafeRelease(made);

        if (!opened)
        {
            InterlockedIncrement(&g_steadyFailed);
            return;
        }
    }

    Surface motion;

    if (OpenSurface(motion, cmd.frame, false, true, false, "the motion vectors", &hr) != ErrNone)
    {
        InterlockedIncrement(&g_steadyFailed);
        return;
    }

    const bool fresh = (cmd.mode & 1) != 0 || !set.keptPrimed[slot];
    const uint32_t from = set.keptAt[slot], to = 1 - from;

    Params params {};
    SetSize(params.dstSize, w, h);
    SetSize(params.workSize, set.proxy.desc.Width, set.proxy.desc.Height);
    params.eyes = cmd.eyes == 2 ? 2u : 1u;
    params.strength = cmd.strength < 0.02f ? 0.02f : (cmd.strength > 1.0f ? 1.0f : cmd.strength);
    params.mode = fresh ? 1u : 0u;
    params.flags = (set.proxy.Encoded() ? kFlagProxyEncoded : 0) | (set.model.Encoded() ? kFlagModelEncoded : 0) | (cmd.topDown != 0 ? kFlagTopDown : 0);

    if (full)
    {
        // the frame is the first input, the motion vectors the fifth; enlarged as the resolve would
        SetSize(params.frameSize, set.frame.desc.Width, set.frame.desc.Height);
        SetSize(params.prev[1], motion.desc.Width, motion.desc.Height);
        params.prev[0][0] = cmd.prev[0];
        params.prev[0][1] = cmd.prev[1];
        params.prev[0][2] = cmd.prev[2];
        params.flags |= (set.frame.Encoded() ? kFlagFrameEncoded : 0) | (g_resolveFollow != 0 ? kFlagFollow : 0);
        params.fade[1] = g_resolveEdge;
        params.fade[2] = g_resolveSharpen;
        params.fade[3] = g_resolveHalo;
    }
    else
    {
        SetSize(params.frameSize, motion.desc.Width, motion.desc.Height);
    }

    for (int e = 0; e < 2; ++e)
    {
        params.window[e][0] = params.window[e][1] = 0.0f;
        params.window[e][2] = params.window[e][3] = 1.0f;
    }

    if (cmd.windowed != 0 && !full)
    {
        memcpy(params.window, cmd.window, sizeof(params.window));
        params.flags |= kFlagWindow;
    }

    ID3D11DeviceContext* c = g_ctx;
    D3D11_MAPPED_SUBRESOURCE mapped {};
    hr = c->Map(g_cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);

    if (FAILED(hr))
    {
        motion.Release();
        InterlockedIncrement(&g_steadyFailed);
        return;
    }

    memcpy(mapped.pData, &params, sizeof(params));
    c->Unmap(g_cb, 0);

    StateBackup backup;
    backup.Capture(c);

    // (the fourth and fifth inputs are beyond what the backup keeps: kept here, and put back below)
    ID3D11ShaderResourceView* beyond[2] {};
    c->PSGetShaderResources(3, 2, beyond);

    ID3D11ShaderResourceView* none[5] {};
    c->PSSetShaderResources(0, 5, none);
    c->OMSetRenderTargets(1, &set.kept[slot][to].rtv, nullptr);

    D3D11_VIEWPORT vp {};
    vp.Width = (float) w;
    vp.Height = (float) h;
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
    c->PSSetShader(full ? g_psGather : g_psSteady, nullptr, 0);
    c->PSSetConstantBuffers(0, 1, &g_cb);
    c->PSSetSamplers(0, 1, &g_sampler);

    ID3D11ShaderResourceView* inputs[5] = { full ? set.frame.srv : motion.srv, set.proxy.srv, set.model.srv, fresh ? nullptr : set.kept[slot][from].srv,
                                            full ? motion.srv : nullptr };
    c->PSSetShaderResources(0, 5, inputs);

    c->Draw(3, 0);

    c->PSSetShaderResources(3, 2, none);
    backup.Restore(c);
    c->PSSetShaderResources(3, 2, beyond);
    SafeRelease(beyond[0]);
    SafeRelease(beyond[1]);
    motion.Release();

    set.keptAt[slot] = to;
    set.keptPrimed[slot] = true;
    set.steadyReady = (int) slot;
    set.steadyFull = full;
    InterlockedIncrement(&g_steadyRuns);

    if (fresh)
        InterlockedIncrement(&g_steadyFresh);
}

// A rigid transform, rows of a 3x4.
struct Rigid
{
    double m[12];
};

Rigid FromFloats(const float* f)
{
    Rigid r;

    for (int i = 0; i < 12; ++i)
        r.m[i] = f[i];

    return r;
}

Rigid Inverse(const Rigid& a)
{
    Rigid r;

    for (int i = 0; i < 3; ++i)
    {
        for (int j = 0; j < 3; ++j)
            r.m[i * 4 + j] = a.m[j * 4 + i];

        r.m[i * 4 + 3] = -(a.m[0 * 4 + i] * a.m[3] + a.m[1 * 4 + i] * a.m[7] + a.m[2 * 4 + i] * a.m[11]);
    }

    return r;
}

Rigid Mul(const Rigid& a, const Rigid& b)
{
    Rigid r;

    for (int i = 0; i < 3; ++i)
    {
        for (int j = 0; j < 4; ++j)
        {
            r.m[i * 4 + j] = a.m[i * 4 + 0] * b.m[0 * 4 + j] + a.m[i * 4 + 1] * b.m[1 * 4 + j] + a.m[i * 4 + 2] * b.m[2 * 4 + j] +
                             (j == 3 ? a.m[i * 4 + 3] : 0.0);
        }
    }

    return r;
}

Rigid HeadAt(const float* framePose, const float* cfg);
double Seconds();

// ---- the room as a SteamVR overlay -------------------------------------------------------------
//
// Drawn into the game's frame, the room moves at the game's frame rate. As an overlay it does not:
// the camera's thread has a device of its own, and for every camera frame draws the room onto a
// stereo quad that is stood in the world where the head was when the frame was taken. SteamVR then
// shows that quad from wherever the head is now, at the headset's own rate. Where the quad is
// opaque is the game's doing: each frame it writes a matte (white where its picture is the key
// colour) into a texture both devices can open, with the head's pose that frame was rendered at.
struct OverlayParams
{
    float quad[4];    // half-width, half-height, distance of the quad; distance the room is taken to be at
    float lens[4];    // fisheye pixels per radian, gain, lens width, view
    float k[4];
    float centre[4];
    float cam[4];     // camera frame width, height, matte usable, half the distance between the eyes
    float tangents[2][4];
    float camRows[2][3][4]; // the head's space -> each camera's
    float rot[3][4];  // the head at the camera frame's moment, in the room (rotation); [0][3] = it is known
    float out[4];     // texture width, height, 1/width, 1/height
    float matte[4];   // how much of the matte texture's height is matte; the row its pose is in; the depth grid's reach (tangent)
    float depth[4];   // used, margin, softness
    float grid[4];    // cells per side, -, -, the most 1/distance
};

// The room's depth: a grid of lines of sight from the middle of the head (see vws_depth.h).
const uint32_t kGridN = vwsdepth::kCells;
const float kGridTan = vwsdepth::kReach, kGridMost = vwsdepth::kMost;

// It is worked out on a thread of its own: the camera's thread hands it a frame whenever it is
// free, and takes whatever it last finished. It runs a frame or two behind the picture, which the
// head's turning in that time does not make matter at the grid's scale.
SRWLOCK g_depthLock = SRWLOCK_INIT;
HANDLE g_depthThread = nullptr, g_depthWake = nullptr;
volatile LONG g_depthQuit = 0, g_depthBusy = 0, g_depthSerial = 0, g_depthMicros = 0;
uint8_t* g_depthIn = nullptr;
uint32_t g_depthW = 0, g_depthH = 0;
float g_depthCfg[vwsdepth::kCalibration] {};
uint8_t g_depthOut[vwsdepth::kCells * vwsdepth::kCells * 4];

DWORD WINAPI DepthThread(void*)
{
    vwsdepth::Solver* solver = new vwsdepth::Solver();
    uint8_t* out = new uint8_t[sizeof(g_depthOut)];

    while (WaitForSingleObject(g_depthWake, INFINITE) == WAIT_OBJECT_0 && g_depthQuit == 0)
    {
        // The frame is the camera thread's until it has set the flag, and this thread's until it
        // clears it.
        const double start = Seconds();
        solver->Solve(g_depthIn, g_depthW, g_depthH, g_depthCfg, out);
        InterlockedExchange(&g_depthMicros, (LONG) ((Seconds() - start) * 1e6));

        AcquireSRWLockExclusive(&g_depthLock);
        memcpy(g_depthOut, out, sizeof(g_depthOut));
        ReleaseSRWLockExclusive(&g_depthLock);
        InterlockedIncrement(&g_depthSerial);
        InterlockedExchange(&g_depthBusy, 0);
    }

    delete[] out;
    delete solver;
    return 0;
}

// Camera thread: hand the depth thread this frame if it is free.
void DepthFeed(const uint8_t* grey, uint32_t w, uint32_t h, const float* cfg)
{
    if (g_depthThread == nullptr)
    {
        InterlockedExchange(&g_depthQuit, 0);
        InterlockedExchange(&g_depthBusy, 0);
        g_depthWake = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        g_depthThread = g_depthWake != nullptr ? CreateThread(nullptr, 0, DepthThread, nullptr, 0, nullptr) : nullptr;

        if (g_depthThread == nullptr)
            return;

        // Below the game's own threads: it must never be what a frame waits for.
        SetThreadPriority(g_depthThread, THREAD_PRIORITY_BELOW_NORMAL);
    }

    if (InterlockedCompareExchange(&g_depthBusy, 1, 0) != 0)
        return;

    if (g_depthIn == nullptr || g_depthW != w || g_depthH != h)
    {
        delete[] g_depthIn;
        g_depthIn = new uint8_t[(size_t) w * h];
        g_depthW = w;
        g_depthH = h;
    }

    memcpy(g_depthIn, grey, (size_t) w * h);
    memcpy(g_depthCfg, cfg, sizeof(g_depthCfg));
    SetEvent(g_depthWake);
}

void DepthStop()
{
    if (g_depthThread == nullptr)
        return;

    InterlockedExchange(&g_depthQuit, 1);
    SetEvent(g_depthWake);

    if (WaitForSingleObject(g_depthThread, 3000) == WAIT_OBJECT_0)
    {
        delete[] g_depthIn;
        g_depthIn = nullptr;
        g_depthW = g_depthH = 0;
    }

    CloseHandle(g_depthThread);
    CloseHandle(g_depthWake);
    g_depthThread = g_depthWake = nullptr;
}

// ---- the wearer's hands (see vws_hand.h) --------------------------------------------------------
//
// Like the room's depth, worked out on a thread of its own from the frames the camera's thread
// hands it whenever it is free; the game reads whatever it last finished.
vwshand::Networks g_handNets;
SRWLOCK g_handLock = SRWLOCK_INIT;
HANDLE g_handThread = nullptr, g_handWake = nullptr;
volatile LONG g_handQuit = 0, g_handBusy = 0, g_handSerial = 0, g_handMicros = 0, g_handOn = 0, g_handEvery = 2;
uint8_t* g_handIn = nullptr;
uint32_t g_handW = 0, g_handH = 0, g_handSince = 0;
float g_handCfg[kCamFloats] {}, g_handHead[12] {}, g_handSet[vwshand::kSettings] {};
bool g_handHeadKnown = false, g_handSetGiven = false, g_handOutInRoom = false;
double g_handAt = 0.0, g_handOutAt = 0.0;
vwshand::HandOut g_handOut[vwshand::kHands] {};
float g_handCurls[vwshand::kHands * 6] {};

DWORD WINAPI HandThread(void*)
{
    vwshand::Tracker* tracker = new vwshand::Tracker();
    tracker->nets = &g_handNets;
    vwshand::HandOut out[vwshand::kHands];
    double said = Seconds(), spent = 0.0;

    while (WaitForSingleObject(g_handWake, INFINITE) == WAIT_OBJECT_0 && g_handQuit == 0)
    {
        // The frame is the camera thread's until it has set the flag, and this thread's until it
        // clears it.
        AcquireSRWLockShared(&g_handLock);

        if (g_handSetGiven)
            memcpy(tracker->set, g_handSet, sizeof(g_handSet));

        ReleaseSRWLockShared(&g_handLock);

        const double start = Seconds();
        tracker->Track(g_handIn, g_handW, g_handH, g_handCfg, g_handHeadKnown ? g_handHead : nullptr, g_handAt, out);
        const double took = Seconds() - start;
        spent += took;
        InterlockedExchange(&g_handMicros, (LONG) (took * 1e6));

        // What it did, said every five seconds: there is no looking into a headset from outside.
        if (start + took - said >= 5.0)
        {
            const vwshand::Tracker::Tally& t = tracker->tally;
            Log("hand thread: %u frames in %.0f s (%.1f ms each, frame mean %.0f/255), a hand followed in %u (by one lens alone %u); looked for palms %u times: %u in the left lens, %u in the right, %u believed by the points network, %u hands begun, %u lost and found again, %u given up, %u dropped as doubles; %u sudden turns not believed, %u finger joints put back; now %s%s",
                t.frames, start + took - said, t.frames != 0 ? spent / t.frames * 1000.0 : 0.0, tracker->frameMean * 255.0f, t.followed, t.oneLens, t.looks,
                t.palms[0], t.palms[1], t.believed, t.begun, t.found, t.lost, t.same, t.doubted, t.folded,
                out[0].live > 0.5f ? (out[0].left > 0.5f ? "left " : "right ") : "", out[1].live > 0.5f ? (out[1].left > 0.5f ? "left" : "right") : (out[0].live > 0.5f ? "" : "none"));
            tracker->tally = {};
            said = start + took;
            spent = 0.0;
        }

        float curls[vwshand::kHands * 6];
        tracker->Curls(curls);

        AcquireSRWLockExclusive(&g_handLock);
        memcpy(g_handOut, out, sizeof(g_handOut));
        memcpy(g_handCurls, curls, sizeof(g_handCurls));
        g_handOutAt = g_handAt;
        g_handOutInRoom = g_handHeadKnown;
        ReleaseSRWLockExclusive(&g_handLock);
        InterlockedIncrement(&g_handSerial);
        InterlockedExchange(&g_handBusy, 0);
    }

    delete tracker;
    return 0;
}

// Camera thread: hand the hand thread this frame if it is wanted and the thread is free. `header`
// is the frame's own (the pose it was taken at).
void HandFeed(const uint8_t* grey, uint32_t w, uint32_t h, const uint8_t* header)
{
    if (g_handOn == 0 || g_handNets.hand == nullptr)
        return;

    // Not every frame: the game needs the processor more than a hand needs sixty looks a second.
    if (++g_handSince < (uint32_t) (g_handEvery < 1 ? 1 : g_handEvery))
        return;

    float cfg[kCamFloats];
    AcquireSRWLockExclusive(&g_camLock);
    const bool configured = g_camConfigured;
    memcpy(cfg, g_camConfig, sizeof(cfg));
    ReleaseSRWLockExclusive(&g_camLock);

    if (!configured)
        return;

    if (g_handThread == nullptr)
    {
        InterlockedExchange(&g_handQuit, 0);
        InterlockedExchange(&g_handBusy, 0);
        g_handWake = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        g_handThread = g_handWake != nullptr ? CreateThread(nullptr, 0, HandThread, nullptr, 0, nullptr) : nullptr;

        if (g_handThread == nullptr)
            return;

        // Below the game's own threads: it must never be what a frame waits for.
        SetThreadPriority(g_handThread, THREAD_PRIORITY_BELOW_NORMAL);
    }

    if (InterlockedCompareExchange(&g_handBusy, 1, 0) != 0)
        return;

    g_handSince = 0;

    if (g_handIn == nullptr || g_handW != w || g_handH != h)
    {
        delete[] g_handIn;
        g_handIn = new uint8_t[(size_t) w * h];
        g_handW = w;
        g_handH = h;
    }

    memcpy(g_handIn, grey, (size_t) w * h);
    memcpy(g_handCfg, cfg, sizeof(g_handCfg));

    // A frame without a pose: the head is taken to be where it last was.
    if (header[96] != 0)
    {
        float framePose[12];
        memcpy(framePose, header + 20, sizeof(framePose));
        const Rigid head = HeadAt(framePose, cfg);

        for (int i = 0; i < 12; ++i)
            g_handHead[i] = (float) head.m[i];

        g_handHeadKnown = true;
    }

    g_handAt = Seconds();
    SetEvent(g_handWake);
}

void HandStop()
{
    if (g_handThread == nullptr)
        return;

    InterlockedExchange(&g_handQuit, 1);
    SetEvent(g_handWake);

    if (WaitForSingleObject(g_handThread, 3000) == WAIT_OBJECT_0)
    {
        delete[] g_handIn;
        g_handIn = nullptr;
        g_handW = g_handH = 0;
    }

    CloseHandle(g_handThread);
    CloseHandle(g_handWake);
    g_handThread = g_handWake = nullptr;

    AcquireSRWLockExclusive(&g_handLock);
    memset(g_handOut, 0, sizeof(g_handOut));
    ReleaseSRWLockExclusive(&g_handLock);
    InterlockedIncrement(&g_handSerial);
}

struct VrTexture
{
    void* handle;
    int type;
    int colorSpace;
};

struct OverlayRig
{
    ID3D11Device* dev = nullptr;
    ID3D11DeviceContext* ctx = nullptr;
    ID3D11VertexShader* vs = nullptr;
    ID3D11PixelShader* ps = nullptr;
    ID3D11Buffer* cb = nullptr;
    ID3D11SamplerState* sampler = nullptr;
    ID3D11RasterizerState* raster = nullptr;
    ID3D11Texture2D* cam = nullptr;
    ID3D11ShaderResourceView* camSrv = nullptr;
    uint32_t camW = 0, camH = 0;
    ID3D11Texture2D* matte = nullptr;
    ID3D11ShaderResourceView* matteSrv = nullptr;
    uint32_t matteSerial = 0xFFFFFFFFu;
    // Two of everything SteamVR sees. A picture and the place it belongs to cannot be handed over
    // in one step, and a picture shown for even one refresh at the next picture's place jumps by
    // however far the head turned in between. So the overlay that is showing is never touched:
    // the hidden one is given its picture and its place, and then the two change over.
    ID3D11Texture2D* out[2] = {};
    ID3D11RenderTargetView* outRtv[2] = {};
    uint32_t outW = 0, outH = 0;
    ID3D11Query* done = nullptr;
    // the room's depth, as the depth thread last had it
    ID3D11Texture2D* grid = nullptr;
    ID3D11ShaderResourceView* gridSrv = nullptr;
    ID3D11Texture2D* probe = nullptr;
    LONG gridSerial = 0;
    bool depthFailed = false;
    uint32_t depthTicks = 0;
    void** vr = nullptr;
    bool vrAsked = false;
    uint64_t overlay[2] = {};
    int front = -1; // which of the two is showing
    float width[2] = {};
    bool failed = false;
    int lost = 0;
};

template <typename F> F VrFn(void** table, int slot)
{
    return (F) table[slot];
}

void DepthRelease(OverlayRig& r)
{
    SafeRelease(r.probe);
    SafeRelease(r.gridSrv);
    SafeRelease(r.grid);
    r.gridSerial = 0;
}

void OverlayHide(OverlayRig& r)
{
    if (r.vr != nullptr && r.front >= 0)
    {
        for (int i = 0; i < 2; ++i)
        {
            if (r.overlay[i] != 0)
                VrFn<int(__stdcall*)(uint64_t)>(r.vr, 44)(r.overlay[i]);
        }
    }

    r.front = -1;
}

void OverlayClose(OverlayRig& r)
{
    OverlayHide(r);

    for (int i = 0; i < 2; ++i)
    {
        if (r.vr != nullptr && r.overlay[i] != 0)
            VrFn<int(__stdcall*)(uint64_t)>(r.vr, 3)(r.overlay[i]);

        r.overlay[i] = 0;
        SafeRelease(r.outRtv[i]);
        SafeRelease(r.out[i]);
    }

    SafeRelease(r.done);
    DepthRelease(r);
    SafeRelease(r.matteSrv);
    SafeRelease(r.matte);
    SafeRelease(r.camSrv);
    SafeRelease(r.cam);
    SafeRelease(r.raster);
    SafeRelease(r.sampler);
    SafeRelease(r.cb);
    SafeRelease(r.ps);
    SafeRelease(r.vs);
    SafeRelease(r.ctx);
    SafeRelease(r.dev);
    InterlockedExchange(&g_ovState, 0);
}

// The overlay's device has stopped answering: say why, let go of everything made on it, and have
// the next camera frame make another. The overlays themselves are SteamVR's and stay.
void OverlayLost(OverlayRig& r, HRESULT hr, const char* where)
{
    const HRESULT reason = r.dev != nullptr ? r.dev->GetDeviceRemovedReason() : S_OK;
    Log("passthrough overlay: its device failed at %s (hr=0x%08X, removed for 0x%08X) -- %s", where, (unsigned) hr, (unsigned) reason,
        r.lost < 5 ? "making another" : "giving up");

    for (int i = 0; i < 2; ++i)
    {
        SafeRelease(r.outRtv[i]);
        SafeRelease(r.out[i]);
    }

    SafeRelease(r.done);
    DepthRelease(r);
    SafeRelease(r.matteSrv);
    SafeRelease(r.matte);
    SafeRelease(r.camSrv);
    SafeRelease(r.cam);
    SafeRelease(r.raster);
    SafeRelease(r.sampler);
    SafeRelease(r.cb);
    SafeRelease(r.ps);
    SafeRelease(r.vs);
    SafeRelease(r.ctx);
    SafeRelease(r.dev);
    r.camW = r.camH = r.outW = r.outH = 0;
    r.matteSerial = 0xFFFFFFFFu;
    r.failed = ++r.lost > 5;
    InterlockedExchange(&g_ovError, (LONG) (reason != S_OK ? reason : hr));
    InterlockedExchange(&g_ovState, 2);
}

bool OverlayDevice(OverlayRig& r, const LUID& luid)
{
    if (r.dev != nullptr)
        return true;

    if (r.failed)
        return false;

    IDXGIFactory1* factory = nullptr;
    IDXGIAdapter1* adapter = nullptr;
    HRESULT hr = CreateDXGIFactory1(__uuidof(IDXGIFactory1), (void**) &factory);

    for (UINT i = 0; SUCCEEDED(hr) && factory->EnumAdapters1(i, &adapter) != DXGI_ERROR_NOT_FOUND; ++i)
    {
        DXGI_ADAPTER_DESC1 ad {};
        adapter->GetDesc1(&ad);

        if (ad.AdapterLuid.LowPart == luid.LowPart && ad.AdapterLuid.HighPart == luid.HighPart)
            break;

        SafeRelease(adapter);
    }

    if (adapter == nullptr)
        hr = E_FAIL;

    const D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL_11_0;

    if (SUCCEEDED(hr))
        hr = D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, nullptr, 0, &level, 1, D3D11_SDK_VERSION, &r.dev, nullptr, &r.ctx);

    SafeRelease(adapter);
    SafeRelease(factory);

    if (SUCCEEDED(hr))
        hr = r.dev->CreateVertexShader(g_vwsVsOverlay, sizeof(g_vwsVsOverlay), nullptr, &r.vs);

    if (SUCCEEDED(hr))
        hr = r.dev->CreatePixelShader(g_vwsPsOverlay, sizeof(g_vwsPsOverlay), nullptr, &r.ps);

    if (SUCCEEDED(hr))
    {
        D3D11_BUFFER_DESC bd {};
        bd.ByteWidth = sizeof(OverlayParams);
        bd.Usage = D3D11_USAGE_DYNAMIC;
        bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        hr = r.dev->CreateBuffer(&bd, nullptr, &r.cb);
    }

    if (SUCCEEDED(hr))
    {
        D3D11_SAMPLER_DESC sd {};
        sd.Filter = D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;
        sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sd.ComparisonFunc = D3D11_COMPARISON_NEVER;
        sd.MaxAnisotropy = 1;
        sd.MaxLOD = D3D11_FLOAT32_MAX;
        hr = r.dev->CreateSamplerState(&sd, &r.sampler);
    }

    if (SUCCEEDED(hr))
    {
        D3D11_RASTERIZER_DESC rd {};
        rd.FillMode = D3D11_FILL_SOLID;
        rd.CullMode = D3D11_CULL_NONE;
        rd.DepthClipEnable = TRUE;
        hr = r.dev->CreateRasterizerState(&rd, &r.raster);
    }

    if (FAILED(hr))
    {
        Log("passthrough overlay: no device of its own (hr=0x%08X) -- the overlay is off", (unsigned) hr);
        r.failed = true;
        InterlockedExchange(&g_ovState, 2);
        InterlockedExchange(&g_ovError, (LONG) hr);
        return false;
    }

    return true;
}

// SteamVR's overlay interface, from the game's own openvr_api.dll.
bool OverlayVr(OverlayRig& r)
{
    if (r.overlay[0] != 0 && r.overlay[1] != 0)
        return true;

    if (r.vrAsked && r.vr == nullptr)
        return false;

    r.vrAsked = true;

    HMODULE module = GetModuleHandleW(L"openvr_api.dll");
    typedef void* (*GetInterfaceFn)(const char*, int*);
    GetInterfaceFn getInterface = module != nullptr ? (GetInterfaceFn) GetProcAddress(module, "VR_GetGenericInterface") : nullptr;
    int error = 0;
    r.vr = getInterface != nullptr ? (void**) getInterface("FnTable:IVROverlay_028", &error) : nullptr;

    if (r.vr == nullptr)
    {
        InterlockedExchange(&g_ovState, 3);
        return false;
    }

    typedef int(__stdcall* KeyFn)(char*, uint64_t*);
    typedef int(__stdcall* CreateFn)(char*, char*, uint64_t*);

    for (int i = 0; i < 2; ++i)
    {
        char key[64], name[64];
        snprintf(key, sizeof(key), "jeahbwoi720.vamdlss.passthrough.%d", i);
        snprintf(name, sizeof(name), "VaM DLSS passthrough %d", i);

        // One left over from a session that did not end tidily is taken up again.
        if (VrFn<KeyFn>(r.vr, 0)(key, &r.overlay[i]) != 0 || r.overlay[i] == 0)
        {
            const int e = VrFn<CreateFn>(r.vr, 1)(key, name, &r.overlay[i]);

            if (e != 0 || r.overlay[i] == 0)
            {
                Log("passthrough overlay: SteamVR would not make the overlay (error %d)", e);
                r.overlay[i] = 0;
                r.vr = nullptr;
                InterlockedExchange(&g_ovState, 4);
                InterlockedExchange(&g_ovError, e);
                return false;
            }
        }

        VrFn<int(__stdcall*)(uint64_t, int, bool)>(r.vr, 11)(r.overlay[i], 1024, true); // side by side, left eye's picture first
        VrFn<int(__stdcall*)(uint64_t, float)>(r.vr, 16)(r.overlay[i], 1.0f);
        VrFn<int(__stdcall*)(uint64_t)>(r.vr, 44)(r.overlay[i]);
        r.width[i] = 0.0f;
    }

    r.front = -1;
    return true;
}

// One camera frame onto the overlay. `header` is SteamVR's, with the head's pose at the frame's moment.
// The texture the room's depth is handed to the card in, made when first wanted. False, and for
// good, when the card will not have it: the overlay then goes on without the room's depth.
bool DepthReady(OverlayRig& r)
{
    if (r.grid != nullptr)
        return true;

    D3D11_TEXTURE2D_DESC td {};
    td.Width = td.Height = kGridN;
    td.MipLevels = 1;
    td.ArraySize = 1;
    td.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE;

    const uint8_t* nothing = new uint8_t[kGridN * kGridN * 4]();
    const D3D11_SUBRESOURCE_DATA first = { nothing, kGridN * 4, 0 };
    HRESULT hr = r.dev->CreateTexture2D(&td, &first, &r.grid);
    delete[] nothing;

    if (SUCCEEDED(hr))
        hr = r.dev->CreateShaderResourceView(r.grid, nullptr, &r.gridSrv);

    if (SUCCEEDED(hr))
    {
        td.Width = td.Height = 1;
        td.Usage = D3D11_USAGE_STAGING;
        td.BindFlags = 0;
        td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        hr = r.dev->CreateTexture2D(&td, nullptr, &r.probe);
    }

    if (SUCCEEDED(hr))
        return true;

    Log("passthrough overlay: no texture for the room's depth (hr=0x%08X) -- going on without it", (unsigned) hr);
    DepthRelease(r);
    r.depthFailed = true;
    return false;
}

void OverlayTick(OverlayRig& r, const uint8_t* grey, const uint8_t* header, double* waited)
{
    float cfg[kCamFloats];
    float gamePose[12];
    bool configured, luidKnown, gamePoseValid;
    LUID luid;
    HANDLE matteHandle;
    uint32_t matteSerial, w, h;

    AcquireSRWLockExclusive(&g_camLock);
    configured = g_camConfigured;
    memcpy(cfg, g_camConfig, sizeof(cfg));
    luid = g_gameAdapter;
    luidKnown = g_gameAdapterKnown;
    matteHandle = g_matteHandle;
    matteSerial = g_matteSerial;
    memcpy(gamePose, g_gamePose, sizeof(gamePose));
    gamePoseValid = g_gamePoseValid;
    w = g_camW;
    h = g_camH;
    ReleaseSRWLockExclusive(&g_camLock);

    if (!configured || cfg[kCfgMode] < 0.5f)
    {
        OverlayHide(r);
        InterlockedExchange(&g_ovState, 0);
        return;
    }

    if (!luidKnown)
    {
        InterlockedExchange(&g_ovState, 1);
        return;
    }

    if (!OverlayDevice(r, luid))
        return;

    HRESULT hr = S_OK;

    if (r.cam == nullptr || r.camW != w || r.camH != h)
    {
        SafeRelease(r.camSrv);
        SafeRelease(r.cam);

        D3D11_TEXTURE2D_DESC td {};
        td.Width = w;
        td.Height = h;
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = DXGI_FORMAT_R8_UNORM;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        hr = r.dev->CreateTexture2D(&td, nullptr, &r.cam);

        if (SUCCEEDED(hr))
            hr = r.dev->CreateShaderResourceView(r.cam, nullptr, &r.camSrv);

        r.camW = w;
        r.camH = h;
    }

    // The quad's picture: one square per eye, side by side -- or, where SteamVR shapes the quad by
    // the whole texture, two half-width ones in a square.
    const bool wholeTexture = cfg[kCfgStereoRule] > 0.5f;
    const uint32_t outH = 1536, outW = wholeTexture ? 1536u : 3072u;

    for (int i = 0; i < 2; ++i)
    {
        if (FAILED(hr) || (r.out[i] != nullptr && r.outW == outW && r.outH == outH))
            continue;

        SafeRelease(r.outRtv[i]);
        SafeRelease(r.out[i]);

        D3D11_TEXTURE2D_DESC td {};
        td.Width = outW;
        td.Height = outH;
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        td.MiscFlags = D3D11_RESOURCE_MISC_SHARED;
        hr = r.dev->CreateTexture2D(&td, nullptr, &r.out[i]);

        if (SUCCEEDED(hr))
            hr = r.dev->CreateRenderTargetView(r.out[i], nullptr, &r.outRtv[i]);
    }

    r.outW = outW;
    r.outH = outH;

    if (SUCCEEDED(hr) && r.done == nullptr)
    {
        D3D11_QUERY_DESC qd {};
        qd.Query = D3D11_QUERY_EVENT;
        hr = r.dev->CreateQuery(&qd, &r.done);
    }

    // The one that is not showing.
    const int next = r.front == 0 ? 1 : 0;

    if (FAILED(hr))
    {
        OverlayLost(r, hr, "making its textures");
        return;
    }

    // The game's matte, whenever the game has made a new texture for it.
    if (r.matteSerial != matteSerial)
    {
        SafeRelease(r.matteSrv);
        SafeRelease(r.matte);
        r.matteSerial = matteSerial;

        if (matteHandle != nullptr && SUCCEEDED(r.dev->OpenSharedResource(matteHandle, __uuidof(ID3D11Texture2D), (void**) &r.matte)))
            r.dev->CreateShaderResourceView(r.matte, nullptr, &r.matteSrv);
    }

    D3D11_MAPPED_SUBRESOURCE mapped {};
    r.ctx->UpdateSubresource(r.cam, 0, nullptr, grey, w, 0);

    // The room's depth: this frame to the depth thread if it is free, and whatever it has
    // finished since into the texture the picture is drawn with.
    const bool depth = cfg[kCfgDepth] > 0.5f && !r.depthFailed && DepthReady(r);

    if (depth)
    {
        // Every other frame: thirty times a second is plenty for a hand, and it leaves the
        // processor to the game.
        if ((r.depthTicks & 1) == 0)
            DepthFeed(grey, w, h, cfg);

        if (r.gridSerial != g_depthSerial)
        {
            r.gridSerial = g_depthSerial;
            AcquireSRWLockExclusive(&g_depthLock);
            r.ctx->UpdateSubresource(r.grid, 0, nullptr, g_depthOut, kGridN * 4, 0);
            ReleaseSRWLockExclusive(&g_depthLock);
        }
    }

    float framePose[12], headThen[12];
    memcpy(framePose, header + 20, sizeof(framePose));
    const bool headThenValid = header[96] != 0;
    const Rigid head = HeadAt(framePose, cfg);

    for (int i = 0; i < 12; ++i)
        headThen[i] = (float) head.m[i];

    OverlayParams op {};
    const float distance = cfg[kCfgQuadDistance] > 0.1f ? cfg[kCfgQuadDistance] : 2.0f;
    const float reach = (cfg[kCfgQuadTan] > 0.1f ? cfg[kCfgQuadTan] : 2.2f) * distance;
    op.quad[0] = reach;
    op.quad[1] = reach;
    op.quad[2] = distance;
    op.quad[3] = cfg[kCfgDistance];
    op.lens[0] = cfg[kCfgFocal];
    op.lens[1] = cfg[kCfgGain];
    op.lens[2] = (float) (w / 2);
    op.lens[3] = cfg[kCfgView];
    memcpy(op.k, cfg + kCfgK, sizeof(op.k));
    memcpy(op.centre, cfg + kCfgCentre, sizeof(op.centre));
    op.cam[0] = (float) w;
    op.cam[1] = (float) h;
    op.cam[2] = (r.matteSrv != nullptr && g_matteFrames > 0) ? 1.0f : 0.0f;
    op.cam[3] = cfg[kCfgEyeToHead + 12 + 3]; // the right eye's x in the head
    memcpy(op.tangents, cfg + kCfgTan, sizeof(op.tangents));

    for (int lens = 0; lens < 2; ++lens)
    {
        const Rigid headToCam = Inverse(FromFloats(cfg + kCfgCamToHead + lens * 12));

        for (int i = 0; i < 12; ++i)
            op.camRows[lens][i / 4][i % 4] = (float) headToCam.m[i];
    }

    // How the head stood at the camera frame's moment. The head the game's frame was drawn from
    // comes with the matte (see PSMatte), and the shader sets the one against the other: only how
    // the head has turned between the two, as SteamVR, too, carries the game's picture to the
    // head's new place by turning it.
    for (int i = 0; i < 3; ++i)
        for (int j = 0; j < 3; ++j)
            op.rot[i][j] = headThen[i * 4 + j];

    op.rot[0][3] = headThenValid ? 1.0f : 0.0f;
    op.matte[0] = (float) kMatteH / (float) kMatteTexH;
    op.matte[1] = (float) kMattePoseRow;
    op.matte[2] = kGridTan;
    op.depth[0] = depth ? 1.0f : 0.0f;
    op.depth[1] = cfg[kCfgDepthMargin];
    op.depth[2] = cfg[kCfgDepthSoft];
    op.grid[0] = (float) kGridN;
    op.grid[3] = kGridMost;

    SetSize(op.out, outW, outH);

    hr = r.ctx->Map(r.cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);

    if (FAILED(hr))
    {
        OverlayLost(r, hr, "setting up the draw");
        return;
    }

    memcpy(mapped.pData, &op, sizeof(op));
    r.ctx->Unmap(r.cb, 0);

    ID3D11DeviceContext* c = r.ctx;
    ID3D11ShaderResourceView* none[3] {};
    c->PSSetShaderResources(0, 3, none);
    c->RSSetState(r.raster);
    c->IASetInputLayout(nullptr);
    c->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    c->PSSetConstantBuffers(1, 1, &r.cb);
    c->VSSetConstantBuffers(1, 1, &r.cb);
    c->PSSetSamplers(0, 1, &r.sampler);

    D3D11_VIEWPORT vp {};
    vp.MaxDepth = 1.0f;

    r.depthTicks++;
    c->OMSetRenderTargets(1, &r.outRtv[next], nullptr);
    vp.Width = (float) outW;
    vp.Height = (float) outH;
    c->RSSetViewports(1, &vp);
    c->VSSetShader(r.vs, nullptr, 0);
    c->PSSetShader(r.ps, nullptr, 0);

    ID3D11ShaderResourceView* inputs[3] = { depth ? r.gridSrv : nullptr, r.camSrv, r.matteSrv };
    c->PSSetShaderResources(0, 3, inputs);
    c->VSSetShaderResources(0, 3, inputs);
    c->Draw(3, 0);
    c->PSSetShaderResources(0, 3, none);
    c->VSSetShaderResources(0, 3, none);
    ID3D11RenderTargetView* noTarget = nullptr;
    c->OMSetRenderTargets(1, &noTarget, nullptr);
    c->Flush();

    hr = r.dev->GetDeviceRemovedReason();

    if (hr != S_OK)
    {
        OverlayLost(r, hr, "drawing");
        return;
    }

    InterlockedIncrement(&g_ovFrames);

    // Now and then, how near the room and the scene are taken to be straight ahead: for the
    // status line, which is the only way to see from outside whether either is being read at all.
    if (depth && r.probe != nullptr && r.depthTicks % 45 == 0)
    {
        AcquireSRWLockExclusive(&g_depthLock);
        const uint8_t room = g_depthOut[((size_t) (kGridN / 2) * kGridN + kGridN / 2) * 4 + 2];
        ReleaseSRWLockExclusive(&g_depthLock);
        InterlockedExchange(&g_ovDepthRoom, g_depthSerial != 0 ? (LONG) lroundf(room / 255.0f * kGridMost * 1000.0f) : -1);

        LONG scene = -1;

        if (r.matte != nullptr)
        {
            const D3D11_BOX box = { kMatteW / 4, kMatteH / 2, 0, kMatteW / 4 + 1, kMatteH / 2 + 1, 1 };
            c->CopySubresourceRegion(r.probe, 0, 0, 0, 0, r.matte, 0, &box);

            if (SUCCEEDED(c->Map(r.probe, 0, D3D11_MAP_READ, 0, &mapped)))
            {
                scene = (LONG) lroundf(((const uint8_t*) mapped.pData)[1] / 255.0f * 4.0f * 1000.0f);
                c->Unmap(r.probe, 0);
            }
        }

        InterlockedExchange(&g_ovDepthScene, scene);
    }
    else if (!depth)
    {
        InterlockedExchange(&g_ovDepthRoom, -1);
        InterlockedExchange(&g_ovDepthScene, -1);
    }

    // For the offline checks: the picture as bytes.
    if (InterlockedCompareExchange(&g_ovDebugWant, 0, 1) == 1)
    {
        D3D11_TEXTURE2D_DESC sd {};
        r.out[next]->GetDesc(&sd);
        sd.Usage = D3D11_USAGE_STAGING;
        sd.BindFlags = 0;
        sd.MiscFlags = 0;
        sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ID3D11Texture2D* staging = nullptr;

        if (SUCCEEDED(r.dev->CreateTexture2D(&sd, nullptr, &staging)))
        {
            c->CopyResource(staging, r.out[next]);

            if (SUCCEEDED(c->Map(staging, 0, D3D11_MAP_READ, 0, &mapped)))
            {
                uint8_t* copy = new uint8_t[(size_t) outW * outH * 4];

                for (uint32_t y = 0; y < outH; ++y)
                    memcpy(copy + (size_t) y * outW * 4, (const uint8_t*) mapped.pData + (size_t) y * mapped.RowPitch, (size_t) outW * 4);

                c->Unmap(staging, 0);

                AcquireSRWLockExclusive(&g_camLock);
                delete[] g_ovDebug;
                g_ovDebug = copy;
                g_ovDebugW = outW;
                g_ovDebugH = outH;
                ReleaseSRWLockExclusive(&g_camLock);
            }

            staging->Release();
        }
    }

    if (!OverlayVr(r))
        return;

    // Without the head's pose at this frame's moment there is nowhere to stand it: the one that
    // is showing stays.
    if (!headThenValid)
        return;

    const uint64_t overlay = r.overlay[next];
    const float width = reach * 2.0f;

    if (width != r.width[next])
    {
        VrFn<int(__stdcall*)(uint64_t, float)>(r.vr, 22)(overlay, width);
        r.width[next] = width;
    }

    // The quad stands where the head was when the camera took this frame, facing it.
    float place[12];
    memcpy(place, headThen, sizeof(place));

    for (int i = 0; i < 3; ++i)
        place[i * 4 + 3] = headThen[i * 4 + 3] - distance * headThen[i * 4 + 2];

    VrFn<int(__stdcall*)(uint64_t, int, float*)>(r.vr, 33)(overlay, (int) cfg[kCfgSpace], place);

    VrTexture texture { r.out[next], 0, 1 };
    const int e = VrFn<int(__stdcall*)(uint64_t, VrTexture*)>(r.vr, 60)(overlay, &texture);

    if (e != 0)
    {
        InterlockedExchange(&g_ovError, e);
        InterlockedExchange(&g_ovState, 4);
        return;
    }

    // SteamVR takes its copy of the picture through this device. Wait until the card has really
    // done it -- it is busy with the game -- before letting the picture be seen.
    if (r.done != nullptr)
    {
        const double start = Seconds();
        c->End(r.done);
        c->Flush();
        BOOL finished = FALSE;

        for (int i = 0; i < 100 && c->GetData(r.done, &finished, sizeof(finished), 0) != S_OK; ++i)
            Sleep(1);

        if (waited != nullptr)
            *waited = Seconds() - start;
    }

    VrFn<int(__stdcall*)(uint64_t)>(r.vr, 43)(overlay);

    if (r.front >= 0 && r.front != next)
        VrFn<int(__stdcall*)(uint64_t)>(r.vr, 44)(r.overlay[r.front]);

    r.front = next;
    InterlockedExchange(&g_ovState, 5);
}

// The head in the room at a camera frame's moment, from the pose the frame came with. SteamVR's
// header calls that pose the tracked device's; a PlayStation VR2's driver puts the first camera's
// there instead -- thirty degrees down and eight centimetres forward of the head.
Rigid HeadAt(const float* framePose, const float* cfg)
{
    const Rigid given = FromFloats(framePose);
    return cfg[kCfgPoseIsCamera] > 0.5f ? Mul(given, Inverse(FromFloats(cfg + kCfgCamToHead))) : given;
}

// SteamVR's call, kept from taking the game down with it if the runtime goes away mid-call.
int CamCall(void* buffer, uint32_t size, void* header)
{
    __try
    {
        return g_camFn(g_camHandle, 0, buffer, size, header, 112);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return -1;
    }
}

// The angle between two poses' rotations, degrees.
float TurnBetween(const float* a, const float* b)
{
    float trace = 0.0f;

    for (int i = 0; i < 3; ++i)
        for (int j = 0; j < 3; ++j)
            trace += a[i * 4 + j] * b[i * 4 + j];

    float c = (trace - 1.0f) * 0.5f;
    c = c > 1.0f ? 1.0f : (c < -1.0f ? -1.0f : c);
    return acosf(c) * 57.29578f;
}

double Seconds()
{
    LARGE_INTEGER f, t;
    QueryPerformanceFrequency(&f);
    QueryPerformanceCounter(&t);
    return (double) t.QuadPart / (double) f.QuadPart;
}

DWORD WINAPI CamThread(void* param)
{
    const LONG mine = (LONG) (intptr_t) param;
    const uint32_t pixels = g_camW * g_camH;
    uint8_t* rgba = new uint8_t[(size_t) pixels * 4];
    uint8_t* grey = new uint8_t[pixels];
    uint8_t header[128];
    uint32_t last = 0xFFFFFFFFu;
    OverlayRig rig;

    // What the thread did, said once every couple of seconds: there is no looking into a headset
    // from outside, and this is how a stall or a wrong pose shows.
    float peek[12] {}, lastPose[12] {};
    bool haveLast = false;
    uint32_t frames = 0, skipped = 0;
    float peekMax = 0.0f, moveSum = 0.0f, moveMax = 0.0f;
    double tickSum = 0.0, tickMax = 0.0, waitSum = 0.0, waitMax = 0.0, fetchSum = 0.0, fetchMax = 0.0;
    double since = Seconds(), gapMax = 0.0, lastFrameAt = since;
    uint64_t lastExposure = 0;
    int64_t exposureMin = 0, exposureMax = 0;
    const LONG picturesAtStart = g_ovFrames;
    LONG picturesBefore = picturesAtStart;

    while (g_camGen == mine)
    {
        // The header alone first: it says whether there is a frame we have not had.
        memset(header, 0, sizeof(header));
        int e = CamCall(nullptr, 0, header);
        uint32_t sequence = 0;
        memcpy(&sequence, header + 16, 4);

        if (e == 0 && sequence != last)
        {
            memcpy(peek, header + 20, sizeof(peek));
            const double fetchStart = Seconds();
            e = CamCall(rgba, pixels * 4, header);

            if (e == 0)
            {
                uint32_t bytes = 0;
                uint32_t got = 0;
                memcpy(&got, header + 16, 4);
                memcpy(&bytes, header + 12, 4);

                if (last != 0xFFFFFFFFu && got - last > 1 && got - last < 1000)
                    skipped += got - last - 1;

                sequence = got;
                last = sequence;

                const double fetched = Seconds();
                fetchSum += fetched - fetchStart;
                fetchMax = fetched - fetchStart > fetchMax ? fetched - fetchStart : fetchMax;
                gapMax = fetched - lastFrameAt > gapMax ? fetched - lastFrameAt : gapMax;
                lastFrameAt = fetched;

                float full[12];
                memcpy(full, header + 20, sizeof(full));
                const float peeked = TurnBetween(peek, full);
                peekMax = peeked > peekMax ? peeked : peekMax;

                if (haveLast)
                {
                    const float moved = TurnBetween(lastPose, full);
                    moveSum += moved;
                    moveMax = moved > moveMax ? moved : moveMax;
                }

                memcpy(lastPose, full, sizeof(full));
                haveLast = true;

                uint64_t exposure = 0;
                memcpy(&exposure, header + 104, 8);

                if (lastExposure != 0)
                {
                    const int64_t step = (int64_t) (exposure - lastExposure);
                    exposureMin = (frames == 0 || step < exposureMin) ? step : exposureMin;
                    exposureMax = (frames == 0 || step > exposureMax) ? step : exposureMax;
                }

                lastExposure = exposure;

                // The cameras are monochrome: one channel says it all.
                if (bytes == 4)
                {
                    for (uint32_t i = 0; i < pixels; ++i)
                        grey[i] = rgba[(size_t) i * 4];
                }
                else
                {
                    memcpy(grey, rgba, pixels);
                }

                HandFeed(grey, g_camW, g_camH, header);

                double waited = 0.0;
                const double tickStart = Seconds();
                OverlayTick(rig, grey, header, &waited);
                const double ticked = Seconds() - tickStart;
                tickSum += ticked;
                tickMax = ticked > tickMax ? ticked : tickMax;
                waitSum += waited;
                waitMax = waited > waitMax ? waited : waitMax;

                if (++frames >= 120)
                {
                    const double now = Seconds();
                    const LONG pictures = g_ovFrames;
                    Log("passthrough thread: %.0f camera frames/s (%u skipped, longest gap %.0f ms), %.0f overlay pictures/s; fetch %.1f/%.1f ms, "
                        "overlay work %.1f/%.1f ms of which waiting for the card %.1f/%.1f ms (avg/max); head turned %.2f/%.2f deg per frame; "
                        "pose moved %.3f deg between asking twice; exposure stamp steps %lld..%lld",
                        frames / (now - since), skipped, gapMax * 1000.0, (pictures - picturesBefore) / (now - since), fetchSum / frames * 1000.0,
                        fetchMax * 1000.0, tickSum / frames * 1000.0, tickMax * 1000.0, waitSum / frames * 1000.0, waitMax * 1000.0, moveSum / frames,
                        moveMax, peekMax, (long long) exposureMin, (long long) exposureMax);

                    picturesBefore = pictures;
                    since = now;
                    frames = skipped = 0;
                    peekMax = moveSum = moveMax = 0.0f;
                    tickSum = tickMax = waitSum = waitMax = fetchSum = fetchMax = gapMax = 0.0;
                }

                AcquireSRWLockExclusive(&g_camLock);
                uint8_t* was = g_camFront;
                g_camFront = grey;
                grey = was;
                memcpy(g_camFrontPose, header + 20, sizeof(g_camFrontPose));
                g_camFrontPoseValid = header[96] != 0;
                g_camFresh = true;
                ReleaseSRWLockExclusive(&g_camLock);
                InterlockedIncrement(&g_camFrames);
            }
        }

        if (e != 0)
            InterlockedExchange(&g_camError, e);

        Sleep(e == 0 ? 2 : 50);
    }

    OverlayClose(rig);
    DepthStop();
    HandStop();
    delete[] rgba;
    delete[] grey;
    return 0;
}

// The frame buffers are made once per size and kept: the render thread may be reading one while
// the stream is being stopped.
void CamBuffers(uint32_t w, uint32_t h)
{
    if (g_camFront != nullptr && g_camW == w && g_camH == h)
        return;

    g_camFront = new uint8_t[(size_t) w * h]();
    g_camSpare = new uint8_t[(size_t) w * h]();
    g_camW = w;
    g_camH = h;
    g_camFresh = false;
}

// Replaces the key colour in a finished frame with the room, as the headset's camera sees it from
// where the eye is. One eye per command: `eyes` 2 means the texture is double-wide and this eye's
// half of it is drawn, 1 that the texture is this eye's own.
void Passthrough(const VwsCmd& cmd)
{
    if (cmd.frame == nullptr || cmd.frame == g_passRefused)
        return;

    float cfg[kCamFloats];
    float pose[12] {};
    bool configured = false, poseValid = false;
    uint8_t* fresh = nullptr;
    uint32_t w = 0, h = 0;

    AcquireSRWLockExclusive(&g_camLock);
    configured = g_camConfigured;
    memcpy(cfg, g_camConfig, sizeof(cfg));
    w = g_camW;
    h = g_camH;

    if (g_camFresh && g_camFront != nullptr && g_camSpare != nullptr)
    {
        uint8_t* was = g_camSpare;
        g_camSpare = g_camFront;
        g_camFront = was;
        fresh = g_camSpare;
        memcpy(pose, g_camFrontPose, sizeof(pose));
        poseValid = g_camFrontPoseValid;
        g_camFresh = false;
    }

    ReleaseSRWLockExclusive(&g_camLock);

    if (!configured || w == 0 || h == 0)
        return;

    ID3D11Device* device = DeviceOf(cmd.frame);

    if (device == nullptr)
        return;

    HRESULT hr = EnsureDevice(device);
    device->Release();

    if (FAILED(hr))
        return;

    ID3D11DeviceContext* c = g_ctx;

    if (g_camTex != nullptr)
    {
        D3D11_TEXTURE2D_DESC have {};
        g_camTex->GetDesc(&have);

        if (have.Width != w || have.Height != h)
            ReleaseCamera();
    }

    if (g_camTex == nullptr)
    {
        D3D11_TEXTURE2D_DESC td {};
        td.Width = w;
        td.Height = h;
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = DXGI_FORMAT_R8_UNORM;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DYNAMIC;
        td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        td.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        hr = g_device->CreateTexture2D(&td, nullptr, &g_camTex);

        if (SUCCEEDED(hr))
            hr = g_device->CreateShaderResourceView(g_camTex, nullptr, &g_camSrv);

        if (FAILED(hr))
        {
            Log("passthrough: no texture for the camera's %ux%u frames (hr=0x%08X)", w, h, (unsigned) hr);
            ReleaseCamera();
            g_passRefused = cmd.frame;
            return;
        }
    }

    if (fresh != nullptr)
    {
        D3D11_MAPPED_SUBRESOURCE mapped {};

        if (SUCCEEDED(c->Map(g_camTex, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
        {
            for (uint32_t y = 0; y < h; ++y)
                memcpy((uint8_t*) mapped.pData + (size_t) y * mapped.RowPitch, fresh + (size_t) y * w, w);

            c->Unmap(g_camTex, 0);
            memcpy(g_camPose, pose, sizeof(pose));
            g_camPoseValid = poseValid;
            g_camHave = true;
            InterlockedIncrement(&g_camUploads);
        }
    }

    // Until the camera has given a frame the key colour stays as it is.
    if (!g_camHave)
        return;

    Surface target;

    if (OpenSurface(target, cmd.frame, (cmd.srgbMask & kFrame) != 0, false, true, "the frame for passthrough", &hr) != ErrNone)
    {
        g_passRefused = cmd.frame;
        return;
    }

    if (g_scratch.tex == nullptr || g_scratch.desc.Width != target.desc.Width || g_scratch.desc.Height != target.desc.Height ||
        g_scratch.desc.Format != target.desc.Format)
    {
        g_scratch.Release();

        D3D11_TEXTURE2D_DESC td = target.desc;
        td.MipLevels = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        td.CPUAccessFlags = 0;
        td.MiscFlags = 0;

        ID3D11Texture2D* copy = nullptr;
        hr = g_device->CreateTexture2D(&td, nullptr, &copy);

        if (SUCCEEDED(hr))
        {
            const int opened = OpenSurface(g_scratch, copy, false, true, false, "the passthrough pass's copy", &hr);
            copy->Release();

            if (opened != ErrNone)
                hr = E_FAIL;
        }

        if (FAILED(hr))
        {
            g_scratch.Release();
            g_passRefused = cmd.frame;
            target.Release();
            return;
        }
    }

    c->CopySubresourceRegion(g_scratch.tex, 0, 0, 0, 0, target.tex, 0, nullptr);

    const uint32_t eye = cmd.which == 1 ? 1u : 0u;
    const bool wide = cmd.eyes == 2;

    // The eye's space to the camera's: through the head as it is now, the room, and the head as
    // it was when the camera took its frame. Without both poses, straight through the head.
    const Rigid camToHead = FromFloats(cfg + kCfgCamToHead + eye * 12);
    Rigid eyeToCam;

    if (cmd.moved != 0 && g_camPoseValid && cfg[kCfgCompensate] > 0.5f)
    {
        float eyeToRoom[12];
        memcpy(eyeToRoom, cmd.window, 8 * sizeof(float));
        memcpy(eyeToRoom + 8, cmd.prev, 4 * sizeof(float));
        eyeToCam = Mul(Inverse(camToHead), Mul(Inverse(HeadAt(g_camPose, cfg)), FromFloats(eyeToRoom)));
    }
    else
    {
        eyeToCam = Mul(Inverse(camToHead), FromFloats(cfg + kCfgEyeToHead + eye * 12));
    }

    PassParams pp {};
    SetSize(pp.dst, target.desc.Width, target.desc.Height);
    pp.key[0] = cfg[kCfgKey];
    pp.key[1] = cfg[kCfgKey + 1];
    pp.key[2] = cfg[kCfgKey + 2];
    pp.key[3] = cfg[kCfgTolerance];
    pp.tune[0] = cfg[kCfgSoftness];
    pp.tune[1] = cfg[kCfgGain];
    pp.tune[2] = cfg[kCfgDistance];
    pp.tune[3] = cfg[kCfgFocal];
    memcpy(pp.k, cfg + kCfgK, sizeof(pp.k));
    memcpy(pp.centre, cfg + kCfgCentre, sizeof(pp.centre));
    pp.cam[0] = (float) w;
    pp.cam[1] = (float) h;
    pp.cam[2] = (float) (w / 2);
    // +8: say in the frame's alpha where the room is; +16: with a half for the game's own picture,
    // so that what is drawn over the frame afterwards can be told from it (see PSMatte)
    pp.cam[3] = cfg[kCfgView] + (cfg[kCfgMode] > 0.5f ? 8.0f : 0.0f) + (cfg[kCfgMode] > 0.5f && cfg[kCfgDepth] > 0.5f ? 16.0f : 0.0f);
    memcpy(pp.tangents, cfg + kCfgTan + eye * 4, sizeof(pp.tangents));

    for (int j = 0; j < 4; ++j)
    {
        pp.row0[j] = (float) eyeToCam.m[j];
        pp.row1[j] = (float) eyeToCam.m[4 + j];
        pp.row2[j] = (float) eyeToCam.m[8 + j];
    }

    pp.misc[0] = (float) eye;
    pp.misc[1] = wide ? 2.0f : 1.0f;
    pp.misc[2] = target.Encoded() ? 1.0f : 0.0f;
    pp.misc[3] = cmd.topDown != 0 ? 1.0f : 0.0f;

    D3D11_MAPPED_SUBRESOURCE mapped {};
    hr = c->Map(g_cbPass, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);

    if (FAILED(hr))
    {
        target.Release();
        return;
    }

    memcpy(mapped.pData, &pp, sizeof(pp));
    c->Unmap(g_cbPass, 0);

    StateBackup backup;
    backup.Capture(c);

    ID3D11Buffer* second = nullptr;
    c->PSGetConstantBuffers(1, 1, &second);

    ID3D11ShaderResourceView* none[3] {};
    c->PSSetShaderResources(0, 3, none);
    c->OMSetRenderTargets(1, &target.rtv, nullptr);

    D3D11_VIEWPORT vp {};
    vp.Width = wide ? (float) (target.desc.Width / 2) : (float) target.desc.Width;
    vp.Height = (float) target.desc.Height;
    vp.TopLeftX = wide ? (float) (eye * (target.desc.Width / 2)) : 0.0f;
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
    c->PSSetShader(g_psPass, nullptr, 0);
    c->PSSetConstantBuffers(1, 1, &g_cbPass);
    c->PSSetSamplers(0, 1, &g_sampler);

    ID3D11ShaderResourceView* inputs[3] = { g_scratch.srv, g_camSrv, nullptr };
    c->PSSetShaderResources(0, 3, inputs);

    c->Draw(3, 0);

    c->PSSetConstantBuffers(1, 1, &second);
    SafeRelease(second);
    backup.Restore(c);
    target.Release();
}

// One eye of the game's matte for the overlay: white where the finished frame is the key colour.
// The matte is one texture for both eyes, first row at the top, opened by the overlay's device.
void Matte(const VwsCmd& cmd)
{
    if (cmd.frame == nullptr || cmd.frame == g_matteRefused)
        return;

    float cfg[kCamFloats];
    bool configured = false;

    AcquireSRWLockExclusive(&g_camLock);
    configured = g_camConfigured;
    memcpy(cfg, g_camConfig, sizeof(cfg));
    ReleaseSRWLockExclusive(&g_camLock);

    if (!configured)
        return;

    ID3D11Device* device = DeviceOf(cmd.frame);

    if (device == nullptr)
        return;

    HRESULT hr = EnsureDevice(device);
    device->Release();

    if (FAILED(hr))
        return;

    ID3D11DeviceContext* c = g_ctx;

    if (g_matteTex == nullptr)
    {
        D3D11_TEXTURE2D_DESC td {};
        td.Width = kMatteW;
        td.Height = kMatteTexH;
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
        td.MiscFlags = D3D11_RESOURCE_MISC_SHARED;
        hr = g_device->CreateTexture2D(&td, nullptr, &g_matteTex);

        if (SUCCEEDED(hr))
            hr = g_device->CreateRenderTargetView(g_matteTex, nullptr, &g_matteRtv);

        IDXGIResource* resource = nullptr;
        HANDLE shared = nullptr;

        if (SUCCEEDED(hr))
            hr = g_matteTex->QueryInterface(__uuidof(IDXGIResource), (void**) &resource);

        if (SUCCEEDED(hr))
            hr = resource->GetSharedHandle(&shared);

        SafeRelease(resource);

        if (FAILED(hr) || shared == nullptr)
        {
            Log("passthrough overlay: no texture to share the matte through (hr=0x%08X)", (unsigned) hr);
            SafeRelease(g_matteRtv);
            SafeRelease(g_matteTex);
            g_matteRefused = cmd.frame;
            return;
        }

        const float clear[4] = { 0, 0, 0, 0 };
        c->ClearRenderTargetView(g_matteRtv, clear);

        AcquireSRWLockExclusive(&g_camLock);
        g_matteHandle = shared;
        g_matteSerial++;
        ReleaseSRWLockExclusive(&g_camLock);
    }

    Surface source;

    if (OpenSurface(source, cmd.frame, (cmd.srgbMask & kFrame) != 0, true, false, "the frame for the matte", &hr) != ErrNone)
    {
        g_matteRefused = cmd.frame;
        return;
    }

    const uint32_t eye = cmd.which == 1 ? 1u : 0u;

    PassParams pp {};
    SetSize(pp.dst, kMatteW, kMatteH);
    pp.key[0] = cfg[kCfgKey];
    pp.key[1] = cfg[kCfgKey + 1];
    pp.key[2] = cfg[kCfgKey + 2];
    pp.key[3] = cfg[kCfgTolerance];
    pp.tune[0] = cfg[kCfgSoftness];
    pp.cam[3] = cmd.strength > 0.5f ? 1.0f : 0.0f; // 1: the frame's alpha already says where the room is
    pp.centre[0] = cmd.strength > 0.5f && cfg[kCfgDepth] > 0.5f ? 1.0f : 0.0f; // ...with a half for the game's own picture

    // The scene's depth, where the game handed its buffer over with the frame (see PSMatte).
    Surface depth;
    const uint32_t depthFlags = (uint32_t) cmd.prev[7];

    if (cmd.proxy != nullptr && cmd.proxy != g_depthRefused && cmd.prev[4] > 0.0f && cmd.prev[5] > cmd.prev[4] && cmd.prev[6] > 0.0f)
    {
        HRESULT dhr = S_OK;

        if (OpenSurface(depth, cmd.proxy, false, true, false, "the scene's depth for the matte", &dhr) == ErrNone)
        {
            pp.tune[1] = cmd.prev[4]; // the near plane
            pp.tune[2] = cmd.prev[5]; // the far one
            pp.tune[3] = cmd.prev[6]; // the scene's units to a metre
            pp.k[0] = 1.0f;
            pp.k[1] = (depthFlags & 1) != 0 ? 1.0f : 0.0f; // its first row is the top
            pp.k[2] = (depthFlags & 2) != 0 ? 2.0f : 1.0f; // it holds both eyes
            pp.k[3] = (depthFlags & 4) != 0 ? 1.0f : 0.0f; // 1 at the near plane
            memcpy(pp.tangents, cfg + kCfgTan + eye * 4, sizeof(pp.tangents));
        }
        else
        {
            g_depthRefused = cmd.proxy;
        }
    }
    pp.misc[0] = (float) eye;
    pp.misc[1] = cmd.eyes == 2 ? 2.0f : 1.0f;
    pp.misc[2] = source.Encoded() ? 1.0f : 0.0f;
    pp.misc[3] = cmd.topDown != 0 ? 1.0f : 0.0f;

    D3D11_MAPPED_SUBRESOURCE mapped {};
    hr = c->Map(g_cbPass, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);

    if (FAILED(hr))
    {
        depth.Release();
        source.Release();
        return;
    }

    memcpy(mapped.pData, &pp, sizeof(pp));
    c->Unmap(g_cbPass, 0);

    StateBackup backup;
    backup.Capture(c);

    ID3D11Buffer* second = nullptr;
    c->PSGetConstantBuffers(1, 1, &second);

    ID3D11ShaderResourceView* none[3] {};
    c->PSSetShaderResources(0, 3, none);
    c->OMSetRenderTargets(1, &g_matteRtv, nullptr);

    D3D11_VIEWPORT vp {};
    vp.Width = (float) (kMatteW / 2);
    vp.Height = (float) kMatteH;
    vp.TopLeftX = (float) (eye * (kMatteW / 2));
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
    c->PSSetShader(g_psMatte, nullptr, 0);
    c->PSSetConstantBuffers(1, 1, &g_cbPass);
    c->PSSetSamplers(0, 1, &g_sampler);

    ID3D11ShaderResourceView* inputs[3] = { source.srv, depth.srv, nullptr };
    c->PSSetShaderResources(0, 3, inputs);

    c->Draw(3, 0);

    // With the second eye the matte is whole. The pose it belongs to goes into the row below it,
    // in the same breath: the card will finish both together, whenever it gets to them.
    if (eye == 1)
    {
        pp.cam[3] = 2.0f;
        pp.misc[0] = cmd.moved != 0 ? 1.0f : 0.0f;

        if (cmd.moved != 0)
        {
            memcpy(pp.row0, cmd.window, 4 * sizeof(float));
            memcpy(pp.row1, cmd.window + 4, 4 * sizeof(float));
            memcpy(pp.row2, cmd.prev, 4 * sizeof(float));
        }

        if (SUCCEEDED(c->Map(g_cbPass, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
        {
            memcpy(mapped.pData, &pp, sizeof(pp));
            c->Unmap(g_cbPass, 0);

            D3D11_VIEWPORT row {};
            row.Width = 16.0f;
            row.Height = 1.0f;
            row.TopLeftY = (float) kMattePoseRow;
            row.MaxDepth = 1.0f;
            c->RSSetViewports(1, &row);
            c->Draw(3, 0);
        }
    }

    c->PSSetConstantBuffers(1, 1, &second);
    SafeRelease(second);
    backup.Restore(c);
    depth.Release();
    source.Release();

    if (eye == 1)
    {
        c->Flush();
        InterlockedIncrement(&g_matteFrames);
    }
}

// Sharpens a finished frame where it lies: the texture is copied aside and drawn back over itself
// through PSSharpen. It is opened for the length of the pass only -- it belongs to no set, and
// whoever made it may replace it between one frame and the next.
void Sharpen(const VwsCmd& cmd)
{
    if (cmd.frame == nullptr || cmd.frame == g_sharpenRefused || !(cmd.strength > 0.0f))
        return;

    ID3D11Device* device = DeviceOf(cmd.frame);

    if (device == nullptr)
        return;

    HRESULT hr = EnsureDevice(device);
    device->Release();

    if (FAILED(hr))
        return;

    Surface target;

    if (OpenSurface(target, cmd.frame, false, false, true, "the frame to sharpen", &hr) != ErrNone)
    {
        g_sharpenRefused = cmd.frame;
        return;
    }

    if (g_scratch.tex == nullptr || g_scratch.desc.Width != target.desc.Width || g_scratch.desc.Height != target.desc.Height ||
        g_scratch.desc.Format != target.desc.Format)
    {
        g_scratch.Release();

        D3D11_TEXTURE2D_DESC td = target.desc;
        td.MipLevels = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        td.CPUAccessFlags = 0;
        td.MiscFlags = 0;

        ID3D11Texture2D* copy = nullptr;
        hr = g_device->CreateTexture2D(&td, nullptr, &copy);

        if (SUCCEEDED(hr))
        {
            const int opened = OpenSurface(g_scratch, copy, false, true, false, "the sharpening pass's copy", &hr);
            copy->Release();

            if (opened != ErrNone)
                hr = E_FAIL;
        }

        if (FAILED(hr))
        {
            Log("sharpening: no working copy of a %ux%u %s frame (hr=0x%08X) -- that frame is left as it is",
                target.desc.Width, target.desc.Height, FormatName(target.desc.Format), (unsigned) hr);
            g_scratch.Release();
            g_sharpenRefused = cmd.frame;
            target.Release();
            return;
        }
    }

    ID3D11DeviceContext* c = g_ctx;
    c->CopySubresourceRegion(g_scratch.tex, 0, 0, 0, 0, target.tex, 0, nullptr);

    bool codec = false;
    const DXGI_FORMAT view = ViewFormat(target.desc.Format, &codec);

    Params params {};
    SetSize(params.frameSize, target.desc.Width, target.desc.Height);
    SetSize(params.dstSize, target.desc.Width, target.desc.Height);
    SetSize(params.workSize, target.desc.Width, target.desc.Height);
    params.eyes = cmd.eyes == 2 ? 2u : 1u;
    params.strength = cmd.strength > 1.0f ? 1.0f : cmd.strength;

    // A float frame holds scene light with no ceiling; the filter's limits want one.
    params.flags = (view == DXGI_FORMAT_R16G16B16A16_FLOAT || view == DXGI_FORMAT_R32G32B32A32_FLOAT) ? kFlagSquash : 0u;

    D3D11_MAPPED_SUBRESOURCE mapped {};
    hr = c->Map(g_cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);

    if (FAILED(hr))
    {
        target.Release();
        return;
    }

    memcpy(mapped.pData, &params, sizeof(params));
    c->Unmap(g_cb, 0);

    StateBackup backup;
    backup.Capture(c);

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
    c->PSSetShader(g_psSharpen, nullptr, 0);
    c->PSSetConstantBuffers(0, 1, &g_cb);
    c->PSSetSamplers(0, 1, &g_sampler);

    ID3D11ShaderResourceView* inputs[3] = { g_scratch.srv, nullptr, nullptr };
    c->PSSetShaderResources(0, 3, inputs);

    c->Draw(3, 0);

    backup.Restore(c);
    target.Release();
}

// The scene's depth into the depth target that is bound at this moment, across the viewport in force
// (see PSDepthFill). Raised by a camera of ours from inside its own render, after it has cleared
// and before it draws: the camera has set the target and the viewport, so this needs to know
// neither, and it works the same on an eye texture, on half of a double-wide one and on the
// window's back buffer. No colour is written and the stencil is left alone.
void DepthFill(const VwsCmd& cmd)
{
    if (cmd.frame == nullptr || cmd.frame == g_fillRefused)
    {
        InterlockedIncrement(&g_fillFailed);
        return;
    }

    ID3D11Device* device = DeviceOf(cmd.frame);

    if (device == nullptr)
    {
        InterlockedIncrement(&g_fillFailed);
        return;
    }

    HRESULT hr = EnsureDevice(device);
    device->Release();

    if (FAILED(hr))
    {
        InterlockedIncrement(&g_fillFailed);
        return;
    }

    ID3D11DeviceContext* c = g_ctx;
    ID3D11RenderTargetView* rtv = nullptr;
    ID3D11DepthStencilView* dsv = nullptr;
    c->OMGetRenderTargets(1, &rtv, &dsv);
    SafeRelease(rtv);

    if (dsv == nullptr)
    {
        InterlockedIncrement(&g_fillNoTarget);
        return;
    }

    UINT count = 1;
    D3D11_VIEWPORT vp {};
    c->RSGetViewports(&count, &vp);

    if (count == 0 || !(vp.Width >= 1.0f) || !(vp.Height >= 1.0f))
    {
        dsv->Release();
        InterlockedIncrement(&g_fillNoTarget);
        return;
    }

    Surface source;

    if (OpenSurface(source, cmd.frame, false, true, false, "the scene's depth", &hr) != ErrNone)
    {
        g_fillRefused = cmd.frame;
        dsv->Release();
        InterlockedIncrement(&g_fillFailed);
        return;
    }

    Params params {};
    SetSize(params.frameSize, source.desc.Width, source.desc.Height);
    SetSize(params.dstSize, (uint32_t) vp.Width, (uint32_t) vp.Height);
    SetSize(params.workSize, source.desc.Width, source.desc.Height);
    params.eyes = 1;
    params.flags = cmd.topDown != 0 ? kFlagTopDown : 0u;
    params.strength = cmd.strength < 0.0f ? 0.0f : (cmd.strength > 0.5f ? 0.5f : cmd.strength);
    params.mode = cmd.mode;
    memcpy(params.window[0], cmd.window, sizeof(float) * 4);
    params.prev[0][0] = vp.TopLeftX;
    params.prev[0][1] = vp.TopLeftY;
    params.prev[0][2] = vp.Width;
    params.prev[0][3] = vp.Height;

    D3D11_MAPPED_SUBRESOURCE mapped {};
    hr = c->Map(g_cb, 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped);

    if (FAILED(hr))
    {
        source.Release();
        dsv->Release();
        InterlockedIncrement(&g_fillFailed);
        return;
    }

    memcpy(mapped.pData, &params, sizeof(params));
    c->Unmap(g_cb, 0);

    StateBackup backup;
    backup.Capture(c);

    // The targets stay as the camera bound them; one viewport, and nothing cut off it.
    ID3D11ShaderResourceView* none[3] {};
    c->PSSetShaderResources(0, 3, none);
    c->RSSetViewports(1, &vp);
    c->RSSetState(g_raster);
    c->OMSetBlendState(g_blendNone, nullptr, 0xFFFFFFFF);
    c->OMSetDepthStencilState(g_depthWrite, 0);

    c->IASetInputLayout(nullptr);
    c->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    c->VSSetShader(g_vs, nullptr, 0);
    c->HSSetShader(nullptr, nullptr, 0);
    c->DSSetShader(nullptr, nullptr, 0);
    c->GSSetShader(nullptr, nullptr, 0);
    c->PSSetShader(g_psDepthFill, nullptr, 0);
    c->PSSetConstantBuffers(0, 1, &g_cb);
    c->PSSetSamplers(0, 1, &g_sampler);

    ID3D11ShaderResourceView* inputs[3] = { source.srv, nullptr, nullptr };
    c->PSSetShaderResources(0, 3, inputs);

    c->Draw(3, 0);

    backup.Restore(c);
    source.Release();
    dsv->Release();
    InterlockedIncrement(&g_fillDone);
}

void Execute(const VwsCmd& cmd)
{
    if (cmd.op == OpSharpen)
    {
        Sharpen(cmd);
        return;
    }

    if (cmd.op == OpDepthFill)
    {
        DepthFill(cmd);
        return;
    }

    if (cmd.op == OpPassthrough)
    {
        Passthrough(cmd);
        return;
    }

    if (cmd.op == OpMatte)
    {
        Matte(cmd);
        return;
    }

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

    case OpSteady:
        Steady(set, cmd);
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
    // The two that a camera raises itself, every frame, with nothing queued for them.
    if ((eventId & kEventMask) == kEventFovea)
    {
        if ((eventId & 0xFF) == 1)
            FoveaOn((uint32_t) (eventId >> 8) & 0xF, (eventId & 0x1000) != 0);
        else
            FoveaOff();

        return;
    }

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

// How much sharper than its exact average the model's input is made when the frame is shrunk for it
// (0: the average itself, as it always was; see PSDown).
// `shiftX`, `shiftY`: the model's raster is shifted by so much of the frame's pixels in the NEXT shrink
// pushed (one push takes them; see PSDown and PSGather).
VWS_EXPORT void vws_down_options(float sharpen, float shiftX, float shiftY)
{
    g_downSharpen = sharpen < 0.0f ? 0.0f : (sharpen > 2.0f ? 2.0f : sharpen);
    g_downShift[0] = shiftX;
    g_downShift[1] = shiftY;
}

// How the resolve enlarges the edit of a model that worked smaller than the frame. `follow` 0: plainly
// (as it always did). 1: along the frame's own edges -- `edge` is how unlike the frame's pixel a model
// texel's input may be and still be listened to (in the encoded 0..1 the model works in; 0.08 is a
// good start), `sharpen` how much the enlarged edit's fine part is then strengthened (0 none; the
// caller makes it follow the scale), `halo` how far that may carry it past the values of the model
// texels around it, as a share of their range (0 not at all).
// `outline`: the model's window is drawn into the picture (its edge, and the line its work is whole inside).
VWS_EXPORT void vws_resolve_options(uint32_t follow, float edge, float sharpen, float halo, uint32_t outline)
{
    InterlockedExchange(&g_resolveOutline, outline != 0 ? 1 : 0);
    g_resolveEdge = edge < 0.005f ? 0.005f : (edge > 1.0f ? 1.0f : edge);
    g_resolveSharpen = sharpen < -1.0f ? -1.0f : (sharpen > 8.0f ? 8.0f : sharpen); // (below nothing: smoothed instead, wholly at -1)
    g_resolveHalo = halo < 0.0f ? 0.0f : (halo > 4.0f ? 4.0f : halo);
    InterlockedExchange(&g_resolveFollow, follow != 0 ? 1 : 0);
}

// Sharpens `texture` where it lies (see Sharpen). `strength` 0..1; `eyes` 2 for a double-wide
// stereo frame, whose halves are then sharpened each on its own.
VWS_EXPORT int vws_push_sharpen(void* texture, float strength, uint32_t eyes)
{
    VwsCmd cmd {};
    cmd.op = OpSharpen;
    cmd.frame = texture;
    cmd.strength = strength;
    cmd.eyes = eyes;
    return Push(cmd);
}

// The scene's depth (`depth`: one number a texel, as the card made it) into whatever depth target is
// bound when the event runs, across the viewport then in force: for a camera's command buffer (see
// DepthFill). `u0`, `uw`: the part of the texture's width that is this picture (0, 1 for all of it;
// one half of a double-wide stereo one). `flip`: the target's first row is the other end of the
// picture from the texture's. `slack`: how far back the depth is pushed, as a fraction of its
// distance. `reversed`: nearer is the larger number.
VWS_EXPORT int vws_push_depth_fill(void* depth, float u0, float uw, uint32_t flip, float slack, uint32_t reversed)
{
    VwsCmd cmd {};
    cmd.op = OpDepthFill;
    cmd.frame = depth;
    cmd.window[0] = u0;
    cmd.window[1] = 0.0f;
    cmd.window[2] = uw;
    cmd.window[3] = 1.0f;
    cmd.strength = slack;
    cmd.topDown = flip;
    cmd.mode = reversed != 0 ? 1u : 0u;
    return Push(cmd);
}

// How often it has run, how often there was no depth target or viewport to fill, and how often the
// depth texture could not be used.
VWS_EXPORT void vws_depth_fill_status(uint32_t* done, uint32_t* noTarget, uint32_t* failed)
{
    if (done != nullptr)
        *done = (uint32_t) g_fillDone;

    if (noTarget != nullptr)
        *noTarget = (uint32_t) g_fillNoTarget;

    if (failed != nullptr)
        *failed = (uint32_t) g_fillFailed;
}

// The numbers the passthrough pass works from (see CamField). Main thread, whenever they change.
VWS_EXPORT void vws_cam_configure(const float* values, uint32_t count)
{
    if (values == nullptr)
        return;

    AcquireSRWLockExclusive(&g_camLock);
    memcpy(g_camConfig, values, sizeof(float) * (count < kCamFloats ? count : kCamFloats));
    g_camConfigured = true;
    ReleaseSRWLockExclusive(&g_camLock);
}

// Starts taking the camera's frames: `getFrameBuffer` is IVRTrackedCamera's GetVideoStreamFrameBuffer
// and `handle` a stream the caller acquired and will release after vws_cam_stop.
VWS_EXPORT int vws_cam_start(void* getFrameBuffer, uint64_t handle, uint32_t width, uint32_t height)
{
    if (getFrameBuffer == nullptr || width == 0 || height == 0 || width > 8192 || height > 8192)
        return 0;

    if (g_camThread != nullptr)
        return 1;

    AcquireSRWLockExclusive(&g_camLock);
    CamBuffers(width, height);
    ReleaseSRWLockExclusive(&g_camLock);

    g_camFn = (CamFrameFn) getFrameBuffer;
    g_camHandle = handle;
    InterlockedExchange(&g_camError, 0);
    const LONG generation = InterlockedIncrement(&g_camGen);
    g_camThread = CreateThread(nullptr, 0, CamThread, (void*) (intptr_t) generation, 0, nullptr);

    if (g_camThread == nullptr)
    {
        InterlockedIncrement(&g_camGen);
        return 0;
    }

    return 1;
}

VWS_EXPORT void vws_cam_stop()
{
    if (g_camThread == nullptr)
        return;

    // A thread that is stuck in a call and does not end in time is left behind: it belongs to a
    // start that is over, and ends itself when the call lets it go.
    InterlockedIncrement(&g_camGen);

    if (WaitForSingleObject(g_camThread, 3000) != WAIT_OBJECT_0)
        Log("passthrough: the camera thread did not end within three seconds -- it was stuck in a call");

    CloseHandle(g_camThread);
    g_camThread = nullptr;
}

VWS_EXPORT void vws_cam_status(uint32_t* frames, uint32_t* uploads, int32_t* error)
{
    if (frames != nullptr)
        *frames = (uint32_t) g_camFrames;

    if (uploads != nullptr)
        *uploads = (uint32_t) g_camUploads;

    if (error != nullptr)
        *error = (int32_t) g_camError;
}

// A camera frame handed in directly, grey, with the head's pose at its moment (3x4, or null): what
// the worker would have delivered. For the offline checks.
VWS_EXPORT void vws_cam_inject(const uint8_t* grey, uint32_t width, uint32_t height, const float* pose)
{
    if (grey == nullptr || width == 0 || height == 0)
        return;

    AcquireSRWLockExclusive(&g_camLock);
    CamBuffers(width, height);
    memcpy(g_camFront, grey, (size_t) width * height);

    if (pose != nullptr)
        memcpy(g_camFrontPose, pose, sizeof(g_camFrontPose));

    g_camFrontPoseValid = pose != nullptr;
    g_camFresh = true;
    ReleaseSRWLockExclusive(&g_camLock);
}

// One eye of the matte the overlay is cut by (see Matte): `source` is the finished frame, `eyes`
// 2 when it holds both eyes side by side. `headPose`, twelve numbers or null, is the head in the
// room as that frame was rendered. `fromAlpha`: the room has already been drawn into the frame and
// the frame's alpha says where (1 = the game's own picture), so the key colour is not looked for.
// `depth`, or null, is the scene's depth buffer for the same frame as the card keeps it, and
// `depthInfo` four numbers about it: the near plane and the far one in the scene's units, how many
// of those units make a metre, and flags (1 its first row is the top of the picture, 2 it holds
// both eyes side by side, 4 it runs from 1 at the near plane to 0 at the far one).
VWS_EXPORT int vws_push_matte(void* source, uint32_t eyes, uint32_t eye, uint32_t srgb, uint32_t topDown, const float* headPose, uint32_t fromAlpha,
                              void* depth, const float* depthInfo)
{
    VwsCmd cmd {};
    cmd.op = OpMatte;
    cmd.strength = fromAlpha != 0 ? 1.0f : 0.0f;
    cmd.frame = source;

    if (depth != nullptr && depthInfo != nullptr)
    {
        cmd.proxy = depth;
        memcpy(cmd.prev + 4, depthInfo, 4 * sizeof(float));
    }

    cmd.eyes = eyes;
    cmd.which = eye;
    cmd.srgbMask = srgb != 0 ? kFrame : 0;
    cmd.topDown = topDown;

    if (headPose != nullptr)
    {
        memcpy(cmd.window, headPose, 8 * sizeof(float));
        memcpy(cmd.prev, headPose + 8, 4 * sizeof(float));
        cmd.moved = 1;
    }

    return Push(cmd);
}

// How the overlay stands: 0 not wanted, 1 waiting for the game's first frame, 2 no device of its
// own, 3 no SteamVR overlay interface, 4 SteamVR refused it (`error`), 5 running.
VWS_EXPORT void vws_overlay_status(int32_t* state, uint32_t* frames, int32_t* error)
{
    if (state != nullptr)
        *state = (int32_t) g_ovState;

    if (frames != nullptr)
        *frames = (uint32_t) g_ovFrames;

    if (error != nullptr)
        *error = (int32_t) g_ovError;
}

// How near the room and the scene are taken to be straight ahead, in 1/metres; below zero when
// that one is not being read.
VWS_EXPORT void vws_overlay_depth(float* room, float* scene)
{
    if (room != nullptr)
        *room = g_ovDepthRoom / 1000.0f;

    if (scene != nullptr)
        *scene = g_ovDepthScene / 1000.0f;
}

// The room's depth grid as bytes (RGBA, `side` x `side`; see vwsdepth::Solver::Solve), as last
// worked out. 1 when `out` was filled; `micros`, how long working it out took.
VWS_EXPORT int vws_depth_read(uint8_t* out, uint32_t capacity, uint32_t* side, uint32_t* micros)
{
    int filled = 0;

    if (g_depthSerial != 0 && out != nullptr && capacity >= sizeof(g_depthOut))
    {
        AcquireSRWLockExclusive(&g_depthLock);
        memcpy(out, g_depthOut, sizeof(g_depthOut));
        ReleaseSRWLockExclusive(&g_depthLock);
        filled = 1;
    }

    if (side != nullptr)
        *side = kGridN;

    if (micros != nullptr)
        *micros = (uint32_t) g_depthMicros;

    return filled;
}

// The same worked out here and now from a frame handed in (grey, both lenses side by side) and
// the passthrough's numbers, `times` times over as if the frame stood still. Returns the
// microseconds one took. For the offline checks, and for trying it on a saved frame.
VWS_EXPORT uint32_t vws_depth_solve(const uint8_t* grey, uint32_t width, uint32_t height, const float* cfg, uint8_t* out, uint32_t times)
{
    if (grey == nullptr || cfg == nullptr || out == nullptr || width < 64 || height < 64)
        return 0;

    vwsdepth::Solver* solver = new vwsdepth::Solver();
    solver->Solve(grey, width, height, cfg, out);
    const double start = Seconds();

    for (uint32_t i = 1; i < (times < 2 ? 2u : times); ++i)
        solver->Solve(grey, width, height, cfg, out);

    const double took = (Seconds() - start) / ((times < 2 ? 2u : times) - 1);
    delete solver;
    return (uint32_t) (took * 1e6);
}

// ---- foveated shading -----------------------------------------------------------------------------

// The event a camera raises (through vws_event_func) to have what it draws next shaded by where
// the eyes look, and the one that ends that.
VWS_EXPORT int vws_fovea_event(uint32_t on)
{
    return kEventFovea | (on != 0 ? 1 : 2);
}

// "On" for a camera by number (0: the one the scene is seen through) and for which of its passes.
VWS_EXPORT int vws_fovea_event_for(uint32_t camera, uint32_t opaque)
{
    return kEventFovea | 1 | (int) ((camera & 0xF) << 8) | (opaque != 0 ? 0x1000 : 0);
}

// The pixel shader's runs as last counted over the scene camera's passes, four numbers: the opaque
// pass with the rates in force and without, then the transparent pass with and without; and how
// many times each has been counted.
VWS_EXPORT void vws_fovea_measured(uint64_t* runs, uint32_t* times)
{
    for (int pass = 0; pass < 2; ++pass)
    {
        for (int without = 0; without < 2; ++without)
        {
            if (runs != nullptr)
                runs[pass * 2 + without] = (uint64_t) g_fovPs[pass][without];

            if (times != nullptr)
                times[pass * 2 + without] = (uint32_t) g_fovPsTimes[pass][without];
        }
    }
}

// What "on" works from (FovField), and any texture of the game's device. Main thread, every frame.
VWS_EXPORT void vws_fovea_configure(const float* values, uint32_t count, void* anyTexture)
{
    if (values == nullptr)
        return;

    AcquireSRWLockExclusive(&g_fovLock);
    memcpy(g_fovCfg, values, sizeof(float) * (count < kFovFloats ? count : kFovFloats));
    g_fovAnyTexture = anyTexture;
    ReleaseSRWLockExclusive(&g_fovLock);
}

// `state`: 0 off, 1 at work, 2 this card cannot, 3 it failed. The target it last worked on, its
// samples a pixel, how many in a hundred of its tiles are shaded coarsely, and how often it was
// switched on.
VWS_EXPORT void vws_fovea_status(int32_t* state, uint32_t* width, uint32_t* height, uint32_t* samples, uint32_t* coarse, uint32_t* ons)
{
    if (state != nullptr)
        *state = (int32_t) g_fovState;

    if (width != nullptr)
        *width = (uint32_t) g_fovW;

    if (height != nullptr)
        *height = (uint32_t) g_fovH;

    if (samples != nullptr)
        *samples = (uint32_t) g_fovSamples;

    if (coarse != nullptr)
        *coarse = (uint32_t) g_fovCoarse;

    if (ons != nullptr)
        *ons = (uint32_t) g_fovOns;
}

// How often it was asked to switch on and did not: with nothing bound to draw into, and with a
// target too small to be the picture; and how often it went by the depth target's size.
VWS_EXPORT void vws_fovea_asked(uint32_t* asked, uint32_t* noDevice)
{
    if (asked != nullptr)
        *asked = (uint32_t) g_fovAsked;

    if (noDevice != nullptr)
        *noDevice = (uint32_t) g_fovNoDevice;
}

VWS_EXPORT void vws_fovea_misses(uint32_t* noTarget, uint32_t* small_, uint32_t* byDepth)
{
    if (noTarget != nullptr)
        *noTarget = (uint32_t) g_fovNoTarget;

    if (small_ != nullptr)
        *small_ = (uint32_t) g_fovSmall;

    if (byDepth != nullptr)
        *byDepth = (uint32_t) g_fovByDepth;
}

// ---- the wearer's hands ---------------------------------------------------------------------------

// Loads ONNX Runtime and the two hand models from `folder` (ending in a backslash), or from beside
// this DLL when it is null. 1 when they are ready; vws_hand_error says why not.
VWS_EXPORT int vws_hand_load(const wchar_t* folder)
{
    wchar_t own[MAX_PATH * 2] = {};

    if (folder == nullptr)
    {
        HMODULE me = nullptr;

        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR) &g_handNets, &me) ||
            GetModuleFileNameW(me, own, MAX_PATH * 2 - 1) == 0)
            return 0;

        wchar_t* slash = wcsrchr(own, L'\\');

        if (slash == nullptr)
            return 0;

        slash[1] = 0;
        folder = own;
    }

    AcquireSRWLockExclusive(&g_handLock);
    const bool ok = g_handNets.Load(folder);
    ReleaseSRWLockExclusive(&g_handLock);

    if (!ok)
        Log("hands: %s", g_handNets.error);
    else
        Log("hands: landmark model %s%s", g_handNets.full ? "hand-points-full.onnx (the full-size one)" :
                                          (g_handNets.wantFull ? "hand-points.onnx (the full-size one was asked for and is not there, or would not load)" : "hand-points.onnx"),
            g_handNets.mercury != nullptr ? "; a hand once found is followed with hand-mercury.onnx" :
            (g_handNets.wantMercury ? "; hand-mercury.onnx was asked for and is not there, or would not load" : ""));

    return ok ? 1 : 0;
}

// Before vws_hand_load, which models are to be used where their files are there: 1 the full-size
// landmark model, 2 Mercury's keypoint network for following a hand.
VWS_EXPORT void vws_hand_prefer(uint32_t which)
{
    AcquireSRWLockExclusive(&g_handLock);
    g_handNets.wantFull = (which & 1) != 0;
    g_handNets.wantMercury = (which & 2) != 0;
    ReleaseSRWLockExclusive(&g_handLock);
}

// 1 when Mercury's keypoint network is loaded.
VWS_EXPORT int vws_hand_mercury()
{
    return g_handNets.mercury != nullptr ? 1 : 0;
}

// 1 when the full-size landmark model is the one loaded.
VWS_EXPORT int vws_hand_full()
{
    return g_handNets.full ? 1 : 0;
}

VWS_EXPORT void vws_hand_error(char* out, uint32_t capacity)
{
    if (out != nullptr && capacity > 0)
        snprintf(out, capacity, "%s", g_handNets.error);
}

// Whether the hands are looked for (in the camera frames vws_cam_start brings), on every how many
// of them, and the tracker's numbers (vwshand::Setting; null leaves them as they are).
VWS_EXPORT void vws_hand_configure(uint32_t on, uint32_t every, const float* values, uint32_t count)
{
    AcquireSRWLockExclusive(&g_handLock);

    if (values != nullptr)
    {
        if (!g_handSetGiven)
            vwshand::Defaults(g_handSet);

        memcpy(g_handSet, values, sizeof(float) * (count < (uint32_t) vwshand::kSettings ? count : (uint32_t) vwshand::kSettings));
        g_handSetGiven = true;
    }

    if (on == 0)
        memset(g_handOut, 0, sizeof(g_handOut));

    ReleaseSRWLockExclusive(&g_handLock);
    InterlockedExchange(&g_handEvery, every < 1 ? 1 : (LONG) every);
    InterlockedExchange(&g_handOn, on != 0 ? 1 : 0);
}

// The hands as last worked out: vwshand::kHands times 67 floats (see vwshand::HandOut). 1 when
// `out` was filled. `serial` counts the frames worked out, `micros` is how long the last took,
// `ageMs` how long ago its camera frame arrived, `inRoom` whether the points are in the room (0: in
// the head's space, the head's place never having been known).
VWS_EXPORT int vws_hand_read(float* out, uint32_t capacity, uint32_t* serial, uint32_t* micros, float* ageMs, uint32_t* inRoom)
{
    const uint32_t floats = sizeof(g_handOut) / sizeof(float);
    int filled = 0;
    AcquireSRWLockShared(&g_handLock);

    if (out != nullptr && capacity >= floats)
    {
        memcpy(out, g_handOut, sizeof(g_handOut));
        filled = 1;
    }

    if (ageMs != nullptr)
        *ageMs = g_handOutAt > 0.0 ? (float) ((Seconds() - g_handOutAt) * 1000.0) : -1.0f;

    if (inRoom != nullptr)
        *inRoom = g_handOutInRoom ? 1u : 0u;

    ReleaseSRWLockShared(&g_handLock);

    if (serial != nullptr)
        *serial = (uint32_t) g_handSerial;

    if (micros != nullptr)
        *micros = (uint32_t) g_handMicros;

    return filled;
}

// The same worked out here and now from a frame handed in (grey, both lenses side by side), the
// passthrough's numbers, the tracker's (or null) and the head's pose (3x4, or null): `times` times
// over, a sixtieth of a second apart, as if the frame stood still -- the first finds the hands,
// the rest follow them. Returns the microseconds a following frame took. For the offline checks,
// and for trying it on a saved frame.
VWS_EXPORT uint32_t vws_hand_solve(const uint8_t* grey, uint32_t width, uint32_t height, const float* cfg, const float* settings,
                                   const float* head, uint32_t times, float* out)
{
    if (grey == nullptr || cfg == nullptr || out == nullptr || g_handNets.hand == nullptr)
        return 0;

    vwshand::Tracker* tracker = new vwshand::Tracker();
    tracker->nets = &g_handNets;

    if (settings != nullptr)
        memcpy(tracker->set, settings, sizeof(tracker->set));

    vwshand::HandOut hands[vwshand::kHands];
    const uint32_t runs = times < 3 ? 3u : times;
    double following = 0.0;

    for (uint32_t i = 0; i < runs; ++i)
    {
        const double start = Seconds();
        tracker->Track(grey, width, height, cfg, head, (double) i / 60.0, hands);

        if (i >= 2)
            following += Seconds() - start;
    }

    memcpy(out, hands, sizeof(hands));
    delete tracker;
    return (uint32_t) (following / (runs - 2) * 1e6);
}

// One frame of a run of frames, through a tracker that is kept from call to call (`fresh` starts
// it anew): a recording played back as the camera would have given it. Returns the microseconds
// the frame took. For trying the tracker on recorded frames.
static vwshand::Tracker* g_stepTracker = nullptr;

// How far each finger of each hand is bent, by Mercury (see vwshand::Tracker::Curls): 12 floats, in
// the order vws_hand_read gives the hands. All zero where Mercury is not following.
VWS_EXPORT void vws_hand_curls(float* out)
{
    if (out == nullptr)
        return;

    AcquireSRWLockShared(&g_handLock);
    memcpy(out, g_handCurls, sizeof(g_handCurls));
    ReleaseSRWLockShared(&g_handLock);
}

// The same of the tracker vws_hand_step plays a recording through.
VWS_EXPORT void vws_hand_step_curls(float* out)
{
    if (out != nullptr && g_stepTracker != nullptr)
        g_stepTracker->Curls(out);
}

// For looking into a played-back frame: for each of the two hands, each lens's depth agreement and
// spread (see Fuse), each lens's rightness, and whether each lens's reading was turned round: 8
// floats a hand.
VWS_EXPORT void vws_hand_step_notes(float* out)
{
    if (out == nullptr || g_stepTracker == nullptr)
        return;

    for (int i = 0; i < vwshand::kHands; ++i)
    {
        memcpy(out + i * 8, g_stepTracker->hand[i].note, sizeof(float) * 6);
        out[i * 8 + 6] = g_stepTracker->hand[i].turned[0] ? 1.0f : 0.0f;
        out[i * 8 + 7] = g_stepTracker->hand[i].turned[1] ? 1.0f : 0.0f;
    }
}

VWS_EXPORT uint32_t vws_hand_step(const uint8_t* grey, uint32_t width, uint32_t height, const float* cfg, const float* settings, const float* head,
                                  double time, uint32_t fresh, float* out)
{
    vwshand::Tracker*& tracker = g_stepTracker;

    if (grey == nullptr || cfg == nullptr || out == nullptr || g_handNets.hand == nullptr)
        return 0;

    if (tracker == nullptr || fresh != 0)
    {
        delete tracker;
        tracker = new vwshand::Tracker();
        tracker->nets = &g_handNets;
    }

    if (settings != nullptr)
        memcpy(tracker->set, settings, sizeof(tracker->set));

    vwshand::HandOut hands[vwshand::kHands];
    const double start = Seconds();
    tracker->Track(grey, width, height, cfg, head, time, hands);
    memcpy(out, hands, sizeof(hands));
    return (uint32_t) ((Seconds() - start) * 1e6);
}

// The newest camera frame as bytes (grey, both lenses side by side). 1 when `out` was filled.
VWS_EXPORT int vws_cam_read(uint8_t* out, uint32_t capacity, uint32_t* width, uint32_t* height)
{
    int filled = 0;
    AcquireSRWLockExclusive(&g_camLock);

    if (g_camFront != nullptr && out != nullptr && capacity >= g_camW * g_camH)
    {
        memcpy(out, g_camFront, (size_t) g_camW * g_camH);
        filled = 1;
    }

    if (width != nullptr)
        *width = g_camW;

    if (height != nullptr)
        *height = g_camH;

    ReleaseSRWLockExclusive(&g_camLock);
    return filled;
}

// The overlay's picture as bytes (RGBA), for the offline checks: asks for the next one drawn and
// returns the last one kept. 1 when `out` was filled.
VWS_EXPORT int vws_overlay_read(uint8_t* out, uint32_t capacity, uint32_t* width, uint32_t* height)
{
    InterlockedExchange(&g_ovDebugWant, 1);

    int filled = 0;
    AcquireSRWLockExclusive(&g_camLock);

    if (g_ovDebug != nullptr && out != nullptr && capacity >= g_ovDebugW * g_ovDebugH * 4)
    {
        memcpy(out, g_ovDebug, (size_t) g_ovDebugW * g_ovDebugH * 4);
        filled = 1;
    }

    if (width != nullptr)
        *width = g_ovDebugW;

    if (height != nullptr)
        *height = g_ovDebugH;

    ReleaseSRWLockExclusive(&g_camLock);
    return filled;
}

// One eye of the passthrough pass (see Passthrough). `eyeToRoom`, twelve numbers or null, is that
// eye's space in the room's as the frame was rendered.
VWS_EXPORT int vws_push_passthrough(void* target, uint32_t eyes, uint32_t eye, uint32_t srgb, uint32_t topDown, const float* eyeToRoom)
{
    VwsCmd cmd {};
    cmd.op = OpPassthrough;
    cmd.frame = target;
    cmd.eyes = eyes;
    cmd.which = eye;
    cmd.srgbMask = srgb != 0 ? kFrame : 0;
    cmd.topDown = topDown;

    if (eyeToRoom != nullptr)
    {
        memcpy(cmd.window, eyeToRoom, 8 * sizeof(float));
        memcpy(cmd.prev, eyeToRoom + 8, 4 * sizeof(float));
        cmd.moved = 1;
    }

    return Push(cmd);
}

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

    if (resolve == 0)
    {
        cmd.prev[0] = g_downShift[0];
        cmd.prev[1] = g_downShift[1];
        g_downShift[0] = g_downShift[1] = 0.0f;
    }

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

    if (resolve == 0)
    {
        cmd.prev[0] = g_downShift[0];
        cmd.prev[1] = g_downShift[1];
        g_downShift[0] = g_downShift[1] = 0.0f;
    }

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

// Before a resolve of the same set: the model's edit of this pass is blended into what was kept of it
// from the frames before, and the resolve then lays that on the frame instead of this frame's edit
// alone (see Steady). `motion`: the model-size motion vectors (how far each point has moved since the
// last frame, in eye widths and heights, y up the picture); `topDown`: their first row is the top of
// the picture. `pass`: which of the model's passes over this frame this is (each has an edit of its
// own to keep). `take`: how much of the new edit is taken, 0..1. `fresh`: nothing kept is to be used.
//
// `full`: kept at the FRAME's size, with this frame's shrink having been made shifted by `shiftX`,
// `shiftY` of the frame's pixels -- detail built up over frames (see PSGather). Not with a window.
// `own`: with `full`, how much each of the frame's pixels keeps to the frame in which it was the one looked at, 0..1.
VWS_EXPORT int vws_push_steady(uint32_t set, uint32_t eyes, void* motion, float take, uint32_t topDown, const float* window, uint32_t pass, uint32_t fresh,
                               uint32_t full, float shiftX, float shiftY, float own)
{
    VwsCmd cmd {};
    cmd.op = OpSteady;
    cmd.set = set;
    cmd.eyes = eyes;
    cmd.frame = motion;
    cmd.strength = take;
    cmd.topDown = topDown;
    cmd.which = pass;
    cmd.mode = (fresh != 0 ? 1u : 0u) | (full != 0 && window == nullptr ? 2u : 0u);
    cmd.prev[0] = shiftX;
    cmd.prev[1] = shiftY;
    cmd.prev[2] = own;

    if (window != nullptr)
    {
        memcpy(cmd.window, window, sizeof(cmd.window));
        cmd.windowed = 1;
    }

    return Push(cmd);
}

// How often the edit has been kept over frames, how often of those it began anew, and how often it could not be.
VWS_EXPORT void vws_steady_status(uint32_t* runs, uint32_t* fresh, uint32_t* failed)
{
    if (runs != nullptr)
        *runs = (uint32_t) g_steadyRuns;

    if (fresh != nullptr)
        *fresh = (uint32_t) g_steadyFresh;

    if (failed != nullptr)
        *failed = (uint32_t) g_steadyFailed;
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

#include "vws_flip.h"

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
        DisableThreadLibraryCalls(module);

    return TRUE;
}
