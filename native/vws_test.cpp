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
    TestStereoSeam();
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
