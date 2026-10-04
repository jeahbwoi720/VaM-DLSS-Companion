// Offline harness for VamDlssNrWorkScaleNative.dll.
//
// Drives the DLL exactly as the plugin does -- vws_push, then the render-event callback with the id
// it returned -- against a real D3D11 device created the way Unity creates VaM's (SINGLETHREADED),
// with textures in the formats Unity allocates (TYPELESS 8-bit for sRGB render textures, half float
// for the frame). Every result is read back and compared with a CPU reference.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <limits>
#include <vector>

#include "vws_vs.h"
#include "vws_ps_down.h"

#pragma pack(push, 8)
struct VwsCmd
{
    uint32_t op;
    uint32_t set;
    void* frame;
    void* proxy;
    void* model;
    void* result;
    uint32_t srgbMask;
    uint32_t eyes;
    float strength;
    uint32_t mode;
    uint32_t which;
    uint32_t token;
    uint32_t reserved[2];
    float window[8];
    float feather;
    uint32_t windowed;
    float prev[8];
    uint32_t moved;
    uint32_t topDown;
};

struct VwsStatus
{
    int32_t ready;
    int32_t error;
    uint32_t token;
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

typedef uint32_t (*AbiFn)();
typedef void* (*EventFuncFn)();
typedef int (*PushFn)(const VwsCmd*);
typedef void (*StatusFn)(uint32_t, VwsStatus*);
typedef int (*DrainFn)(char*, int);
typedef int (*PushRegisterFn)(uint32_t, void*, void*, void*, void*, uint32_t, uint32_t, uint32_t);
typedef int (*PushPassFn)(uint32_t, uint32_t, uint32_t, float, uint32_t);
typedef int (*PushReleaseFn)(uint32_t);
typedef int (*PollFn)(uint32_t, int*, int*, uint32_t*, int*);
typedef void(__stdcall* RenderEventFn)(int);

static AbiFn vws_abi;
static EventFuncFn vws_event_func;
static PushFn vws_push;
static StatusFn vws_status;
static DrainFn vws_drain_log;
static PushRegisterFn vws_push_register;
static PushPassFn vws_push_pass;
static PushReleaseFn vws_push_release;
static PollFn vws_poll;
static RenderEventFn g_event;

static uint32_t g_token = 0;
static bool g_warp = false;
static ID3D11Device* g_dev;
static ID3D11DeviceContext* g_ctx;
static int g_failures = 0;
static int g_checks = 0;

#define CHECK(cond, ...)                                                                           \
    do                                                                                             \
    {                                                                                              \
        ++g_checks;                                                                                \
        if (!(cond))                                                                               \
        {                                                                                          \
            ++g_failures;                                                                          \
            printf("  FAIL %s:%d: ", __FUNCTION__, __LINE__);                                      \
            printf(__VA_ARGS__);                                                                   \
            printf("\n");                                                                          \
        }                                                                                          \
    } while (0)

static void Drain(bool print)
{
    char buf[8192];

    while (vws_drain_log(buf, sizeof(buf)) > 0)
    {
        if (print)
            printf("%s", buf);
    }
}

static void Run(const VwsCmd& cmd)
{
    const int id = vws_push(&cmd);
    g_event(id);
}

// ---- half float ---------------------------------------------------------------------------------
static uint16_t FloatToHalf(float f)
{
    uint32_t x;
    memcpy(&x, &f, 4);
    const uint32_t sign = (x >> 16) & 0x8000u;
    const int32_t exp = (int32_t) ((x >> 23) & 0xFF) - 127 + 15;
    uint32_t mant = x & 0x7FFFFFu;

    if (((x >> 23) & 0xFF) == 0xFF)
        return (uint16_t) (sign | 0x7C00u | (mant != 0 ? 0x200u : 0u)); // inf / nan

    if (exp >= 31)
        return (uint16_t) (sign | 0x7C00u);

    if (exp <= 0)
    {
        if (exp < -10)
            return (uint16_t) sign;

        mant |= 0x800000u;
        const uint32_t shift = (uint32_t) (14 - exp);
        uint32_t half = mant >> shift;

        if ((mant >> (shift - 1)) & 1u)
            half++;

        return (uint16_t) (sign | half);
    }

    uint32_t half = ((uint32_t) exp << 10) | (mant >> 13);

    if (mant & 0x1000u)
        half++;

    return (uint16_t) (sign | half);
}

static float HalfToFloat(uint16_t h)
{
    const uint32_t sign = (uint32_t) (h & 0x8000u) << 16;
    uint32_t exp = (h >> 10) & 0x1Fu;
    uint32_t mant = h & 0x3FFu;
    uint32_t x;

    if (exp == 0)
    {
        if (mant == 0)
        {
            x = sign;
        }
        else
        {
            exp = 1;

            while ((mant & 0x400u) == 0)
            {
                mant <<= 1;
                exp--;
            }

            mant &= 0x3FFu;
            x = sign | ((exp + 127 - 15) << 23) | (mant << 13);
        }
    }
    else if (exp == 31)
    {
        x = sign | 0x7F800000u | (mant << 13);
    }
    else
    {
        x = sign | ((exp + 127 - 15) << 23) | (mant << 13);
    }

    float f;
    memcpy(&f, &x, 4);
    return f;
}

// ---- colour reference ---------------------------------------------------------------------------
static float Saturate(float v)
{
    if (!(v == v))
        return 0.0f; // NaN, as D3D's saturate

    return std::min(1.0f, std::max(0.0f, v));
}

static float L2S(float v)
{
    v = Saturate(v);
    return v <= 0.0031308f ? v * 12.92f : 1.055f * powf(v, 1.0f / 2.4f) - 0.055f;
}

static float S2L(float v)
{
    v = Saturate(v);
    return v <= 0.04045f ? v / 12.92f : powf((v + 0.055f) / 1.055f, 2.4f);
}

static int ToByte(float v)
{
    return (int) floorf(Saturate(v) * 255.0f + 0.5f);
}

struct Image
{
    uint32_t w = 0, h = 0;
    std::vector<float> px; // rgba, linear

    Image() {}
    Image(uint32_t w_, uint32_t h_) : w(w_), h(h_), px((size_t) w_ * h_ * 4, 0.0f) {}
    float* at(uint32_t x, uint32_t y) { return &px[((size_t) y * w + x) * 4]; }
    const float* at(uint32_t x, uint32_t y) const { return &px[((size_t) y * w + x) * 4]; }
};

struct Bytes
{
    uint32_t w = 0, h = 0;
    std::vector<uint8_t> px; // rgba, as stored

    Bytes() {}
    Bytes(uint32_t w_, uint32_t h_) : w(w_), h(h_), px((size_t) w_ * h_ * 4, 0) {}
    uint8_t* at(uint32_t x, uint32_t y) { return &px[((size_t) y * w + x) * 4]; }
    const uint8_t* at(uint32_t x, uint32_t y) const { return &px[((size_t) y * w + x) * 4]; }
};

// ---- textures -----------------------------------------------------------------------------------
static ID3D11Texture2D* MakeTexture(uint32_t w, uint32_t h, DXGI_FORMAT format, UINT bind)
{
    D3D11_TEXTURE2D_DESC d {};
    d.Width = w;
    d.Height = h;
    d.MipLevels = 1;
    d.ArraySize = 1;
    d.Format = format;
    d.SampleDesc.Count = 1;
    d.Usage = D3D11_USAGE_DEFAULT;
    d.BindFlags = bind;
    ID3D11Texture2D* t = nullptr;
    const HRESULT hr = g_dev->CreateTexture2D(&d, nullptr, &t);

    if (FAILED(hr))
    {
        printf("CreateTexture2D failed 0x%08X\n", (unsigned) hr);
        exit(2);
    }

    return t;
}

static const UINT kRT = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;

static void UploadHalf(ID3D11Texture2D* t, const Image& img)
{
    std::vector<uint16_t> data((size_t) img.w * img.h * 4);

    for (size_t i = 0; i < data.size(); ++i)
        data[i] = FloatToHalf(img.px[i]);

    g_ctx->UpdateSubresource(t, 0, nullptr, data.data(), img.w * 8, 0);
}

static void UploadBytes(ID3D11Texture2D* t, const Bytes& img)
{
    g_ctx->UpdateSubresource(t, 0, nullptr, img.px.data(), img.w * 4, 0);
}

static Bytes ReadBytes(ID3D11Texture2D* t)
{
    D3D11_TEXTURE2D_DESC d {};
    t->GetDesc(&d);

    D3D11_TEXTURE2D_DESC sd = d;
    sd.Usage = D3D11_USAGE_STAGING;
    sd.BindFlags = 0;
    sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;

    if (sd.Format == DXGI_FORMAT_R8G8B8A8_TYPELESS)
        sd.Format = DXGI_FORMAT_R8G8B8A8_UNORM;

    ID3D11Texture2D* staging = nullptr;
    g_dev->CreateTexture2D(&sd, nullptr, &staging);
    g_ctx->CopyResource(staging, t);

    Bytes out(d.Width, d.Height);
    D3D11_MAPPED_SUBRESOURCE m {};

    if (SUCCEEDED(g_ctx->Map(staging, 0, D3D11_MAP_READ, 0, &m)))
    {
        for (uint32_t y = 0; y < d.Height; ++y)
            memcpy(out.at(0, y), (const uint8_t*) m.pData + (size_t) y * m.RowPitch, (size_t) d.Width * 4);

        g_ctx->Unmap(staging, 0);
    }

    staging->Release();
    return out;
}

// The frame as the passes see it: the half-quantised values, bounded to [0,1], NaN as 0.
static Image Seen(const Image& frame)
{
    Image out(frame.w, frame.h);

    for (size_t i = 0; i < frame.px.size(); ++i)
        out.px[i] = Saturate(HalfToFloat(FloatToHalf(frame.px[i])));

    return out;
}

// CPU reference of the area average (texels of `src` per pixel of the w x h destination).
static Image AreaReference(const Image& src, uint32_t w, uint32_t h)
{
    Image out(w, h);
    const double rx = (double) src.w / w;
    const double ry = (double) src.h / h;

    for (uint32_t y = 0; y < h; ++y)
    {
        for (uint32_t x = 0; x < w; ++x)
        {
            const double x0 = x * rx, x1 = x0 + rx, y0 = y * ry, y1 = y0 + ry;
            double acc[4] = { 0, 0, 0, 0 };
            double wsum = 0;

            for (int j = (int) floor(y0); j < (int) ceil(y1); ++j)
            {
                const double wy = std::min(y1, j + 1.0) - std::max(y0, (double) j);

                if (wy <= 0)
                    continue;

                for (int i = (int) floor(x0); i < (int) ceil(x1); ++i)
                {
                    const double wx = std::min(x1, i + 1.0) - std::max(x0, (double) i);

                    if (wx <= 0)
                        continue;

                    const float* p = src.at((uint32_t) std::min(i, (int) src.w - 1),
                                            (uint32_t) std::min(j, (int) src.h - 1));

                    for (int c = 0; c < 4; ++c)
                        acc[c] += p[c] * wx * wy;

                    wsum += wx * wy;
                }
            }

            for (int c = 0; c < 4; ++c)
                out.at(x, y)[c] = (float) (acc[c] / wsum);
        }
    }

    return out;
}

// Largest per-channel difference between stored bytes and a linear reference (rgb through sRGB).
static int MaxDiffEncoded(const Bytes& got, const Image& linearRef, bool checkAlpha, int* atX = nullptr,
                          int* atY = nullptr)
{
    int worst = 0;

    for (uint32_t y = 0; y < got.h; ++y)
    {
        for (uint32_t x = 0; x < got.w; ++x)
        {
            const uint8_t* g = got.at(x, y);
            const float* r = linearRef.at(x, y);

            for (int c = 0; c < (checkAlpha ? 4 : 3); ++c)
            {
                const int want = c < 3 ? ToByte(L2S(r[c])) : ToByte(r[c]);
                const int diff = abs((int) g[c] - want);

                if (diff > worst)
                {
                    worst = diff;

                    if (atX != nullptr)
                        *atX = (int) x;

                    if (atY != nullptr)
                        *atY = (int) y;
                }
            }
        }
    }

    return worst;
}

static Image TestPattern(uint32_t w, uint32_t h, uint32_t seed)
{
    Image img(w, h);
    uint32_t s = seed * 2654435761u + 12345u;

    for (uint32_t y = 0; y < h; ++y)
    {
        for (uint32_t x = 0; x < w; ++x)
        {
            float* p = img.at(x, y);

            for (int c = 0; c < 4; ++c)
            {
                s = s * 1664525u + 1013904223u;
                // k/256: exactly representable as a half, so the reference is exact.
                p[c] = (float) ((s >> 12) & 0xFF) / 256.0f;
            }

            // A smooth ramp under the noise, so there is low-frequency content as well.
            p[0] = 0.5f * p[0] + 0.5f * (float) ((x * 256 / w) & 0xFF) / 256.0f;
            p[1] = 0.5f * p[1] + 0.5f * (float) ((y * 256 / h) & 0xFF) / 256.0f;
        }
    }

    return img;
}

struct Rig
{
    ID3D11Texture2D* frame = nullptr;
    ID3D11Texture2D* proxy = nullptr;
    ID3D11Texture2D* model = nullptr;
    ID3D11Texture2D* result = nullptr;
    uint32_t fw = 0, fh = 0, ww = 0, wh = 0;

    void Make(uint32_t fw_, uint32_t fh_, uint32_t ww_, uint32_t wh_,
              DXGI_FORMAT eight = DXGI_FORMAT_R8G8B8A8_TYPELESS,
              DXGI_FORMAT frameFormat = DXGI_FORMAT_R16G16B16A16_FLOAT)
    {
        fw = fw_;
        fh = fh_;
        ww = ww_;
        wh = wh_;
        frame = MakeTexture(fw, fh, frameFormat, kRT);
        proxy = MakeTexture(ww, wh, eight, kRT);
        model = MakeTexture(ww, wh, eight, kRT);
        result = MakeTexture(fw, fh, eight, kRT);
    }

    void Free()
    {
        if (frame) frame->Release();
        if (proxy) proxy->Release();
        if (model) model->Release();
        if (result) result->Release();
        frame = proxy = model = result = nullptr;
    }

    VwsCmd Cmd(uint32_t op, uint32_t set, uint32_t eyes = 1, uint32_t mode = 0, float strength = 1.0f) const
    {
        VwsCmd c {};
        c.op = op;
        c.set = set;
        c.frame = frame;
        c.proxy = proxy;
        c.model = model;
        c.result = result;
        c.srgbMask = 2 | 4 | 8; // the three 8-bit surfaces carry sRGB; the frame is linear
        c.eyes = eyes;
        c.strength = strength;
        c.mode = mode;
        c.which = 15;
        c.token = ++g_token;
        return c;
    }
};

// 1 when the resolve can run, 0 when the set is absent or incomplete, negative for an error.
static int State(uint32_t set)
{
    VwsStatus s {};
    vws_status(set, &s);
    return s.error != 0 ? s.error : ((s.ready & 2) != 0 ? 1 : 0);
}

// ---- tests --------------------------------------------------------------------------------------

// Half scale, mono. The model input is the exact 2x2 average; with the model returning its input
// untouched the frame comes back as itself.
static void TestHalfIdentity(DXGI_FORMAT eight, DXGI_FORMAT frameFormat, const char* label)
{
    printf("[half scale, identity, %s]\n", label);
    Rig rig;
    rig.Make(64, 48, 32, 24, eight, frameFormat);

    Image frame = TestPattern(64, 48, 1);
    frame.at(5, 7)[0] = 1.25f;                                     // above 1: must clamp
    frame.at(6, 7)[1] = std::numeric_limits<float>::quiet_NaN();   // must read as 0
    frame.at(9, 9)[2] = -0.5f;                                     // below 0: must clamp
    UploadHalf(rig.frame, frame);

    Run(rig.Cmd(1, 0));
    CHECK(State(0) == 1, "register state %d", State(0));
    Run(rig.Cmd(2, 0));

    const Image seen = Seen(frame);
    const Image ref = AreaReference(seen, 32, 24);
    const Bytes proxy = ReadBytes(rig.proxy);
    int x = 0, y = 0;
    const int d = MaxDiffEncoded(proxy, ref, true, &x, &y);
    CHECK(d <= 1, "model input differs from the area average by %d at (%d,%d)", d, x, y);

    g_ctx->CopyResource(rig.model, rig.proxy);
    Run(rig.Cmd(3, 0));

    const Bytes result = ReadBytes(rig.result);
    const int d2 = MaxDiffEncoded(result, seen, false, &x, &y);
    CHECK(d2 <= 1, "identity resolve moved the frame by %d at (%d,%d)", d2, x, y);

    VwsStatus s {};
    vws_status(0, &s);
    CHECK(s.downsamples == 1 && s.resolves == 1 && s.skipped == 0, "counters down=%u resolve=%u skipped=%u",
          s.downsamples, s.resolves, s.skipped);
    CHECK(s.frameW == 64 && s.frameH == 48 && s.workW == 32 && s.workH == 24, "status extents");

    Run(rig.Cmd(4, 0));
    CHECK(State(0) == 0, "release state %d", State(0));
    rig.Free();
}

// The model's edit lands at full size on top of the frame's own detail, and only where it was made.
static void TestEditTransfer()
{
    printf("[half scale, edit transfer]\n");
    Rig rig;
    rig.Make(64, 48, 32, 24);

    // Keep red low enough that +24 never clips, so the expected value is plain addition.
    Image frame = TestPattern(64, 48, 2);

    for (uint32_t i = 0; i < 64 * 48; ++i)
        frame.px[i * 4] *= 0.5f;

    UploadHalf(rig.frame, frame);
    Run(rig.Cmd(1, 1));
    Run(rig.Cmd(2, 1));

    Bytes model = ReadBytes(rig.proxy);

    // Edit: +24 on red over the left 16x24 model pixels, i.e. the left 32 frame columns.
    for (uint32_t y = 0; y < 24; ++y)
        for (uint32_t x = 0; x < 16; ++x)
            model.at(x, y)[0] = (uint8_t) std::min(255, model.at(x, y)[0] + 24);

    UploadBytes(rig.model, model);
    Run(rig.Cmd(3, 1));

    const Image seen = Seen(frame);
    const Bytes result = ReadBytes(rig.result);
    int worstInside = 0, worstOutside = 0, worstOther = 0;

    for (uint32_t y = 0; y < 48; ++y)
    {
        for (uint32_t x = 0; x < 64; ++x)
        {
            const uint8_t* g = result.at(x, y);
            const float* f = seen.at(x, y);
            const int r0 = ToByte(L2S(f[0]));

            // Away from the edit's own edge (the bilinear ramp spans frame columns 30..33).
            if (x <= 28)
                worstInside = std::max(worstInside, abs((int) g[0] - (r0 + 24)));
            else if (x >= 35)
                worstOutside = std::max(worstOutside, abs((int) g[0] - r0));

            worstOther = std::max(worstOther, abs((int) g[1] - ToByte(L2S(f[1]))));
            worstOther = std::max(worstOther, abs((int) g[2] - ToByte(L2S(f[2]))));
        }
    }

    CHECK(worstInside <= 1, "edit did not land at full strength (off by %d)", worstInside);
    CHECK(worstOutside <= 1, "edit leaked outside its region (off by %d)", worstOutside);
    CHECK(worstOther <= 1, "an unedited channel moved by %d", worstOther);

    // Strength 0.5 lands half the edit.
    Run(rig.Cmd(3, 1, 1, 0, 0.5f));
    const Bytes half = ReadBytes(rig.result);
    int worstHalf = 0;

    for (uint32_t y = 0; y < 48; ++y)
        for (uint32_t x = 0; x <= 28; ++x)
            worstHalf = std::max(worstHalf, abs((int) half.at(x, y)[0] - (ToByte(L2S(seen.at(x, y)[0])) + 12)));

    CHECK(worstHalf <= 1, "strength 0.5 is off by %d", worstHalf);

    // Mode 3 hands the frame back untouched whatever the model says.
    Run(rig.Cmd(3, 1, 1, 3));
    int x = 0, y = 0;
    const int d3 = MaxDiffEncoded(ReadBytes(rig.result), seen, false, &x, &y);
    CHECK(d3 <= 1, "mode 3 moved the frame by %d at (%d,%d)", d3, x, y);

    // Mode 1 (classic) is the model's picture enlarged: inside the edit, far from any edge of a
    // flat region, that is the model's own value.
    Bytes flat(32, 24);

    for (size_t i = 0; i < flat.px.size(); i += 4)
    {
        flat.px[i] = 200;
        flat.px[i + 1] = 100;
        flat.px[i + 2] = 50;
        flat.px[i + 3] = 255;
    }

    UploadBytes(rig.model, flat);
    Run(rig.Cmd(3, 1, 1, 1));
    const Bytes classic = ReadBytes(rig.result);
    const uint8_t* c = classic.at(20, 20);
    CHECK(abs(c[0] - 200) <= 1 && abs(c[1] - 100) <= 1 && abs(c[2] - 50) <= 1 && c[3] == 255,
          "classic mode gave %u,%u,%u,%u", c[0], c[1], c[2], c[3]);

    Run(rig.Cmd(4, 1));
    rig.Free();
}

// The cube scaling: an edit that would push a channel past white is scaled as a whole, so the
// result stays inside the cube and keeps the edit's direction.
static void TestCubeScale()
{
    printf("[cube scaling]\n");
    Rig rig;
    rig.Make(16, 16, 8, 8);

    // Columns alternate bright and dim red, so the model's raster (their average) sits between the
    // two -- and an edit the model can afford there overshoots white on the bright columns.
    Image frame(16, 16);

    for (uint32_t y = 0; y < 16; ++y)
    {
        for (uint32_t x = 0; x < 16; ++x)
        {
            float* p = frame.at(x, y);
            p[0] = S2L(((x & 1) == 0 ? 250.0f : 150.0f) / 255.0f);
            p[1] = S2L(100.0f / 255.0f);
            p[2] = S2L(60.0f / 255.0f);
            p[3] = 1.0f;
        }
    }

    UploadHalf(rig.frame, frame);
    Run(rig.Cmd(1, 2));
    Run(rig.Cmd(2, 2));

    Bytes model = ReadBytes(rig.proxy);
    CHECK(model.at(4, 4)[0] + 30 <= 255, "test setup: the model raster has no room for the edit");

    // +30 red, +20 green everywhere.
    for (size_t i = 0; i < model.px.size(); i += 4)
    {
        model.px[i] = (uint8_t) (model.px[i] + 30);
        model.px[i + 1] = (uint8_t) (model.px[i + 1] + 20);
    }

    UploadBytes(rig.model, model);
    Run(rig.Cmd(3, 2));

    const Image seen = Seen(frame);
    const Bytes result = ReadBytes(rig.result);
    int worst = 0;
    bool scaled = false;

    for (uint32_t y = 0; y < 16; ++y)
    {
        for (uint32_t x = 0; x < 16; ++x)
        {
            const float* f = seen.at(x, y);
            const float pr = 255.0f * L2S(f[0]), pg = 255.0f * L2S(f[1]), pb = 255.0f * L2S(f[2]);
            const float alpha = std::min(1.0f, (255.0f - pr) / 30.0f);
            scaled = scaled || alpha < 0.5f;
            const uint8_t* g = result.at(x, y);
            worst = std::max(worst, abs((int) g[0] - (int) floorf(pr + alpha * 30.0f + 0.5f)));
            worst = std::max(worst, abs((int) g[1] - (int) floorf(pg + alpha * 20.0f + 0.5f)));
            worst = std::max(worst, abs((int) g[2] - (int) floorf(pb + 0.5f)));
        }
    }

    CHECK(scaled, "test setup: no pixel needed scaling");
    CHECK(worst <= 1, "cube scaling off by %d", worst);

    // A channel that is not being moved limits nothing, even when it sits on a wall: pure red with
    // green and blue at exactly 0 still takes the whole of a red-only edit.
    Image red(16, 16);

    for (uint32_t i = 0; i < 16 * 16; ++i)
    {
        red.px[i * 4 + 0] = S2L(100.0f / 255.0f);
        red.px[i * 4 + 3] = 1.0f;
    }

    UploadHalf(rig.frame, red);
    Run(rig.Cmd(2, 2));
    model = ReadBytes(rig.proxy);

    for (size_t i = 0; i < model.px.size(); i += 4)
        model.px[i] = (uint8_t) (model.px[i] + 40);

    UploadBytes(rig.model, model);
    Run(rig.Cmd(3, 2));
    const uint8_t* r = ReadBytes(rig.result).at(8, 8);
    CHECK(abs(r[0] - 140) <= 1 && r[1] == 0 && r[2] == 0, "an edit beside channels at zero gave %u,%u,%u", r[0], r[1], r[2]);

    // And one that darkens to the floor stops there, taking the rest of the edit with it.
    for (size_t i = 0; i < model.px.size(); i += 4)
    {
        model.px[i] = 0;        // red: proxy 100 -> 0, i.e. -100
        model.px[i + 1] = 50;   // green: 0 -> 50
    }

    UploadBytes(rig.model, model);
    Run(rig.Cmd(3, 2));
    const uint8_t* f = ReadBytes(rig.result).at(8, 8);
    CHECK(f[0] <= 1 && abs(f[1] - 50) <= 1 && f[2] == 0, "an edit to the floor gave %u,%u,%u", f[0], f[1], f[2]);

    Run(rig.Cmd(4, 2));
    rig.Free();
}

// Double-wide stereo: nothing crosses the seam, in either pass.
static void TestStereoSeam()
{
    printf("[stereo seam]\n");
    Rig rig;
    rig.Make(64, 48, 32, 24);

    // Left eye black, right eye white: any mixing in the area average would show as grey.
    Image frame(64, 48);

    for (uint32_t y = 0; y < 48; ++y)
    {
        for (uint32_t x = 0; x < 64; ++x)
        {
            float* p = frame.at(x, y);
            p[0] = p[1] = p[2] = x < 32 ? 0.0f : 1.0f;
            p[3] = 1.0f;
        }
    }

    UploadHalf(rig.frame, frame);
    Run(rig.Cmd(1, 3, 2));
    CHECK(State(3) == 1, "stereo register state %d", State(3));
    Run(rig.Cmd(2, 3, 2));

    const Bytes proxy = ReadBytes(rig.proxy);
    CHECK(proxy.at(15, 10)[0] == 0 && proxy.at(16, 10)[0] == 255, "area average mixed the eyes: %u | %u",
          proxy.at(15, 10)[0], proxy.at(16, 10)[0]);

    // A mid-grey frame, and a model that brightens the right eye only.
    Image grey(64, 48);

    for (size_t i = 0; i < grey.px.size(); ++i)
        grey.px[i] = (i % 4 == 3) ? 1.0f : S2L(100.0f / 255.0f);

    UploadHalf(rig.frame, grey);
    Run(rig.Cmd(2, 3, 2));
    Bytes model = ReadBytes(rig.proxy);

    for (uint32_t y = 0; y < 24; ++y)
        for (uint32_t x = 16; x < 32; ++x)
            model.at(x, y)[1] = (uint8_t) (model.at(x, y)[1] + 40);

    UploadBytes(rig.model, model);
    Run(rig.Cmd(3, 3, 2));
    const Bytes stereo = ReadBytes(rig.result);

    int worstLeft = 0, worstRight = 0;

    for (uint32_t y = 0; y < 48; ++y)
    {
        for (uint32_t x = 0; x < 64; ++x)
        {
            const int g = stereo.at(x, y)[1];

            if (x < 32)
                worstLeft = std::max(worstLeft, abs(g - 100));
            else
                worstRight = std::max(worstRight, abs(g - 140));
        }
    }

    CHECK(worstLeft <= 1, "the right eye's edit bled into the left eye (off by %d)", worstLeft);
    CHECK(worstRight <= 1, "the right eye's edit is not whole up to the seam (off by %d)", worstRight);

    // The same textures resolved as ONE eye must bleed across the middle -- which proves the check
    // above is testing the clamp and not just passing by construction.
    Run(rig.Cmd(3, 3, 1));
    const Bytes mono = ReadBytes(rig.result);
    const int bleed = abs((int) mono.at(31, 10)[1] - 100);
    CHECK(bleed >= 5, "expected a bilinear ramp across the middle in mono, got %d", bleed);

    Run(rig.Cmd(4, 3));
    rig.Free();
}

// ---- the window ---------------------------------------------------------------------------------

// The model's window of each eye, as the plugin describes it: origin and size as fractions.
static void SetWindow(VwsCmd& c, uint32_t eyeW, uint32_t h, uint32_t lx, uint32_t ly, uint32_t rx, uint32_t ry,
                      uint32_t ww, uint32_t wh, float feather)
{
    const float w[8] = { (float) lx / eyeW, (float) ly / h, (float) ww / eyeW, (float) wh / h,
                         (float) rx / eyeW, (float) ry / h, (float) ww / eyeW, (float) wh / h };
    memcpy(c.window, w, sizeof(w));
    c.feather = feather;
    c.windowed = 1;
}

static Image Crop(const Image& src, uint32_t x0, uint32_t y0, uint32_t w, uint32_t h)
{
    Image out(w, h);

    for (uint32_t y = 0; y < h; ++y)
        for (uint32_t x = 0; x < w; ++x)
            memcpy(out.at(x, y), src.at(x0 + x, y0 + y), 4 * sizeof(float));

    return out;
}

static std::vector<float> ReadFloats(ID3D11Texture2D* t, int channels)
{
    D3D11_TEXTURE2D_DESC d {};
    t->GetDesc(&d);
    D3D11_TEXTURE2D_DESC sd = d;
    sd.Usage = D3D11_USAGE_STAGING;
    sd.BindFlags = 0;
    sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    ID3D11Texture2D* staging = nullptr;
    g_dev->CreateTexture2D(&sd, nullptr, &staging);
    g_ctx->CopyResource(staging, t);

    std::vector<float> out((size_t) d.Width * d.Height * channels);
    D3D11_MAPPED_SUBRESOURCE m {};

    if (SUCCEEDED(g_ctx->Map(staging, 0, D3D11_MAP_READ, 0, &m)))
    {
        for (uint32_t y = 0; y < d.Height; ++y)
        {
            const uint8_t* row = (const uint8_t*) m.pData + (size_t) y * m.RowPitch;

            for (uint32_t x = 0; x < d.Width * channels; ++x)
            {
                out[(size_t) y * d.Width * channels + x] =
                    d.Format == DXGI_FORMAT_R32_FLOAT ? ((const float*) row)[x] : HalfToFloat(((const uint16_t*) row)[x]);
            }
        }

        g_ctx->Unmap(staging, 0);
    }

    staging->Release();
    return out;
}

// The model on a window of the frame: it is handed that rectangle and nothing else, its edit comes
// back into that rectangle and nowhere else, faded out along the edge.
static void TestWindow()
{
    printf("[window]\n");
    const uint32_t fw = 64, fh = 48, wx = 20, wy = 10, ww = 32, wh = 24;
    const Image frame = TestPattern(fw, fh, 7);
    const Image seen = Seen(frame);

    // 1:1 -- the model's input is the frame's own pixels of the window.
    {
        Rig rig;
        rig.Make(fw, fh, ww, wh);
        UploadHalf(rig.frame, frame);
        Run(rig.Cmd(1, 5));
        VwsCmd down = rig.Cmd(2, 5);
        SetWindow(down, fw, fh, wx, wy, wx, wy, ww, wh, 0.25f);
        Run(down);
        int ax = 0, ay = 0;
        const int diff = MaxDiffEncoded(ReadBytes(rig.proxy), Crop(seen, wx, wy, ww, wh), true, &ax, &ay);
        CHECK(diff <= 1, "1:1 window: model input differs from the frame's window by %d at %d,%d", diff, ax, ay);

        // A model that brightens green everywhere it was shown.
        Bytes model = ReadBytes(rig.proxy);

        for (uint32_t y = 0; y < wh; ++y)
            for (uint32_t x = 0; x < ww; ++x)
                model.at(x, y)[1] = (uint8_t) std::min(255, model.at(x, y)[1] + 40);

        UploadBytes(rig.model, model);
        Run(rig.Cmd(3, 5, 1, 3)); // the frame as the resolve writes it untouched
        const Bytes untouched = ReadBytes(rig.result);
        VwsCmd resolve = rig.Cmd(3, 5);
        SetWindow(resolve, fw, fh, wx, wy, wx, wy, ww, wh, 0.25f);
        Run(resolve);
        const Bytes got = ReadBytes(rig.result);

        int outside = 0, middle = 0, edge = 0, backwards = 0;

        for (uint32_t y = 0; y < fh; ++y)
        {
            for (uint32_t x = 0; x < fw; ++x)
            {
                const bool in = x >= wx && x < wx + ww && y >= wy && y < wy + wh;

                if (!in)
                {
                    for (int c = 0; c < 4; ++c)
                        outside = std::max(outside, abs((int) got.at(x, y)[c] - (int) untouched.at(x, y)[c]));
                }
            }
        }

        // Along the window's middle row: nothing at the edge, all of it across the middle, never
        // less further in.
        const uint32_t row = wy + wh / 2;
        int last = 0;

        for (uint32_t x = wx; x < wx + ww / 2; ++x)
        {
            const int before = untouched.at(x, row)[1];
            const int room = 255 - before;
            const int rise = (int) got.at(x, row)[1] - before;

            if (room >= 60)
            {
                if (x == wx)
                    edge = rise;

                if (x >= wx + ww / 2 - 4)
                    middle = std::max(middle, abs(rise - 40));

                if (rise < last - 1)
                    backwards++;

                last = rise;
            }
        }

        CHECK(outside == 0, "the frame outside the window changed by %d", outside);
        CHECK(edge <= 2, "the edit does not fade out at the window's edge (it is %d there)", edge);
        CHECK(middle <= 1, "the edit is not whole across the middle of the window (off by %d)", middle);
        CHECK(backwards == 0, "the edit does not grow steadily inwards (%d steps back)", backwards);

        // The edit on its own: grey where the model saw nothing.
        VwsCmd view = rig.Cmd(3, 5, 1, 2);
        SetWindow(view, fw, fh, wx, wy, wx, wy, ww, wh, 0.25f);
        Run(view);
        const Bytes edit = ReadBytes(rig.result);
        int brightest = 0;

        for (uint32_t x = wx + ww / 2 - 4; x < wx + ww / 2 + 4; ++x)
            brightest = std::max(brightest, (int) edit.at(x, row)[1]);

        CHECK(abs((int) edit.at(2, 2)[1] - 128) <= 1 && brightest > 200,
              "edit view: %u outside the window, %d at most in its middle", edit.at(2, 2)[1], brightest);

        Run(rig.Cmd(4, 5));
        rig.Free();
    }

    // 2:1 -- the model's input is the exact average of the window, not of the frame.
    {
        Rig rig;
        rig.Make(fw, fh, ww / 2, wh / 2);
        UploadHalf(rig.frame, frame);
        Run(rig.Cmd(1, 5));
        VwsCmd down = rig.Cmd(2, 5);
        SetWindow(down, fw, fh, wx, wy, wx, wy, ww, wh, 0.25f);
        Run(down);
        int ax = 0, ay = 0;
        const int diff = MaxDiffEncoded(ReadBytes(rig.proxy), AreaReference(Crop(seen, wx, wy, ww, wh), ww / 2, wh / 2),
                                        true, &ax, &ay);
        CHECK(diff <= 1, "2:1 window: model input differs from the window's average by %d at %d,%d", diff, ax, ay);
        Run(rig.Cmd(4, 5));
        rig.Free();
    }

    // Two eyes, each with its own window (mirrored, as lens centres are).
    {
        const uint32_t eyeW = 64, lx = 24, rx = 8;
        Rig rig;
        rig.Make(eyeW * 2, fh, ww * 2, wh);
        const Image both = TestPattern(eyeW * 2, fh, 11);
        const Image seenBoth = Seen(both);
        UploadHalf(rig.frame, both);
        Run(rig.Cmd(1, 5, 2));
        VwsCmd down = rig.Cmd(2, 5, 2);
        SetWindow(down, eyeW, fh, lx, wy, rx, wy, ww, wh, 0.25f);
        Run(down);
        const Bytes proxy = ReadBytes(rig.proxy);
        Bytes left(ww, wh), right(ww, wh);

        for (uint32_t y = 0; y < wh; ++y)
        {
            memcpy(left.at(0, y), proxy.at(0, y), (size_t) ww * 4);
            memcpy(right.at(0, y), proxy.at(ww, y), (size_t) ww * 4);
        }

        const int dl = MaxDiffEncoded(left, Crop(seenBoth, lx, wy, ww, wh), true);
        const int dr = MaxDiffEncoded(right, Crop(seenBoth, eyeW + rx, wy, ww, wh), true);
        CHECK(dl <= 1 && dr <= 1, "stereo windows: left eye off by %d, right eye off by %d", dl, dr);

        // The right eye's model brightens green; the left eye's leaves its input alone.
        Bytes model = proxy;

        for (uint32_t y = 0; y < wh; ++y)
            for (uint32_t x = ww; x < ww * 2; ++x)
                model.at(x, y)[1] = (uint8_t) std::min(255, model.at(x, y)[1] + 40);

        UploadBytes(rig.model, model);
        Run(rig.Cmd(3, 5, 2, 3));
        const Bytes untouched = ReadBytes(rig.result);
        VwsCmd resolve = rig.Cmd(3, 5, 2);
        SetWindow(resolve, eyeW, fh, lx, wy, rx, wy, ww, wh, 0.25f);
        Run(resolve);
        const Bytes got = ReadBytes(rig.result);
        int leftEye = 0;

        for (uint32_t y = 0; y < fh; ++y)
            for (uint32_t x = 0; x < eyeW; ++x)
                for (int c = 0; c < 3; ++c)
                    leftEye = std::max(leftEye, abs((int) got.at(x, y)[c] - (int) untouched.at(x, y)[c]));

        const uint32_t cx = eyeW + rx + ww / 2, cy = wy + wh / 2;
        const int rise = (int) got.at(cx, cy)[1] - (int) untouched.at(cx, cy)[1];
        CHECK(leftEye <= 1, "the right eye's edit reached the left eye (%d)", leftEye);
        CHECK(untouched.at(cx, cy)[1] > 195 || abs(rise - 40) <= 1, "the right eye's edit is not in its window (rise %d)", rise);
        Run(rig.Cmd(4, 5));
        rig.Free();
    }

    // The guides. Every texel holds its own position, so a copy says where it was taken from.
    {
        const uint32_t eyeW = 64, lx = 24, rx = 8;
        ID3D11Texture2D* depth = MakeTexture(eyeW * 2, fh, DXGI_FORMAT_R32_FLOAT, kRT);
        ID3D11Texture2D* depthCut = MakeTexture(ww * 2, wh, DXGI_FORMAT_R32_FLOAT, kRT);
        ID3D11Texture2D* motion = MakeTexture(eyeW * 2, fh, DXGI_FORMAT_R16G16_FLOAT, kRT);
        ID3D11Texture2D* motionHalf = MakeTexture(ww, wh / 2, DXGI_FORMAT_R16G16_FLOAT, kRT);
        std::vector<float> d((size_t) eyeW * 2 * fh);
        std::vector<uint16_t> mv((size_t) eyeW * 2 * fh * 2);

        for (uint32_t y = 0; y < fh; ++y)
        {
            for (uint32_t x = 0; x < eyeW * 2; ++x)
            {
                d[(size_t) y * eyeW * 2 + x] = (float) x + 1000.0f * (float) y;
                mv[((size_t) y * eyeW * 2 + x) * 2] = FloatToHalf((float) x);
                mv[((size_t) y * eyeW * 2 + x) * 2 + 1] = FloatToHalf(-(float) y);
            }
        }

        g_ctx->UpdateSubresource(depth, 0, nullptr, d.data(), eyeW * 2 * 4, 0);
        g_ctx->UpdateSubresource(motion, 0, nullptr, mv.data(), eyeW * 2 * 4, 0);

        VwsCmd reg {};
        reg.op = 6;
        reg.set = 5;
        reg.which = 1;
        reg.frame = depth;
        reg.proxy = depthCut;
        reg.token = ++g_token;
        Run(reg);
        reg.which = 0;
        reg.frame = motion;
        reg.proxy = motionHalf;
        reg.token = ++g_token;
        Run(reg);

        VwsStatus st {};
        vws_status(5, &st);
        CHECK(st.error == 0 && (st.ready & (4 | 8)) == (4 | 8) && st.token == g_token, "guides registered: ready=%d error=%d", st.ready, st.error);

        VwsCmd cut {};
        cut.op = 7;
        cut.set = 5;
        cut.eyes = 2;
        cut.which = 1;
        SetWindow(cut, eyeW, fh, lx, wy, rx, wy, ww, wh, 0.0f);
        Run(cut);
        const std::vector<float> got = ReadFloats(depthCut, 1);
        int wrong = 0;

        for (uint32_t y = 0; y < wh; ++y)
        {
            for (uint32_t x = 0; x < ww * 2; ++x)
            {
                const uint32_t sx = x < ww ? lx + x : eyeW + rx + (x - ww);
                wrong += got[(size_t) y * ww * 2 + x] != (float) sx + 1000.0f * (float) (wy + y) ? 1 : 0;
            }
        }

        CHECK(wrong == 0, "depth guide: %d of %u texels were not taken from the window", wrong, ww * 2 * wh);

        // Half size: one texel of each 2x2 block of the window, values untouched.
        cut.which = 0;
        Run(cut);
        const std::vector<float> half = ReadFloats(motionHalf, 2);
        wrong = 0;

        for (uint32_t y = 0; y < wh / 2; ++y)
        {
            for (uint32_t x = 0; x < ww; ++x)
            {
                const bool rightEye = x >= ww / 2;
                const uint32_t bx = (rightEye ? eyeW + rx : lx) + (rightEye ? x - ww / 2 : x) * 2, by = wy + y * 2;
                const float gx = half[((size_t) y * ww + x) * 2], gy = -half[((size_t) y * ww + x) * 2 + 1];
                wrong += (gx < (float) bx || gx > (float) bx + 1.0f || gy < (float) by || gy > (float) by + 1.0f) ? 1 : 0;
            }
        }

        CHECK(wrong == 0, "motion guide at half size: %d texels came from outside their 2x2 block", wrong);

        // A window that moved: its own movement comes off the motion vectors, each eye's its own.
        ID3D11Texture2D* motionCut = MakeTexture(ww * 2, wh, DXGI_FORMAT_R16G16_FLOAT, kRT);
        reg.which = 0;
        reg.frame = motion;
        reg.proxy = motionCut;
        reg.token = ++g_token;
        Run(reg);
        // The window a frame ago was the same size, elsewhere: each eye by its own amount.
        const float shift[4] = { 0.25f, -0.5f, -1.0f, 2.0f };
        memcpy(cut.prev, cut.window, sizeof(cut.prev));

        for (int e = 0; e < 2; ++e)
        {
            cut.prev[e * 4] -= shift[e * 2];
            cut.prev[e * 4 + 1] -= shift[e * 2 + 1];
        }

        cut.moved = 1;
        cut.topDown = 0;
        Run(cut);
        const std::vector<float> moved = ReadFloats(motionCut, 2);
        wrong = 0;

        for (uint32_t y = 0; y < wh; ++y)
        {
            for (uint32_t x = 0; x < ww * 2; ++x)
            {
                const bool rightEye = x >= ww;
                const float sx = (float) (rightEye ? eyeW + rx + (x - ww) : lx + x), sy = -(float) (wy + y);
                const float wantX = sx - (rightEye ? shift[2] : shift[0]), wantY = sy - (rightEye ? shift[3] : shift[1]);
                wrong += (fabsf(moved[((size_t) y * ww * 2 + x) * 2] - wantX) > 0.07f || fabsf(moved[((size_t) y * ww * 2 + x) * 2 + 1] - wantY) > 0.07f) ? 1 : 0;
            }
        }

        CHECK(wrong == 0, "motion guide of a moved window: %d texels do not carry their value less the window's movement", wrong);
        cut.moved = 0;
        motionCut->Release();

        // A window that also changed size, in textures stored top row first: the same motion
        // everywhere in the eye becomes a different one at every point of the window.
        {
            const uint32_t sw = 64, sh = 48, dw = 32, dh = 24;
            ID3D11Texture2D* flow = MakeTexture(sw, sh, DXGI_FORMAT_R16G16_FLOAT, kRT);
            ID3D11Texture2D* flowCut = MakeTexture(dw, dh, DXGI_FORMAT_R16G16_FLOAT, kRT);
            const float mx = 0.02f, my = -0.03f; // as stored: y up the picture
            std::vector<uint16_t> texels((size_t) sw * sh * 2);

            for (size_t i = 0; i < texels.size(); i += 2)
            {
                texels[i] = FloatToHalf(mx);
                texels[i + 1] = FloatToHalf(my);
            }

            g_ctx->UpdateSubresource(flow, 0, nullptr, texels.data(), sw * 4, 0);
            reg.which = 0;
            reg.frame = flow;
            reg.proxy = flowCut;
            reg.token = ++g_token;
            Run(reg);

            VwsCmd zoom {};
            zoom.op = 7;
            zoom.set = 5;
            zoom.eyes = 1;
            zoom.which = 0;
            const float now[8] = { 0.25f, 0.25f, 0.5f, 0.5f, 0.25f, 0.25f, 0.5f, 0.5f };
            const float then[8] = { 0.2f, 0.3f, 0.4f, 0.625f, 0.2f, 0.3f, 0.4f, 0.625f };
            memcpy(zoom.window, now, sizeof(now));
            memcpy(zoom.prev, then, sizeof(then));
            zoom.windowed = 1;
            zoom.moved = 1;
            zoom.topDown = 1;
            Run(zoom);
            const std::vector<float> got2 = ReadFloats(flowCut, 2);
            const float storedX = HalfToFloat(FloatToHalf(mx)), storedY = HalfToFloat(FloatToHalf(my));
            float worst = 0.0f;

            for (uint32_t y = 0; y < dh; ++y)
            {
                for (uint32_t x = 0; x < dw; ++x)
                {
                    const float atX = now[0] + ((float) x + 0.5f) / dw * now[2], atY = now[1] + ((float) y + 0.5f) / dh * now[3];
                    const float rowsY = -1.0f; // top row first: motion's y runs against the rows
                    const float wantX = (atX - now[0]) - (now[2] / then[2]) * (atX - storedX - then[0]);
                    const float wantY = ((atY - now[1]) - (now[3] / then[3]) * (atY - storedY * rowsY - then[1])) * rowsY;
                    worst = std::max(worst, fabsf(got2[((size_t) y * dw + x) * 2] - wantX));
                    worst = std::max(worst, fabsf(got2[((size_t) y * dw + x) * 2 + 1] - wantY));
                }
            }

            CHECK(worst < 2e-3f, "motion guide of a window that changed size: off by %g at worst", worst);

            // Unchanged, the same window gives the motion back as it was.
            memcpy(zoom.prev, now, sizeof(now));
            Run(zoom);
            const std::vector<float> same = ReadFloats(flowCut, 2);
            worst = 0.0f;

            for (size_t i = 0; i < same.size(); i += 2)
                worst = std::max(worst, std::max(fabsf(same[i] - storedX), fabsf(same[i + 1] - storedY)));

            CHECK(worst < 1e-3f, "motion guide of a window that did not change: off by %g at worst", worst);
            flow->Release();
            flowCut->Release();
        }

        // Without a window the same pass is a plain copy of the whole of each eye.
        ID3D11Texture2D* depthAll = MakeTexture(eyeW * 2, fh, DXGI_FORMAT_R32_FLOAT, kRT);
        reg.which = 2;
        reg.frame = depth;
        reg.proxy = depthAll;
        reg.token = ++g_token;
        Run(reg);
        VwsCmd whole {};
        whole.op = 7;
        whole.set = 5;
        whole.eyes = 2;
        whole.which = 2;
        Run(whole);
        CHECK(ReadFloats(depthAll, 1) == d, "a guide copied without a window is not the guide");

        // A pair is let go by registering nothing in its place.
        reg.frame = reg.proxy = nullptr;
        reg.token = ++g_token;
        Run(reg);
        vws_status(5, &st);
        CHECK((st.ready & 16) == 0 && (st.ready & (4 | 8)) == (4 | 8), "guide 2 let go: ready=%d", st.ready);

        Run(Rig().Cmd(4, 5));
        depth->Release();
        depthCut->Release();
        motion->Release();
        motionHalf->Release();
        depthAll->Release();
    }
}

// A ratio that is not a whole number, as every real resolution pair is.
static void TestOddRatio()
{
    printf("[odd ratio 100x60 -> 37x23]\n");
    Rig rig;
    rig.Make(100, 60, 37, 23);

    const Image frame = TestPattern(100, 60, 5);
    UploadHalf(rig.frame, frame);
    Run(rig.Cmd(1, 4));
    Run(rig.Cmd(2, 4));

    const Image seen = Seen(frame);
    const Image ref = AreaReference(seen, 37, 23);
    int x = 0, y = 0;
    const int d = MaxDiffEncoded(ReadBytes(rig.proxy), ref, true, &x, &y);
    CHECK(d <= 1, "area average off by %d at (%d,%d)", d, x, y);

    g_ctx->CopyResource(rig.model, rig.proxy);
    Run(rig.Cmd(3, 4));
    const int d2 = MaxDiffEncoded(ReadBytes(rig.result), seen, false, &x, &y);
    CHECK(d2 <= 1, "identity resolve moved the frame by %d at (%d,%d)", d2, x, y);

    // A quarter scale, the slider's floor: 4x4 footprints.
    Rig q;
    q.Make(96, 64, 24, 16);
    const Image f2 = TestPattern(96, 64, 6);
    UploadHalf(q.frame, f2);
    Run(q.Cmd(1, 5));
    Run(q.Cmd(2, 5));
    const int d3 = MaxDiffEncoded(ReadBytes(q.proxy), AreaReference(Seen(f2), 24, 16), true, &x, &y);
    CHECK(d3 <= 1, "quarter-scale area average off by %d at (%d,%d)", d3, x, y);

    Run(rig.Cmd(4, 4));
    Run(q.Cmd(4, 5));
    rig.Free();
    q.Free();
}

// Above the frame's size: the input is enlarged, the edit is averaged back down.
static void TestSupersample()
{
    printf("[supersample 32x24 -> 48x36]\n");
    Rig rig;
    rig.Make(32, 24, 48, 36);

    const Image frame = TestPattern(32, 24, 7);
    UploadHalf(rig.frame, frame);
    Run(rig.Cmd(1, 6));
    CHECK(State(6) == 1, "supersample register state %d", State(6));
    Run(rig.Cmd(2, 6));

    const Image seen = Seen(frame);
    Bytes model = ReadBytes(rig.proxy);

    g_ctx->CopyResource(rig.model, rig.proxy);
    Run(rig.Cmd(3, 6));
    int x = 0, y = 0;
    const int d = MaxDiffEncoded(ReadBytes(rig.result), seen, false, &x, &y);
    CHECK(d <= 1, "identity resolve moved the frame by %d at (%d,%d)", d, x, y);

    // A uniform +16 on blue, where the frame has room for it, averages back to exactly +16.
    Image dim = frame;

    for (uint32_t i = 0; i < 32 * 24; ++i)
        dim.px[i * 4 + 2] *= 0.5f;

    UploadHalf(rig.frame, dim);
    Run(rig.Cmd(2, 6));
    model = ReadBytes(rig.proxy);

    for (size_t i = 0; i < model.px.size(); i += 4)
        model.px[i + 2] = (uint8_t) std::min(255, model.px[i + 2] + 16);

    UploadBytes(rig.model, model);
    Run(rig.Cmd(3, 6));
    const Bytes result = ReadBytes(rig.result);
    const Image seenDim = Seen(dim);
    int worst = 0;

    for (uint32_t yy = 0; yy < 24; ++yy)
        for (uint32_t xx = 0; xx < 32; ++xx)
            worst = std::max(worst, abs((int) result.at(xx, yy)[2] - (ToByte(L2S(seenDim.at(xx, yy)[2])) + 16)));

    // The model's 8-bit steps do not line up with the frame's after the enlarge, so allow 2.
    CHECK(worst <= 2, "supersampled edit off by %d", worst);

    Run(rig.Cmd(4, 6));
    rig.Free();
}

// The passes leave the context exactly as they found it.
static void TestStateRestore()
{
    printf("[state restore]\n");
    Rig rig;
    rig.Make(64, 48, 32, 24);
    UploadHalf(rig.frame, TestPattern(64, 48, 9));
    Run(rig.Cmd(1, 7));

    ID3D11Texture2D* dummyTex = MakeTexture(8, 8, DXGI_FORMAT_R8G8B8A8_UNORM, kRT);
    ID3D11Texture2D* dummyTex2 = MakeTexture(8, 8, DXGI_FORMAT_R8G8B8A8_UNORM, kRT);
    ID3D11RenderTargetView* dummyRtv = nullptr;
    ID3D11ShaderResourceView* dummySrv = nullptr;
    g_dev->CreateRenderTargetView(dummyTex, nullptr, &dummyRtv);
    g_dev->CreateShaderResourceView(dummyTex2, nullptr, &dummySrv);

    ID3D11VertexShader* vs = nullptr;
    ID3D11PixelShader* ps = nullptr;
    g_dev->CreateVertexShader(g_vwsVs, sizeof(g_vwsVs), nullptr, &vs);
    g_dev->CreatePixelShader(g_vwsPsDown, sizeof(g_vwsPsDown), nullptr, &ps);

    D3D11_RASTERIZER_DESC rd {};
    rd.FillMode = D3D11_FILL_WIREFRAME;
    rd.CullMode = D3D11_CULL_FRONT;
    rd.ScissorEnable = TRUE;
    ID3D11RasterizerState* raster = nullptr;
    g_dev->CreateRasterizerState(&rd, &raster);

    D3D11_BLEND_DESC bd {};
    bd.RenderTarget[0].BlendEnable = TRUE;
    bd.RenderTarget[0].SrcBlend = D3D11_BLEND_BLEND_FACTOR;
    bd.RenderTarget[0].DestBlend = D3D11_BLEND_ONE;
    bd.RenderTarget[0].BlendOp = D3D11_BLEND_OP_ADD;
    bd.RenderTarget[0].SrcBlendAlpha = D3D11_BLEND_ONE;
    bd.RenderTarget[0].DestBlendAlpha = D3D11_BLEND_ONE;
    bd.RenderTarget[0].BlendOpAlpha = D3D11_BLEND_OP_ADD;
    bd.RenderTarget[0].RenderTargetWriteMask = D3D11_COLOR_WRITE_ENABLE_RED;
    ID3D11BlendState* blend = nullptr;
    g_dev->CreateBlendState(&bd, &blend);

    D3D11_DEPTH_STENCIL_DESC dd {};
    dd.DepthEnable = TRUE;
    dd.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL;
    dd.DepthFunc = D3D11_COMPARISON_LESS;
    ID3D11DepthStencilState* depth = nullptr;
    g_dev->CreateDepthStencilState(&dd, &depth);

    D3D11_SAMPLER_DESC sd {};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_POINT;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_WRAP;
    sd.ComparisonFunc = D3D11_COMPARISON_NEVER;
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    ID3D11SamplerState* sampler = nullptr;
    g_dev->CreateSamplerState(&sd, &sampler);

    D3D11_BUFFER_DESC cbd {};
    cbd.ByteWidth = 64;
    cbd.Usage = D3D11_USAGE_DEFAULT;
    cbd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    ID3D11Buffer* cb = nullptr;
    g_dev->CreateBuffer(&cbd, nullptr, &cb);

    const D3D11_VIEWPORT vp = { 1.0f, 2.0f, 7.0f, 5.0f, 0.25f, 0.75f };
    const D3D11_RECT sc = { 1, 2, 3, 4 };
    const FLOAT factor[4] = { 0.1f, 0.2f, 0.3f, 0.4f };

    for (int pass = 0; pass < 2; ++pass)
    {
        g_ctx->OMSetRenderTargets(1, &dummyRtv, nullptr);
        g_ctx->RSSetViewports(1, &vp);
        g_ctx->RSSetScissorRects(1, &sc);
        g_ctx->RSSetState(raster);
        g_ctx->OMSetBlendState(blend, factor, 0x0F0F0F0F);
        g_ctx->OMSetDepthStencilState(depth, 7);
        ID3D11ShaderResourceView* srvs[3] = { dummySrv, nullptr, dummySrv };
        g_ctx->PSSetShaderResources(0, 3, srvs);
        g_ctx->PSSetSamplers(0, 1, &sampler);
        g_ctx->PSSetConstantBuffers(0, 1, &cb);
        g_ctx->VSSetShader(vs, nullptr, 0);
        g_ctx->PSSetShader(ps, nullptr, 0);
        g_ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_LINELIST);

        Run(rig.Cmd(pass == 0 ? 2 : 3, 7));

        ID3D11RenderTargetView* gotRtv[2] {};
        ID3D11DepthStencilView* gotDsv = nullptr;
        g_ctx->OMGetRenderTargets(2, gotRtv, &gotDsv);
        CHECK(gotRtv[0] == dummyRtv && gotRtv[1] == nullptr && gotDsv == nullptr, "render targets not restored");

        UINT n = 16;
        D3D11_VIEWPORT gotVp[16] {};
        g_ctx->RSGetViewports(&n, gotVp);
        CHECK(n == 1 && memcmp(&gotVp[0], &vp, sizeof(vp)) == 0, "viewport not restored (%u)", n);

        n = 16;
        D3D11_RECT gotSc[16] {};
        g_ctx->RSGetScissorRects(&n, gotSc);
        CHECK(n == 1 && memcmp(&gotSc[0], &sc, sizeof(sc)) == 0, "scissor not restored (%u)", n);

        ID3D11RasterizerState* gotRaster = nullptr;
        g_ctx->RSGetState(&gotRaster);
        CHECK(gotRaster == raster, "rasterizer state not restored");

        ID3D11BlendState* gotBlend = nullptr;
        FLOAT gotFactor[4] {};
        UINT gotMask = 0;
        g_ctx->OMGetBlendState(&gotBlend, gotFactor, &gotMask);
        CHECK(gotBlend == blend && memcmp(gotFactor, factor, sizeof(factor)) == 0 && gotMask == 0x0F0F0F0F,
              "blend state not restored");

        ID3D11DepthStencilState* gotDepth = nullptr;
        UINT gotRef = 0;
        g_ctx->OMGetDepthStencilState(&gotDepth, &gotRef);
        CHECK(gotDepth == depth && gotRef == 7, "depth state not restored");

        ID3D11ShaderResourceView* gotSrv[3] {};
        g_ctx->PSGetShaderResources(0, 3, gotSrv);
        CHECK(gotSrv[0] == dummySrv && gotSrv[1] == nullptr && gotSrv[2] == dummySrv, "shader resources not restored");

        ID3D11SamplerState* gotSampler = nullptr;
        g_ctx->PSGetSamplers(0, 1, &gotSampler);
        CHECK(gotSampler == sampler, "sampler not restored");

        ID3D11Buffer* gotCb = nullptr;
        g_ctx->PSGetConstantBuffers(0, 1, &gotCb);
        CHECK(gotCb == cb, "constant buffer not restored");

        ID3D11VertexShader* gotVs = nullptr;
        ID3D11PixelShader* gotPs = nullptr;
        g_ctx->VSGetShader(&gotVs, nullptr, nullptr);
        g_ctx->PSGetShader(&gotPs, nullptr, nullptr);
        CHECK(gotVs == vs && gotPs == ps, "shaders not restored");

        D3D11_PRIMITIVE_TOPOLOGY gotTopo = D3D11_PRIMITIVE_TOPOLOGY_UNDEFINED;
        g_ctx->IAGetPrimitiveTopology(&gotTopo);
        CHECK(gotTopo == D3D11_PRIMITIVE_TOPOLOGY_LINELIST, "topology not restored");

        for (auto& r : gotRtv) if (r) r->Release();
        if (gotDsv) gotDsv->Release();
        if (gotRaster) gotRaster->Release();
        if (gotBlend) gotBlend->Release();
        if (gotDepth) gotDepth->Release();
        for (auto& s : gotSrv) if (s) s->Release();
        if (gotSampler) gotSampler->Release();
        if (gotCb) gotCb->Release();
        if (gotVs) gotVs->Release();
        if (gotPs) gotPs->Release();
    }

    // The passes really did draw while that hostile state was bound.
    VwsStatus s {};
    vws_status(7, &s);
    CHECK(s.downsamples == 1 && s.resolves == 1, "passes did not run under foreign state");

    g_ctx->ClearState();
    Run(rig.Cmd(4, 7));

    cb->Release();
    sampler->Release();
    depth->Release();
    blend->Release();
    raster->Release();
    ps->Release();
    vs->Release();
    dummySrv->Release();
    dummyRtv->Release();
    dummyTex2->Release();
    dummyTex->Release();
    rig.Free();
}

// Bad input is refused with an error, never dereferenced into a crash; references are given back.
static void TestErrorsAndLifetime()
{
    printf("[errors and lifetime]\n");
    Rig rig;
    rig.Make(64, 48, 32, 24);

    auto refs = [](IUnknown* u) {
        u->AddRef();
        return (int) u->Release();
    };

    VwsStatus s {};

    // Passes against a set nothing has registered are counted and skipped, without an error:
    // that is simply a set that is not there yet.
    Run(rig.Cmd(2, 8));
    Run(rig.Cmd(3, 8));
    vws_status(8, &s);
    CHECK(s.skipped == 2 && s.error == 0 && s.ready == 0, "skipped=%u error=%d ready=%d", s.skipped, s.error,
          s.ready);

    // A null pointer for a surface the command lists.
    VwsCmd bad = rig.Cmd(1, 8);
    bad.model = nullptr;
    Run(bad);
    vws_status(8, &s);
    CHECK(s.error == -1 && s.ready == 1 && s.token == bad.token, "null pointer: error=%d ready=%d token=%u/%u",
          s.error, s.ready, s.token, bad.token);

    // Model input and output of different sizes: nothing wrong with either texture, so the
    // registration stands, the set is not ready, and the resolve that finds out reports it.
    ID3D11Texture2D* wrong = MakeTexture(30, 24, DXGI_FORMAT_R8G8B8A8_TYPELESS, kRT);
    VwsCmd mismatch = rig.Cmd(1, 8);
    mismatch.model = wrong;
    Run(mismatch);
    vws_status(8, &s);
    CHECK(s.error == 0 && s.ready == 1, "size mismatch at registration: error=%d ready=%d", s.error, s.ready);
    Run(rig.Cmd(3, 8));
    vws_status(8, &s);
    CHECK(s.error == -2 && s.resolves == 0, "size mismatch at resolve: error=%d resolves=%u", s.error, s.resolves);

    // An odd width cannot be a double-wide frame; mono is fine with it.
    ID3D11Texture2D* oddIn = MakeTexture(31, 24, DXGI_FORMAT_R8G8B8A8_TYPELESS, kRT);
    ID3D11Texture2D* oddOut = MakeTexture(31, 24, DXGI_FORMAT_R8G8B8A8_TYPELESS, kRT);
    VwsCmd odd = rig.Cmd(1, 8);
    odd.proxy = oddIn;
    odd.model = oddOut;
    Run(odd);
    CHECK(State(8) == 1, "odd-width registration gave state %d", State(8));
    Run(rig.Cmd(2, 8, 2));
    vws_status(8, &s);
    CHECK(s.error == -2 && s.downsamples == 0, "odd stereo width: error=%d downsamples=%u", s.error, s.downsamples);
    Run(odd);
    Run(rig.Cmd(2, 8, 1));
    vws_status(8, &s);
    CHECK(s.error == 0 && s.downsamples == 1, "odd mono width: error=%d downsamples=%u", s.error, s.downsamples);

    // A multisampled texture is refused.
    D3D11_TEXTURE2D_DESC md {};
    md.Width = 64;
    md.Height = 48;
    md.MipLevels = 1;
    md.ArraySize = 1;
    md.Format = DXGI_FORMAT_R16G16B16A16_FLOAT;
    md.SampleDesc.Count = 4;
    md.Usage = D3D11_USAGE_DEFAULT;
    md.BindFlags = kRT;
    ID3D11Texture2D* msaa = nullptr;

    if (SUCCEEDED(g_dev->CreateTexture2D(&md, nullptr, &msaa)))
    {
        VwsCmd m = rig.Cmd(1, 8);
        m.frame = msaa;
        Run(m);
        vws_status(8, &s);
        CHECK(s.error == -2 && s.ready == 0, "multisampled frame: error=%d ready=%d", s.error, s.ready);
        CHECK(refs(msaa) == 1, "a refused texture was kept (%d)", refs(msaa));
    }

    // A texture without the render-target bind cannot be a result.
    ID3D11Texture2D* noRt = MakeTexture(64, 48, DXGI_FORMAT_R8G8B8A8_TYPELESS, D3D11_BIND_SHADER_RESOURCE);
    VwsCmd nort = rig.Cmd(1, 8);
    nort.result = noRt;
    Run(nort);
    vws_status(8, &s);
    CHECK(s.error == -3 && (s.ready & 2) == 0, "result without RT bind: error=%d ready=%d", s.error, s.ready);
    CHECK(refs(noRt) == 1, "a refused texture was kept (%d)", refs(noRt));

    // Releasing the set gives every reference back.
    Run(rig.Cmd(4, 8));
    CHECK(refs(rig.frame) == 1 && refs(rig.proxy) == 1 && refs(rig.model) == 1 && refs(rig.result) == 1 &&
              refs(wrong) == 1 && refs(oddIn) == 1 && refs(oddOut) == 1,
          "release kept references (%d %d %d %d %d %d %d)", refs(rig.frame), refs(rig.proxy), refs(rig.model),
          refs(rig.result), refs(wrong), refs(oddIn), refs(oddOut));

    // A good registration holds its textures...
    Run(rig.Cmd(1, 8));
    CHECK(State(8) == 1, "good registration gave state %d", State(8));
    CHECK(refs(rig.frame) >= 2 && refs(rig.result) >= 2, "a registration should hold references");

    // ...and survives the caller dropping the model output mid-flight, the way Unity does when it
    // replaces a render texture: the passes keep running on the old one instead of crashing.
    rig.model->Release();
    rig.model = nullptr;
    Run(rig.Cmd(2, 8));
    Run(rig.Cmd(3, 8));
    vws_status(8, &s);
    CHECK(s.resolves == 1, "resolve after the caller dropped a texture: resolves=%u", s.resolves);

    // Re-registering replaces the set and gives the old references back.
    rig.model = MakeTexture(32, 24, DXGI_FORMAT_R8G8B8A8_TYPELESS, kRT);
    Run(rig.Cmd(1, 8));
    CHECK(State(8) == 1, "re-registration gave state %d", State(8));

    Run(rig.Cmd(5, 0)); // release everything
    vws_status(8, &s);
    CHECK(s.ready == 0 && s.error == 0 && s.token == 0, "release-all left ready=%d error=%d token=%u", s.ready,
          s.error, s.token);
    CHECK(refs(rig.frame) == 1 && refs(rig.proxy) == 1 && refs(rig.model) == 1 && refs(rig.result) == 1,
          "release-all kept references (%d %d %d %d)", refs(rig.frame), refs(rig.proxy), refs(rig.model),
          refs(rig.result));

    // And the DLL comes back up after a release-all.
    Run(rig.Cmd(1, 8));
    CHECK(State(8) == 1, "registration after release-all gave state %d", State(8));
    Run(rig.Cmd(5, 0));

    // Out-of-range sets and event ids that are not ours are ignored.
    VwsCmd outOfRange = rig.Cmd(1, 99);
    Run(outOfRange);
    g_event(768);
    g_event(0x100);
    g_event(-1);

    if (msaa) msaa->Release();
    noRt->Release();
    oddOut->Release();
    oddIn->Release();
    wrong->Release();
    rig.Free();
}

// The two halves of a set arrive separately, the way the plugin sends them: frame and model input
// before VamDlssNr's evaluate, model output and result after. Driven through the scalar exports
// the plugin uses.
static void TestSplitRegistration()
{
    printf("[split registration, scalar exports]\n");
    Rig rig;
    rig.Make(64, 48, 32, 24);
    const Image frame = TestPattern(64, 48, 11);
    UploadHalf(rig.frame, frame);

    int ready = -1, error = -1, hr = -1;
    uint32_t token = 0;

    g_event(vws_push_release(10));
    vws_poll(10, &ready, &error, &token, &hr);
    CHECK(ready == 0 && error == 0 && token == 0, "fresh set: ready=%d error=%d token=%u", ready, error, token);

    // Input half only.
    g_event(vws_push_register(10, rig.frame, rig.proxy, nullptr, nullptr, 1 | 2, 2 | 4 | 8, 1001));
    vws_poll(10, &ready, &error, &token, &hr);
    CHECK(ready == 1 && error == 0 && token == 1001, "input half: ready=%d error=%d token=%u", ready, error, token);

    g_event(vws_push_pass(0, 10, 1, 1.0f, 0));
    const Image seen = Seen(frame);
    int x = 0, y = 0;
    const int d = MaxDiffEncoded(ReadBytes(rig.proxy), AreaReference(seen, 32, 24), true, &x, &y);
    CHECK(d <= 1, "downsample with only the input half off by %d at (%d,%d)", d, x, y);

    // A resolve now is early, not wrong: skipped, no error.
    g_event(vws_push_pass(1, 10, 1, 1.0f, 0));
    VwsStatus s {};
    vws_status(10, &s);
    CHECK(s.resolves == 0 && s.skipped == 1 && s.error == 0, "early resolve: resolves=%u skipped=%u error=%d",
          s.resolves, s.skipped, s.error);

    // Output half.
    g_ctx->CopyResource(rig.model, rig.proxy);
    g_event(vws_push_register(10, nullptr, nullptr, rig.model, rig.result, 4 | 8, 2 | 4 | 8, 1002));
    vws_poll(10, &ready, &error, &token, &hr);
    CHECK(ready == 3 && error == 0 && token == 1002, "output half: ready=%d error=%d token=%u", ready, error, token);

    g_event(vws_push_pass(1, 10, 1, 1.0f, 0));
    const int d2 = MaxDiffEncoded(ReadBytes(rig.result), seen, false, &x, &y);
    CHECK(d2 <= 1, "resolve after both halves off by %d at (%d,%d)", d2, x, y);

    // The model's size changes (the slider moved): the input half is re-registered first, and the
    // stale output half must not be used against it.
    ID3D11Texture2D* proxy2 = MakeTexture(48, 36, DXGI_FORMAT_R8G8B8A8_TYPELESS, kRT);
    ID3D11Texture2D* model2 = MakeTexture(48, 36, DXGI_FORMAT_R8G8B8A8_TYPELESS, kRT);
    g_event(vws_push_register(10, rig.frame, proxy2, nullptr, nullptr, 1 | 2, 2 | 4 | 8, 1003));
    vws_poll(10, &ready, &error, &token, &hr);
    CHECK(ready == 1 && error == 0 && token == 1003, "after a resize: ready=%d error=%d token=%u", ready, error, token);
    g_event(vws_push_pass(0, 10, 1, 1.0f, 0));
    g_ctx->CopyResource(model2, proxy2);
    g_event(vws_push_register(10, nullptr, nullptr, model2, rig.result, 4 | 8, 2 | 4 | 8, 1004));
    vws_poll(10, &ready, &error, &token, &hr);
    CHECK(ready == 3 && token == 1004, "after re-registering the output half: ready=%d token=%u", ready, token);
    g_event(vws_push_pass(1, 10, 1, 1.0f, 0));
    const int d3 = MaxDiffEncoded(ReadBytes(rig.result), seen, false, &x, &y);
    CHECK(d3 <= 1, "resolve at the new size off by %d at (%d,%d)", d3, x, y);

    g_event(vws_push_release(10));
    vws_poll(10, &ready, &error, &token, &hr);
    CHECK(ready == 0 && token == 0, "after release: ready=%d token=%u", ready, token);

    g_event(vws_push_release(0xFFFFFFFFu));
    model2->Release();
    proxy2->Release();
    rig.Free();
}

// A registration from another device drops everything built on the old one and carries on there.
static void TestDeviceChange()
{
    printf("[device change]\n");
    Rig first;
    first.Make(64, 48, 32, 24);
    Run(first.Cmd(1, 11));
    CHECK(State(11) == 1, "first device: state %d", State(11));

    ID3D11Device* dev2 = nullptr;
    ID3D11DeviceContext* ctx2 = nullptr;
    D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL_11_0;
    const HRESULT hr = D3D11CreateDevice(nullptr, g_warp ? D3D_DRIVER_TYPE_WARP : D3D_DRIVER_TYPE_HARDWARE, nullptr,
                                         D3D11_CREATE_DEVICE_SINGLETHREADED, &level, 1, D3D11_SDK_VERSION, &dev2,
                                         nullptr, &ctx2);

    if (FAILED(hr))
    {
        printf("  (second device unavailable, skipped)\n");
        Run(first.Cmd(5, 0));
        first.Free();
        return;
    }

    ID3D11Device* dev1 = g_dev;
    ID3D11DeviceContext* ctx1 = g_ctx;
    g_dev = dev2;
    g_ctx = ctx2;

    Rig second;
    second.Make(64, 48, 32, 24);
    const Image frame = TestPattern(64, 48, 13);
    UploadHalf(second.frame, frame);
    Run(second.Cmd(1, 12));
    CHECK(State(12) == 1, "second device: state %d", State(12));
    CHECK(State(11) == 0, "the first device's set survived the change (state %d)", State(11));

    Run(second.Cmd(2, 12));
    g_ctx->CopyResource(second.model, second.proxy);
    Run(second.Cmd(3, 12));
    int x = 0, y = 0;
    const int d = MaxDiffEncoded(ReadBytes(second.result), Seen(frame), false, &x, &y);
    CHECK(d <= 1, "passes on the second device off by %d at (%d,%d)", d, x, y);

    Run(second.Cmd(5, 0));
    second.Free();
    ctx2->ClearState();
    ctx2->Flush();
    ctx2->Release();
    dev2->Release();

    g_dev = dev1;
    g_ctx = ctx1;

    auto refs = [](IUnknown* u) {
        u->AddRef();
        return (int) u->Release();
    };

    CHECK(refs(first.frame) == 1 && refs(first.result) == 1, "the old device's textures are still held (%d %d)",
          refs(first.frame), refs(first.result));
    first.Free();
}

// A rough cost figure at the sizes VaM actually runs, so a pathological shader shows up here.
static void TestTiming(uint32_t fw, uint32_t fh, uint32_t ww, uint32_t wh, uint32_t eyes)
{
    Rig rig;
    rig.Make(fw, fh, ww, wh);
    Image frame(fw, fh);

    for (size_t i = 0; i < frame.px.size(); ++i)
        frame.px[i] = 0.25f;

    UploadHalf(rig.frame, frame);
    Run(rig.Cmd(1, 9, eyes));
    Run(rig.Cmd(2, 9, eyes));
    g_ctx->CopyResource(rig.model, rig.proxy);

    ID3D11Query* query = nullptr;
    D3D11_QUERY_DESC qd { D3D11_QUERY_EVENT, 0 };
    g_dev->CreateQuery(&qd, &query);

    auto finish = [&]() {
        g_ctx->End(query);
        BOOL done = FALSE;

        while (g_ctx->GetData(query, &done, sizeof(done), 0) != S_OK || !done)
            Sleep(0);
    };

    finish();

    LARGE_INTEGER freq, t0, t1;
    QueryPerformanceFrequency(&freq);
    const int frames = 200;
    QueryPerformanceCounter(&t0);

    for (int i = 0; i < frames; ++i)
    {
        Run(rig.Cmd(2, 9, eyes));
        Run(rig.Cmd(3, 9, eyes));
    }

    finish();
    QueryPerformanceCounter(&t1);

    const double ms = (double) (t1.QuadPart - t0.QuadPart) * 1000.0 / (double) freq.QuadPart / frames;
    printf("[timing] %ux%u frame, %ux%u model, %u eye(s): %.3f ms for both passes\n", fw, fh, ww, wh, eyes, ms);

    query->Release();
    Run(rig.Cmd(4, 9));
    rig.Free();
}

// ---- sharpening ---------------------------------------------------------------------------------
//
// PSSharpen, worked on the CPU: the same five taps, the same limits.
static Image SharpenReference(const Image& src, uint32_t eyes, float strength, bool squash)
{
    Image out(src.w, src.h);
    const int w = (int) src.w, h = (int) src.h, eyeW = eyes == 2 ? w / 2 : w;

    auto tap = [&](int x, int y, float c[3]) {
        const float* p = src.at((uint32_t) x, (uint32_t) y);

        for (int i = 0; i < 3; ++i)
            c[i] = squash ? (p[i] > 0.0f ? p[i] : 0.0f) : Saturate(p[i]);

        if (squash)
        {
            const float m = 1.0f + std::max(c[0], std::max(c[1], c[2]));

            for (int i = 0; i < 3; ++i)
                c[i] /= m;
        }
    };

    for (int y = 0; y < h; ++y)
    {
        for (int x = 0; x < w; ++x)
        {
            const int x0 = (eyes == 2 && x >= eyeW) ? eyeW : 0;
            const int x1 = x0 + eyeW - 1;
            float e[3], b[3], d[3], f[3], hh[3];
            tap(x, y, e);
            tap(x, std::max(y - 1, 0), b);
            tap(std::max(x - 1, x0), y, d);
            tap(std::min(x + 1, x1), y, f);
            tap(x, std::min(y + 1, h - 1), hh);

            float widest = -1e30f;

            for (int i = 0; i < 3; ++i)
            {
                const float mn4 = std::min(std::min(b[i], d[i]), std::min(f[i], hh[i]));
                const float mx4 = std::max(std::max(b[i], d[i]), std::max(f[i], hh[i]));
                const float hitMin = std::min(mn4, e[i]) / std::max(4.0f * mx4, 1e-5f);
                const float hitMax = (1.0f - std::max(mx4, e[i])) / std::min(4.0f * mn4 - 4.0f, -1e-5f);
                widest = std::max(widest, std::max(-hitMin, hitMax));
            }

            const float lobe = std::max(-0.1875f, std::min(widest, 0.0f)) * Saturate(strength);
            float o[3];

            for (int i = 0; i < 3; ++i)
                o[i] = (lobe * (b[i] + d[i] + f[i] + hh[i]) + e[i]) / (4.0f * lobe + 1.0f);

            if (squash)
            {
                for (int i = 0; i < 3; ++i)
                    o[i] = std::min(std::max(o[i], 0.0f), 0.999f);

                const float m = 1.0f - std::max(o[0], std::max(o[1], o[2]));

                for (int i = 0; i < 3; ++i)
                    o[i] /= m;
            }
            else
            {
                for (int i = 0; i < 3; ++i)
                    o[i] = Saturate(o[i]);
            }

            float* q = out.at((uint32_t) x, (uint32_t) y);
            q[0] = o[0];
            q[1] = o[1];
            q[2] = o[2];
            q[3] = src.at((uint32_t) x, (uint32_t) y)[3];
        }
    }

    return out;
}

static void TestSharpen()
{
    printf("[sharpening a finished frame where it lies]\n");

    const uint32_t w = 64, h = 24;
    Image img(w, h);

    for (uint32_t y = 0; y < h; ++y)
    {
        for (uint32_t x = 0; x < w; ++x)
        {
            float v = 0.15f + 0.07f * (float) ((x * 7 + y * 13) % 11);

            if (x >= 4 && x < 14 && y >= 2 && y < 10)
                v = 0.5f; // a flat patch: nothing to sharpen

            if (y >= 16)
                v *= 6.0f; // scene light well above 1

            float* p = img.at(x, y);
            p[0] = v;
            p[1] = v * 0.8f;
            p[2] = v * 0.5f + 0.05f;
            p[3] = 0.25f + 0.5f * (float) (x % 2);
        }
    }

    // What the texture really holds once it is half floats.
    for (float& v : img.px)
        v = HalfToFloat(FloatToHalf(v));

    VwsCmd c {};
    c.op = 8; // OpSharpen
    c.eyes = 2;

    // A double-wide half-float frame, as DLSS's output is in a headset.
    {
        ID3D11Texture2D* t = MakeTexture(w, h, DXGI_FORMAT_R16G16B16A16_TYPELESS, kRT);
        UploadHalf(t, img);

        c.frame = t;
        c.strength = 0.0f;
        Run(c);
        std::vector<float> same = ReadFloats(t, 4);
        float moved = 0.0f;

        for (size_t i = 0; i < same.size(); ++i)
            moved = std::max(moved, fabsf(same[i] - img.px[i]));

        CHECK(moved == 0.0f, "strength 0 leaves the frame as it was (moved by %g)", moved);

        c.strength = 0.8f;
        Run(c);
        const std::vector<float> got = ReadFloats(t, 4);
        const Image ref = SharpenReference(img, 2, 0.8f, true);

        float worst = 0.0f, changed = 0.0f, flat = 0.0f, alpha = 0.0f;

        for (uint32_t y = 0; y < h; ++y)
        {
            for (uint32_t x = 0; x < w; ++x)
            {
                for (int i = 0; i < 4; ++i)
                {
                    const float g = got[((size_t) y * w + x) * 4 + i];
                    const float r = ref.at(x, y)[i];
                    const float was = img.at(x, y)[i];

                    if (i == 3)
                    {
                        alpha = std::max(alpha, fabsf(g - was));
                        continue;
                    }

                    worst = std::max(worst, fabsf(g - r) / (0.004f + 0.004f * fabsf(r)));
                    changed = std::max(changed, fabsf(g - was));

                    if (x >= 5 && x < 13 && y >= 3 && y < 9)
                        flat = std::max(flat, fabsf(g - was));
                }
            }
        }

        CHECK(worst <= 1.0f, "a half-float stereo frame comes back as the reference has it (worst %.2fx the tolerance)", worst);
        CHECK(changed > 0.02f, "the pass sharpened something (largest change %g)", changed);
        CHECK(flat < 0.003f, "a flat patch is left alone (moved by %g)", flat);
        CHECK(alpha == 0.0f, "alpha is carried through (moved by %g)", alpha);
        t->Release();
    }

    // An 8-bit frame, as Neural Rendering's result is.
    {
        Bytes bytes(w, h);
        Image seen(w, h);

        for (uint32_t y = 0; y < h; ++y)
        {
            for (uint32_t x = 0; x < w; ++x)
            {
                for (int i = 0; i < 4; ++i)
                {
                    const int b = ToByte(Saturate(img.at(x, y)[i] * (y >= 16 ? 1.0f / 6.0f : 1.0f)));
                    bytes.at(x, y)[i] = (uint8_t) b;
                    seen.at(x, y)[i] = (float) b / 255.0f;
                }
            }
        }

        ID3D11Texture2D* t = MakeTexture(w, h, DXGI_FORMAT_R8G8B8A8_TYPELESS, kRT);
        UploadBytes(t, bytes);

        c.frame = t;
        c.eyes = 1;
        c.strength = 1.0f;
        Run(c);

        const Bytes got = ReadBytes(t);
        const Image ref = SharpenReference(seen, 1, 1.0f, false);
        int worst = 0;

        for (uint32_t y = 0; y < h; ++y)
            for (uint32_t x = 0; x < w; ++x)
                for (int i = 0; i < 4; ++i)
                    worst = std::max(worst, abs((int) got.at(x, y)[i] - (int) lroundf(ref.at(x, y)[i] * 255.0f)));

        CHECK(worst <= 1, "an 8-bit frame comes back as the reference has it (worst %d/255)", worst);
        t->Release();
    }

    // Something that is not a texture is refused once, quietly, and nothing else is disturbed.
    c.frame = g_dev;
    Run(c);
    Run(c);
    Drain(true);
}

// ---- passthrough --------------------------------------------------------------------------------
//
// PSPassthrough worked on the CPU, with the transforms composed the way the DLL composes them.
struct Rig34
{
    double m[12];
};

static Rig34 RigInverse(const Rig34& a)
{
    Rig34 r;

    for (int i = 0; i < 3; ++i)
    {
        for (int j = 0; j < 3; ++j)
            r.m[i * 4 + j] = a.m[j * 4 + i];

        r.m[i * 4 + 3] = -(a.m[i] * a.m[3] + a.m[4 + i] * a.m[7] + a.m[8 + i] * a.m[11]);
    }

    return r;
}

static Rig34 RigMul(const Rig34& a, const Rig34& b)
{
    Rig34 r;

    for (int i = 0; i < 3; ++i)
        for (int j = 0; j < 4; ++j)
            r.m[i * 4 + j] = a.m[i * 4] * b.m[j] + a.m[i * 4 + 1] * b.m[4 + j] + a.m[i * 4 + 2] * b.m[8 + j] + (j == 3 ? a.m[i * 4 + 3] : 0.0);

    return r;
}

static Rig34 RigFrom(const float* f)
{
    Rig34 r;

    for (int i = 0; i < 12; ++i)
        r.m[i] = f[i];

    return r;
}

static void RigYaw(float* out, float yaw, float pitch, float x, float y, float z)
{
    const float cy = cosf(yaw), sy = sinf(yaw), cp = cosf(pitch), sp = sinf(pitch);
    const float m[12] = { cy, sy * sp, sy * cp, x, 0, cp, -sp, y, -sy, cy * sp, cy * cp, z };
    memcpy(out, m, sizeof(m));
}

// The room's grey at one pixel of one eye's picture; `matte` is how much of it shows.
static float PassRoom(const float* cfg, const Rig34& eyeToCam, const std::vector<uint8_t>& cam, uint32_t cw, uint32_t ch, uint32_t eye,
                      float u, float v)
{
    const float* tn = cfg + 16 + eye * 4;
    double ray[3] = { tn[0] + (tn[1] - tn[0]) * u, -(tn[2] + (tn[3] - tn[2]) * v), -1.0 };
    const double len = sqrt(ray[0] * ray[0] + ray[1] * ray[1] + ray[2] * ray[2]);
    double at[3], q[3];

    for (int i = 0; i < 3; ++i)
        at[i] = ray[i] / len * cfg[6];

    for (int i = 0; i < 3; ++i)
        q[i] = eyeToCam.m[i * 4] * at[0] + eyeToCam.m[i * 4 + 1] * at[1] + eyeToCam.m[i * 4 + 2] * at[2] + eyeToCam.m[i * 4 + 3];

    const double across = std::max(sqrt(q[0] * q[0] + q[1] * q[1]), 1e-9);
    const double angle = atan2(across, -q[2]);
    const double a2 = angle * angle;
    const double radius = cfg[7] * angle * (1.0 + a2 * (cfg[8] + a2 * (cfg[9] + a2 * (cfg[10] + a2 * cfg[11]))));
    const double lensW = cw / 2;
    double tx = cfg[12 + eye * 2] + radius * q[0] / across;
    double ty = cfg[13 + eye * 2] - radius * q[1] / across;
    const double inside = std::min(std::max((lensW * 0.5 - 6.0 - radius) / 40.0, 0.0), 1.0);
    tx = std::min(std::max(tx, 0.0), lensW - 1.0);
    ty = std::min(std::max(ty, 0.0), (double) ch - 1.0);

    const int x0 = (int) floor(tx), y0 = (int) floor(ty);
    const int x1 = std::min(x0 + 1, (int) lensW - 1), y1 = std::min(y0 + 1, (int) ch - 1);
    const double fx = tx - x0, fy = ty - y0;
    const int ox = (int) (eye * lensW);
    const double g = (cam[(size_t) y0 * cw + ox + x0] * (1 - fx) * (1 - fy) + cam[(size_t) y0 * cw + ox + x1] * fx * (1 - fy) +
                      cam[(size_t) y1 * cw + ox + x0] * (1 - fx) * fy + cam[(size_t) y1 * cw + ox + x1] * fx * fy) / 255.0;

    return (float) (std::min(std::max(g * cfg[5], 0.0), 1.0) * inside);
}

static float PassMatte(const float* cfg, float r, float g, float b)
{
    const float d = sqrtf((r - cfg[0]) * (r - cfg[0]) + (g - cfg[1]) * (g - cfg[1]) + (b - cfg[2]) * (b - cfg[2]));
    const float t = Saturate((d - cfg[3]) / std::max(cfg[4], 1e-4f));
    return 1.0f - t * t * (3.0f - 2.0f * t);
}

static void TestPassthrough(HMODULE dll)
{
    printf("[passthrough: the key colour becomes the camera's picture]\n");

    typedef void (*ConfigureFn)(const float*, uint32_t);
    typedef void (*InjectFn)(const uint8_t*, uint32_t, uint32_t, const float*);
    typedef int (*PushPassFn2)(void*, uint32_t, uint32_t, uint32_t, uint32_t, const float*);
    const ConfigureFn configure = (ConfigureFn) GetProcAddress(dll, "vws_cam_configure");
    const InjectFn inject = (InjectFn) GetProcAddress(dll, "vws_cam_inject");
    const PushPassFn2 push = (PushPassFn2) GetProcAddress(dll, "vws_push_passthrough");

    CHECK(configure && inject && push, "the passthrough exports are there");

    if (!configure || !inject || !push)
        return;

    // A camera frame with something smooth in it, two lenses side by side.
    const uint32_t cw = 2032, ch = 1016;
    std::vector<uint8_t> cam((size_t) cw * ch);

    for (uint32_t y = 0; y < ch; ++y)
        for (uint32_t x = 0; x < cw; ++x)
            cam[(size_t) y * cw + x] = (uint8_t) lroundf(128.0f + 90.0f * sinf(x * 0.013f) * cosf(y * 0.017f));

    float cfg[80] = {};
    cfg[0] = 0.0f; cfg[1] = 1.0f; cfg[2] = 0.0f; // green
    cfg[3] = 0.30f;  // tolerance
    cfg[4] = 0.20f;  // softness
    cfg[5] = 1.2f;   // gain
    cfg[6] = 1.5f;   // distance
    cfg[7] = 382.6f; // focal
    cfg[8] = 0.01983145f; cfg[9] = -0.0011872f; cfg[10] = -0.00294614f; cfg[11] = 0.00045608f;
    cfg[12] = 507.887f; cfg[13] = 510.078f; cfg[14] = 506.532f; cfg[15] = 504.605f;
    const float tangents[8] = { -1.8418f, 0.9472f, -1.329f, 1.329f, -0.9472f, 1.8418f, -1.329f, 1.329f };
    memcpy(cfg + 16, tangents, sizeof(tangents));
    const float camToHead[24] = { 0.9647f, 0.0022f, 0.2635f, -0.04f, -0.1331f, 0.8671f, 0.48f, -0.0392f, -0.2274f, -0.4981f, 0.8368f, -0.0809f,
                                  0.9653f, 0.0041f, -0.261f, 0.0388f, 0.1275f, 0.8651f, 0.4852f, -0.0393f, 0.2278f, -0.5016f, 0.8345f, -0.0806f };
    memcpy(cfg + 24, camToHead, sizeof(camToHead));
    const float eyeToHead[24] = { 1, 0, 0, -0.0325f, 0, 1, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0.0325f, 0, 1, 0, 0, 0, 0, 1, 0 };
    memcpy(cfg + 48, eyeToHead, sizeof(eyeToHead));
    cfg[72] = 1.0f; // follow the head
    cfg[73] = 0.0f;

    float headThen[12], headNow[12];
    RigYaw(headThen, 0.20f, 0.05f, 0.10f, 1.20f, -0.30f);
    RigYaw(headNow, 0.27f, 0.02f, 0.12f, 1.21f, -0.28f);

    // The pose a frame comes with is the first camera's, as a PlayStation VR2 gives it.
    cfg[79] = 1.0f;
    float cameraThen[12];
    {
        const Rig34 c = RigMul(RigFrom(headThen), RigFrom(camToHead));

        for (int i = 0; i < 12; ++i)
            cameraThen[i] = (float) c.m[i];
    }

    configure(cfg, 80);
    inject(cam.data(), cw, ch, cameraThen);

    // A double-wide half-float frame, first row at the top: key, nearly key, not key.
    {
        const uint32_t w = 128, h = 64, eyeW = 64;
        Image img(w, h);

        for (uint32_t y = 0; y < h; ++y)
        {
            for (uint32_t x = 0; x < w; ++x)
            {
                const uint32_t third = (x % eyeW) * 3 / eyeW;
                float* p = img.at(x, y);
                const float rgb[3][3] = { { 0.0f, 1.0f, 0.0f }, { 0.05f, 0.8f, 0.05f }, { 0.4f, 0.3f, 0.2f } };
                p[0] = rgb[third][0];
                p[1] = rgb[third][1];
                p[2] = rgb[third][2];
                p[3] = 0.5f;
            }
        }

        for (float& v : img.px)
            v = HalfToFloat(FloatToHalf(v));

        ID3D11Texture2D* t = MakeTexture(w, h, DXGI_FORMAT_R16G16B16A16_TYPELESS, kRT);
        UploadHalf(t, img);

        Rig34 eyeToCam[2];

        for (uint32_t eye = 0; eye < 2; ++eye)
        {
            const Rig34 eyeToRoom = RigMul(RigFrom(headNow), RigFrom(eyeToHead + eye * 12));
            float asFloats[12];

            for (int i = 0; i < 12; ++i)
                asFloats[i] = (float) eyeToRoom.m[i];

            eyeToCam[eye] = RigMul(RigInverse(RigFrom(camToHead + eye * 12)), RigMul(RigInverse(RigFrom(headThen)), RigFrom(asFloats)));
            g_event(push(t, 2, eye, 0, 1, asFloats));
        }

        const std::vector<float> got = ReadFloats(t, 4);
        float worst = 0.0f, keyed = 0.0f, kept = 0.0f;
        int rooms = 0;

        for (uint32_t y = 0; y < h; ++y)
        {
            for (uint32_t x = 0; x < w; ++x)
            {
                const uint32_t eye = x / eyeW;
                const float* was = img.at(x, y);
                const float matte = PassMatte(cfg, L2S(Saturate(was[0])), L2S(Saturate(was[1])), L2S(Saturate(was[2])));
                const float room = S2L(PassRoom(cfg, eyeToCam[eye], cam, cw, ch, eye, (x - eye * eyeW + 0.5f) / eyeW, (y + 0.5f) / h));

                for (int i = 0; i < 3; ++i)
                {
                    const float want = was[i] + (room - was[i]) * matte;
                    const float g = got[((size_t) y * w + x) * 4 + i];
                    worst = std::max(worst, fabsf(g - want) / (0.02f + 0.02f * fabsf(want)));

                    if (matte >= 1.0f)
                        keyed = std::max(keyed, fabsf(g - room));

                    if (matte <= 0.0f)
                        kept = std::max(kept, fabsf(g - was[i]));
                }

                if (matte >= 1.0f && room > 0.02f)
                    rooms++;
            }
        }

        CHECK(worst <= 1.0f, "a stereo half-float frame comes back as the reference has it (worst %.2fx the tolerance)", worst);
        CHECK(kept == 0.0f, "what is not the key colour is left exactly as it was (moved by %g)", kept);
        CHECK(rooms > 500, "the key colour shows the camera's picture (%d pixels of it)", rooms);
        t->Release();
    }

    // One eye's own 8-bit frame, first row at the bottom, no pose given: straight through the head.
    {
        const uint32_t w = 64, h = 64, eye = 1;
        Bytes bytes(w, h);

        for (uint32_t y = 0; y < h; ++y)
        {
            for (uint32_t x = 0; x < w; ++x)
            {
                uint8_t* p = bytes.at(x, y);
                const bool key = x < w / 2;
                p[0] = key ? 0 : 120;
                p[1] = key ? 255 : 60;
                p[2] = key ? 0 : 200;
                p[3] = 255;
            }
        }

        ID3D11Texture2D* t = MakeTexture(w, h, DXGI_FORMAT_R8G8B8A8_TYPELESS, kRT);
        UploadBytes(t, bytes);
        g_event(push(t, 1, eye, 1, 0, nullptr));

        const Rig34 eyeToCam = RigMul(RigInverse(RigFrom(camToHead + eye * 12)), RigFrom(eyeToHead + eye * 12));
        const Bytes got = ReadBytes(t);
        int worst = 0;

        for (uint32_t y = 0; y < h; ++y)
        {
            for (uint32_t x = 0; x < w; ++x)
            {
                const uint8_t* was = bytes.at(x, y);
                const float matte = PassMatte(cfg, was[0] / 255.0f, was[1] / 255.0f, was[2] / 255.0f);
                const float room = PassRoom(cfg, eyeToCam, cam, cw, ch, eye, (x + 0.5f) / w, 1.0f - (y + 0.5f) / h);

                for (int i = 0; i < 3; ++i)
                {
                    const float want = was[i] / 255.0f + (room - was[i] / 255.0f) * matte;
                    worst = std::max(worst, abs((int) got.at(x, y)[i] - (int) lroundf(want * 255.0f)));
                }
            }
        }

        CHECK(worst <= 2, "one eye's 8-bit frame, the other way up, comes back as the reference has it (worst %d/255)", worst);
        t->Release();
    }

    Drain(true);
}

// ---- the room as an overlay -----------------------------------------------------------------------
//
// The camera thread, fed by a stand-in for SteamVR's frame call, draws the overlay's picture on a
// device of its own from the matte this thread's device wrote. There is no SteamVR here, so the
// picture goes nowhere; it is read back and held to the same arithmetic done on the CPU.
static std::vector<uint8_t> g_fakeCam;
static uint32_t g_fakeW = 0, g_fakeH = 0;
static float g_fakePose[12];

static int __stdcall FakeFrame(uint64_t, int, void* buffer, uint32_t size, void* header, uint32_t headerSize)
{
    if (header == nullptr || headerSize < 100)
        return 1;

    uint8_t* h = (uint8_t*) header;
    const uint32_t fields[5] = { 0, g_fakeW, g_fakeH, 4, GetTickCount() / 20 };
    memcpy(h, fields, sizeof(fields));
    memcpy(h + 20, g_fakePose, sizeof(g_fakePose));
    h[96] = 1;

    if (buffer != nullptr)
    {
        if (size < g_fakeW * g_fakeH * 4)
            return 2;

        uint8_t* out = (uint8_t*) buffer;

        for (size_t i = 0; i < (size_t) g_fakeW * g_fakeH; ++i)
        {
            out[i * 4] = out[i * 4 + 1] = out[i * 4 + 2] = g_fakeCam[i];
            out[i * 4 + 3] = 255;
        }
    }

    return 0;
}

static void TestOverlay(HMODULE dll)
{
    printf("[passthrough as an overlay: a second device, the game's matte, the camera's own pace]\n");

    typedef void (*ConfigureFn)(const float*, uint32_t);
    typedef int (*PushMatteFn)(void*, uint32_t, uint32_t, uint32_t, uint32_t, const float*, uint32_t);
    typedef int (*StartFn)(void*, uint64_t, uint32_t, uint32_t);
    typedef void (*StopFn)();
    typedef int (*ReadFn)(uint8_t*, uint32_t, uint32_t*, uint32_t*);
    typedef void (*StatusFn2)(int32_t*, uint32_t*, int32_t*);
    const ConfigureFn configure = (ConfigureFn) GetProcAddress(dll, "vws_cam_configure");
    const PushMatteFn pushMatte = (PushMatteFn) GetProcAddress(dll, "vws_push_matte");
    const StartFn start = (StartFn) GetProcAddress(dll, "vws_cam_start");
    const StopFn stop = (StopFn) GetProcAddress(dll, "vws_cam_stop");
    const ReadFn read = (ReadFn) GetProcAddress(dll, "vws_overlay_read");
    const StatusFn2 status = (StatusFn2) GetProcAddress(dll, "vws_overlay_status");

    CHECK(configure && pushMatte && start && stop && read && status, "the overlay exports are there");

    if (!configure || !pushMatte || !start || !stop || !read || !status)
        return;

    const uint32_t cw = 2032, ch = 1016;
    g_fakeW = cw;
    g_fakeH = ch;
    g_fakeCam.resize((size_t) cw * ch);

    for (uint32_t y = 0; y < ch; ++y)
        for (uint32_t x = 0; x < cw; ++x)
            g_fakeCam[(size_t) y * cw + x] = (uint8_t) lroundf(128.0f + 90.0f * sinf(x * 0.013f) * cosf(y * 0.017f));

    float cfg[80] = {};
    cfg[0] = 0.0f; cfg[1] = 1.0f; cfg[2] = 0.0f;
    cfg[3] = 0.30f;
    cfg[4] = 0.20f;
    cfg[5] = 1.2f;
    cfg[6] = 1.5f;
    cfg[7] = 382.6f;
    cfg[8] = 0.01983145f; cfg[9] = -0.0011872f; cfg[10] = -0.00294614f; cfg[11] = 0.00045608f;
    cfg[12] = 507.887f; cfg[13] = 510.078f; cfg[14] = 506.532f; cfg[15] = 504.605f;
    const float tangents[8] = { -1.8418f, 0.9472f, -1.329f, 1.329f, -0.9472f, 1.8418f, -1.329f, 1.329f };
    memcpy(cfg + 16, tangents, sizeof(tangents));
    const float camToHead[24] = { 0.9647f, 0.0022f, 0.2635f, -0.04f, -0.1331f, 0.8671f, 0.48f, -0.0392f, -0.2274f, -0.4981f, 0.8368f, -0.0809f,
                                  0.9653f, 0.0041f, -0.261f, 0.0388f, 0.1275f, 0.8651f, 0.4852f, -0.0393f, 0.2278f, -0.5016f, 0.8345f, -0.0806f };
    memcpy(cfg + 24, camToHead, sizeof(camToHead));
    const float eyeToHead[24] = { 1, 0, 0, -0.0325f, 0, 1, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0.0325f, 0, 1, 0, 0, 0, 0, 1, 0 };
    memcpy(cfg + 48, eyeToHead, sizeof(eyeToHead));
    cfg[72] = 1.0f; // follow the head
    cfg[73] = 0.0f; // the picture
    cfg[74] = 1.0f; // as an overlay
    cfg[75] = 2.2f; // the quad's reach
    cfg[76] = 2.0f; // its distance
    cfg[77] = 0.0f; // shaped by one eye's picture
    cfg[78] = 1.0f; // standing universe

    cfg[79] = 1.0f; // the frames come with the first camera's pose

    float headNow[12], headThen[12];
    RigYaw(headThen, 0.20f, 0.05f, 0.10f, 1.20f, -0.30f);
    RigYaw(headNow, 0.27f, 0.02f, 0.12f, 1.21f, -0.28f);
    {
        const Rig34 c = RigMul(RigFrom(headThen), RigFrom(camToHead));

        for (int i = 0; i < 12; ++i)
            g_fakePose[i] = (float) c.m[i];
    }

    configure(cfg, 80);

    // The game's frame: thirds of key, nearly key, not key, in each eye.
    const uint32_t w = 192, h = 64, eyeW = 96;
    const float thirds[3][3] = { { 0.0f, 1.0f, 0.0f }, { 0.05f, 0.8f, 0.05f }, { 0.4f, 0.3f, 0.2f } };
    Image img(w, h);

    for (uint32_t y = 0; y < h; ++y)
    {
        for (uint32_t x = 0; x < w; ++x)
        {
            const uint32_t third = (x % eyeW) * 3 / eyeW;
            float* p = img.at(x, y);
            p[0] = thirds[third][0];
            p[1] = thirds[third][1];
            p[2] = thirds[third][2];
            p[3] = 1.0f;
        }
    }

    ID3D11Texture2D* frame = MakeTexture(w, h, DXGI_FORMAT_R16G16B16A16_TYPELESS, kRT);
    UploadHalf(frame, img);
    g_event(pushMatte(frame, 2, 0, 0, 1, headNow, 0));
    g_event(pushMatte(frame, 2, 1, 0, 1, headNow, 0));
    g_ctx->Flush();
    Drain(true);

    CHECK(start((void*) &FakeFrame, 1, cw, ch) == 1, "the camera thread starts");

    std::vector<uint8_t> picture((size_t) 3072 * 1536 * 4);
    uint32_t pw = 0, ph = 0;
    int filled = 0;

    for (int i = 0; i < 200 && !filled; ++i)
    {
        Sleep(50);
        filled = read(picture.data(), (uint32_t) picture.size(), &pw, &ph);
    }

    int32_t state = 0, error = 0;
    uint32_t frames = 0;
    status(&state, &frames, &error);
    CHECK(filled == 1 && pw == 3072 && ph == 1536, "the overlay drew a picture on its own device (%ux%u, state %d, %u frames, error 0x%X)", pw, ph, state, frames, (unsigned) error);

    if (filled)
    {
        // the rotation between the two heads, as the DLL composes it
        double rot[3][3];

        for (int i = 0; i < 3; ++i)
            for (int j = 0; j < 3; ++j)
                rot[i][j] = headNow[i] * headThen[j] + headNow[4 + i] * headThen[4 + j] + headNow[8 + i] * headThen[8 + j];

        const double reach = cfg[75] * cfg[76];
        int worstGrey = 0, worstAlpha = 0, checked = 0, shown = 0;

        for (uint32_t py = 20; py < ph; py += 61)
        {
            for (uint32_t px = 20; px < pw; px += 67)
            {
                const uint32_t eye = px >= pw / 2 ? 1 : 0;
                const double u = (px + 0.5 - eye * (pw / 2.0)) / (pw / 2.0), v = (py + 0.5) / ph;
                const double onQuad[3] = { (2 * u - 1) * reach, (1 - 2 * v) * reach, -cfg[76] };
                const double from[3] = { eye == 0 ? -0.0325 : 0.0325, 0, 0 };
                double ray[3] = { onQuad[0] - from[0], onQuad[1] - from[1], onQuad[2] - from[2] };
                const double len = sqrt(ray[0] * ray[0] + ray[1] * ray[1] + ray[2] * ray[2]);

                for (double& c : ray)
                    c /= len;

                // the camera's grey: PassRoom takes a point of the eye's space through eyeToCam, so
                // give it the head's point and the head -> camera transform
                const Rig34 headToCam = RigInverse(RigFrom(camToHead + eye * 12));
                const double at[3] = { from[0] + ray[0] * cfg[6], from[1] + ray[1] * cfg[6], from[2] + ray[2] * cfg[6] };
                double q[3];

                for (int i = 0; i < 3; ++i)
                    q[i] = headToCam.m[i * 4] * at[0] + headToCam.m[i * 4 + 1] * at[1] + headToCam.m[i * 4 + 2] * at[2] + headToCam.m[i * 4 + 3];

                const double across = std::max(sqrt(q[0] * q[0] + q[1] * q[1]), 1e-9);
                const double angle = atan2(across, -q[2]);
                const double a2 = angle * angle;
                const double radius = cfg[7] * angle * (1.0 + a2 * (cfg[8] + a2 * (cfg[9] + a2 * (cfg[10] + a2 * cfg[11]))));
                const double lensW = cw / 2;
                const double inside = std::min(std::max((lensW * 0.5 - 6.0 - radius) / 40.0, 0.0), 1.0);
                double tx = std::min(std::max(cfg[12 + eye * 2] + radius * q[0] / across, 0.0), lensW - 1.0);
                double ty = std::min(std::max(cfg[13 + eye * 2] - radius * q[1] / across, 0.0), (double) ch - 1.0);
                const int x0 = (int) floor(tx), y0 = (int) floor(ty);
                const int x1 = std::min(x0 + 1, (int) lensW - 1), y1 = std::min(y0 + 1, (int) ch - 1);
                const double fx = tx - x0, fy = ty - y0;
                const int ox = (int) (eye * lensW);
                const double grey = (g_fakeCam[(size_t) y0 * cw + ox + x0] * (1 - fx) * (1 - fy) + g_fakeCam[(size_t) y0 * cw + ox + x1] * fx * (1 - fy) +
                                     g_fakeCam[(size_t) y1 * cw + ox + x0] * (1 - fx) * fy + g_fakeCam[(size_t) y1 * cw + ox + x1] * fx * fy) / 255.0;
                const int wantGrey = (int) lround(std::min(std::max(grey * cfg[5], 0.0), 1.0) * 255.0);

                // the matte, in the direction the game's picture has this line of sight
                double g[3];

                for (int i = 0; i < 3; ++i)
                    g[i] = rot[i][0] * ray[0] + rot[i][1] * ray[1] + rot[i][2] * ray[2];

                double alpha = 0.0;
                bool sure = true;

                if (g[2] < -1e-3)
                {
                    const float* tn = tangents + eye * 4;
                    const double mx = (g[0] / -g[2] - tn[0]) / (tn[1] - tn[0]), my = (-g[1] / -g[2] - tn[2]) / (tn[3] - tn[2]);

                    if (mx >= 0 && mx <= 1 && my >= 0 && my <= 1)
                    {
                        // well inside one third, away from where two colours were filtered together
                        const double third = mx * 3.0, part = third - floor(third);
                        sure = part > 0.2 && part < 0.8 && mx > 0.03 && mx < 0.97 && my > 0.03 && my < 0.97;
                        const float* c = thirds[std::min((int) floor(third), 2)];
                        alpha = PassMatte(cfg, L2S(c[0]), L2S(c[1]), L2S(c[2]));
                    }
                    else
                    {
                        sure = mx < -0.03 || mx > 1.03 || my < -0.03 || my > 1.03;
                    }
                }

                const uint8_t* got = &picture[((size_t) py * pw + px) * 4];
                worstGrey = std::max(worstGrey, abs((int) got[0] - wantGrey));

                if (sure)
                {
                    worstAlpha = std::max(worstAlpha, abs((int) got[3] - (int) lround(alpha * inside * 255.0)));
                    checked++;

                    if (alpha * inside > 0.9)
                        shown++;
                }
            }
        }

        CHECK(worstGrey <= 3, "the room on the quad is what the reference has (worst %d/255)", worstGrey);
        CHECK(worstAlpha <= 3, "it shows where the game's frame is the key colour, seen from the turned head (worst %d/255 over %d texels)", worstAlpha, checked);
        CHECK(shown > 20, "some of the quad is the room (%d of the texels checked)", shown);
    }

    stop();
    frame->Release();
    Drain(true);
}

int wmain(int argc, wchar_t** argv)
{
    const wchar_t* dllPath = argc > 1 ? argv[1] : L"VamDlssNrWorkScaleNative.dll";
    // "warp" runs on the software rasterizer (no GPU at all); "timing" adds the cost measurement,
    // which allocates frame-size textures and only means something on an idle GPU.
    bool warp = false, timing = false;

    for (int i = 2; i < argc; ++i)
    {
        warp = warp || wcscmp(argv[i], L"warp") == 0;
        timing = timing || wcscmp(argv[i], L"timing") == 0;
    }

    g_warp = warp;

    HMODULE dll = LoadLibraryW(dllPath);

    if (dll == nullptr)
    {
        printf("could not load %ls (error %lu)\n", dllPath, GetLastError());
        return 2;
    }

    vws_abi = (AbiFn) GetProcAddress(dll, "vws_abi");
    vws_event_func = (EventFuncFn) GetProcAddress(dll, "vws_event_func");
    vws_push = (PushFn) GetProcAddress(dll, "vws_push");
    vws_status = (StatusFn) GetProcAddress(dll, "vws_status");
    vws_drain_log = (DrainFn) GetProcAddress(dll, "vws_drain_log");
    vws_push_register = (PushRegisterFn) GetProcAddress(dll, "vws_push_register");
    vws_push_pass = (PushPassFn) GetProcAddress(dll, "vws_push_pass");
    vws_push_release = (PushReleaseFn) GetProcAddress(dll, "vws_push_release");
    vws_poll = (PollFn) GetProcAddress(dll, "vws_poll");

    if (!vws_abi || !vws_event_func || !vws_push || !vws_status || !vws_drain_log || !vws_push_register ||
        !vws_push_pass || !vws_push_release || !vws_poll)
    {
        printf("an export is missing\n");
        return 2;
    }

    printf("abi %u, sizeof(VwsCmd)=%zu, sizeof(VwsStatus)=%zu\n", vws_abi(), sizeof(VwsCmd), sizeof(VwsStatus));
    g_event = (RenderEventFn) vws_event_func();

    D3D_FEATURE_LEVEL level = D3D_FEATURE_LEVEL_11_0;
    D3D_FEATURE_LEVEL got {};
    HRESULT hr = D3D11CreateDevice(nullptr, warp ? D3D_DRIVER_TYPE_WARP : D3D_DRIVER_TYPE_HARDWARE, nullptr,
                                   D3D11_CREATE_DEVICE_SINGLETHREADED, &level, 1, D3D11_SDK_VERSION, &g_dev, &got,
                                   &g_ctx);

    if (FAILED(hr))
    {
        printf("D3D11CreateDevice failed 0x%08X\n", (unsigned) hr);
        return 2;
    }

    {
        IDXGIDevice* dxgi = nullptr;
        IDXGIAdapter* adapter = nullptr;
        DXGI_ADAPTER_DESC ad {};

        if (SUCCEEDED(g_dev->QueryInterface(__uuidof(IDXGIDevice), (void**) &dxgi)) &&
            SUCCEEDED(dxgi->GetAdapter(&adapter)) && SUCCEEDED(adapter->GetDesc(&ad)))
            printf("device: %ls\n", ad.Description);

        if (adapter) adapter->Release();
        if (dxgi) dxgi->Release();
    }

    TestHalfIdentity(DXGI_FORMAT_R8G8B8A8_TYPELESS, DXGI_FORMAT_R16G16B16A16_FLOAT, "Unity's formats");
    Drain(true);
    TestHalfIdentity(DXGI_FORMAT_R8G8B8A8_TYPELESS, DXGI_FORMAT_R16G16B16A16_TYPELESS, "typeless half frame");
    Drain(true);
    TestHalfIdentity(DXGI_FORMAT_R8G8B8A8_UNORM, DXGI_FORMAT_R16G16B16A16_FLOAT, "typed UNORM");
    Drain(true);
    TestHalfIdentity(DXGI_FORMAT_R8G8B8A8_UNORM_SRGB, DXGI_FORMAT_R16G16B16A16_FLOAT, "typed sRGB, hardware codec");
    Drain(true);
    TestEditTransfer();
    TestCubeScale();
    TestSharpen();
    TestPassthrough(dll);
    TestOverlay(dll);
    Drain(true);
    TestStereoSeam();
    TestWindow();
    Drain(true);
    TestOddRatio();
    TestSupersample();
    TestStateRestore();
    Drain(false);
    TestErrorsAndLifetime();
    Drain(true);
    TestSplitRegistration();
    Drain(true);
    TestDeviceChange();
    Drain(true);

    if (timing)
    {
        TestTiming(2560, 1440, 1280, 720, 1);
        TestTiming(2560, 1440, 640, 360, 1);
        TestTiming(4096, 2240, 2048, 1120, 2);
        Drain(false);
    }

    g_ctx->ClearState();
    g_ctx->Flush();
    g_ctx->Release();
    g_dev->Release();

    printf("\n%d checks, %d failure(s)\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
