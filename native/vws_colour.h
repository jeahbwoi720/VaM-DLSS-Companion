// VaM DLSS - Model Resolution: a guess at the room's colours.
//
// The headset's cameras are monochrome. A network trained to colour black-and-white photographs
// (DDColor, its smallest variant; Apache-2.0) is run on a small copy of ONE lens's picture, on
// the processor, on a thread of its own, a few times a second. What it gives is kept as colour
// alone -- two numbers a texel, at a fraction of the picture's size -- and laid under the
// camera's own brightness where the room is drawn: the eye takes its detail from the brightness,
// so the colour may be coarse and late.
//
// One lens, for both eyes: asked about each lens by itself the network colours the same wall red
// in one and leaves it white in the other, and two eyes that disagree are worse than two that
// are both wrong. Each eye looks its colour up in that one lens's picture, through the point
// the room is taken to be at.
//
// It is a guess. The cameras see near-infrared as well as light, so the grey is not the grey of
// a photograph; and nothing ties one answer to the next but the averaging done here.
#pragma once

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>

namespace vwscolour
{
const int kIn = 256;   // the network's picture, per side
const int kOut = 128;  // the colours kept, per side

// ---- plain arithmetic (the checks run these) ----------------------------------------------------

// One lens's picture (the frame's columns from `x0`, `lensW` of them) averaged down to kIn x kIn,
// 0..1, times the brightness the room is shown at.
inline void Shrink(const uint8_t* grey, uint32_t w, uint32_t h, uint32_t x0, uint32_t lensW, float gain, float* out)
{
    for (int y = 0; y < kIn; ++y)
    {
        const uint32_t ya = (uint32_t) ((uint64_t) y * h / kIn), yb = (uint32_t) ((uint64_t) (y + 1) * h / kIn);

        for (int x = 0; x < kIn; ++x)
        {
            const uint32_t xa = (uint32_t) ((uint64_t) x * lensW / kIn), xb = (uint32_t) ((uint64_t) (x + 1) * lensW / kIn);
            uint32_t sum = 0, n = 0;

            for (uint32_t sy = ya; sy < yb || sy == ya; ++sy)
            {
                const uint8_t* row = grey + (size_t) (sy < h ? sy : h - 1) * w + x0;

                for (uint32_t sx = xa; sx < xb || sx == xa; ++sx)
                {
                    sum += row[sx < lensW ? sx : lensW - 1];
                    ++n;
                }
            }

            const float v = (float) sum / (255.0f * (float) n) * gain;
            out[y * kIn + x] = v < 0.0f ? 0.0f : (v > 1.0f ? 1.0f : v);
        }
    }
}

inline float Decode(float v)
{
    return v <= 0.04045f ? v / 12.92f : powf((v + 0.055f) / 1.055f, 2.4f);
}

inline float Encode(float v)
{
    v = v < 0.0f ? 0.0f : (v > 1.0f ? 1.0f : v);
    return v <= 0.0031308f ? v * 12.92f : 1.055f * powf(v, 1.0f / 2.4f) - 0.055f;
}

// A grey as displayed, with the network's a and b (CIE Lab, D65), as a displayed colour's two
// differences from its own brightness: Cb and Cr of BT.601, each within a half of nought. The
// shader adds them to the camera's grey -- so a colour the grey has no room for (a strong red on
// near-white) comes out weaker there, never brighter or darker than the camera saw.
inline void Differences(float grey, float a, float b, float* cb, float* cr)
{
    const float y = Decode(grey);
    const float fy = y > 0.008856f ? cbrtf(y) : 7.787f * y + 16.0f / 116.0f;
    const float fx = fy + a / 500.0f, fz = fy - b / 200.0f;
    const float xr = fx > 0.206897f ? fx * fx * fx : (fx - 16.0f / 116.0f) / 7.787f;
    const float zr = fz > 0.206897f ? fz * fz * fz : (fz - 16.0f / 116.0f) / 7.787f;
    const float X = xr * 0.950456f, Y = y, Z = zr * 1.088754f;
    const float r = Encode(3.2404542f * X - 1.5371385f * Y - 0.4985314f * Z);
    const float g = Encode(-0.9692660f * X + 1.8760108f * Y + 0.0415560f * Z);
    const float bl = Encode(0.0556434f * X - 0.2040259f * Y + 1.0572252f * Z);
    const float luma = 0.299f * r + 0.587f * g + 0.114f * bl;
    *cb = (bl - luma) / 1.772f;
    *cr = (r - luma) / 1.402f;
}

// The network's answer (`ab`: kIn x kIn of a, then of b) for the picture it was given (`light`),
// as kOut x kOut pairs of differences.
inline void Colours(const float* light, const float* ab, float* out)
{
    const int step = kIn / kOut;
    const float each = 1.0f / (float) (step * step);

    for (int y = 0; y < kOut; ++y)
    {
        for (int x = 0; x < kOut; ++x)
        {
            float g = 0.0f, a = 0.0f, b = 0.0f;

            for (int sy = 0; sy < step; ++sy)
            {
                for (int sx = 0; sx < step; ++sx)
                {
                    const int at = (y * step + sy) * kIn + x * step + sx;
                    g += light[at];
                    a += ab[at];
                    b += ab[kIn * kIn + at];
                }
            }

            Differences(g * each, a * each, b * each, &out[(y * kOut + x) * 2], &out[(y * kOut + x) * 2 + 1]);
        }
    }
}

// How much of a new answer is taken over what was kept. The network changes its mind a little
// with every picture, which shows as shimmer while the head is still: there a third. But what was
// kept belongs to where the lens pointed then, and a texel of it is about a degree and a half:
// once the head has turned by more than that the old answer is of other things, and the new one
// is taken as it comes.
inline float Take(float turnedDegrees, bool turnKnown)
{
    if (!turnKnown)
        return 0.5f;

    const float take = 0.3f + turnedDegrees / 3.0f;
    return take > 1.0f ? 1.0f : take;
}

// The angle between two poses' orientations (3x4, row-major), in degrees.
inline float Turned(const float* a, const float* b)
{
    float trace = 0.0f;

    for (int i = 0; i < 3; ++i)
        for (int j = 0; j < 3; ++j)
            trace += a[i * 4 + j] * b[i * 4 + j];

    float c = (trace - 1.0f) * 0.5f;
    c = c < -1.0f ? -1.0f : (c > 1.0f ? 1.0f : c);
    return acosf(c) * 57.29578f;
}

inline void Blend(float* kept, const float* fresh, float take)
{
    for (int i = 0; i < kOut * kOut * 2; ++i)
        kept[i] += (fresh[i] - kept[i]) * take;
}

// As the texture has them: Cb, Cr about 128, and whether there is anything.
inline void Bytes(const float* kept, uint8_t* out)
{
    for (int i = 0; i < kOut * kOut; ++i)
    {
        for (int c = 0; c < 2; ++c)
        {
            const float v = kept[i * 2 + c] * 255.0f + 128.0f;
            out[i * 4 + c] = (uint8_t) (v < 0.0f ? 0.0f : (v > 255.0f ? 255.0f : v + 0.5f));
        }

        out[i * 4 + 2] = 0;
        out[i * 4 + 3] = 255;
    }
}

// ---- the network ----------------------------------------------------------------------------------

#ifdef ORT_API_VERSION

struct Network
{
    HMODULE dll = nullptr;
    const OrtApi* api = nullptr;
    OrtEnv* env = nullptr;
    OrtSession* session = nullptr;
    OrtMemoryInfo* memory = nullptr;
    float* in = nullptr; // the picture three times over, as the network takes it
    char error[256] = {};

    bool Ok(OrtStatus* status, const char* what)
    {
        if (status == nullptr)
            return true;

        if (error[0] == 0)
            snprintf(error, sizeof(error), "%s: %s", what, api->GetErrorMessage(status));

        api->ReleaseStatus(status);
        return false;
    }

    // `folder` ends in a backslash and holds onnxruntime.dll and colour.onnx. `threads`: how many
    // of the processor's a run may use (the game's own threads come first: see ColourThread).
    bool Load(const wchar_t* folder, int threads)
    {
        if (session != nullptr)
            return true;

        error[0] = 0;
        wchar_t path[MAX_PATH * 2];
        swprintf(path, MAX_PATH * 2, L"%scolour.onnx", folder);

        if (GetFileAttributesW(path) == INVALID_FILE_ATTRIBUTES)
        {
            snprintf(error, sizeof(error), "colour.onnx is not beside the plugin (it is a separate download: see the README)");
            return false;
        }

        swprintf(path, MAX_PATH * 2, L"%sonnxruntime.dll", folder);
        dll = LoadLibraryExW(path, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);

        if (dll == nullptr)
        {
            snprintf(error, sizeof(error), "onnxruntime.dll would not load (error %lu)", GetLastError());
            return false;
        }

        typedef const OrtApiBase*(ORT_API_CALL * BaseFn)(void);
        const BaseFn base = (BaseFn) GetProcAddress(dll, "OrtGetApiBase");
        api = base != nullptr ? base()->GetApi(ORT_API_VERSION) : nullptr;

        if (api == nullptr)
        {
            snprintf(error, sizeof(error), "onnxruntime.dll is not the version this was built for (%d)", ORT_API_VERSION);
            Free();
            return false;
        }

        OrtSessionOptions* options = nullptr;
        bool ok = Ok(api->CreateEnv(ORT_LOGGING_LEVEL_ERROR, "vws", &env), "environment") &&
                  Ok(api->CreateSessionOptions(&options), "options") &&
                  Ok(api->SetIntraOpNumThreads(options, threads < 1 ? 1 : threads), "options") && Ok(api->SetInterOpNumThreads(options, 1), "options") &&
                  Ok(api->SetSessionExecutionMode(options, ORT_SEQUENTIAL), "options") &&
                  Ok(api->SetSessionGraphOptimizationLevel(options, ORT_ENABLE_ALL), "options") &&
                  // its threads sleep between runs: spinning ones would take a core each from the game
                  Ok(api->AddSessionConfigEntry(options, "session.intra_op.allow_spinning", "0"), "options") &&
                  Ok(api->CreateCpuMemoryInfo(OrtArenaAllocator, OrtMemTypeDefault, &memory), "memory");

        if (ok)
        {
            swprintf(path, MAX_PATH * 2, L"%scolour.onnx", folder);
            ok = Ok(api->CreateSession(env, path, options, &session), "colour.onnx");
        }

        if (options != nullptr)
            api->ReleaseSessionOptions(options);

        if (ok)
            in = new float[(size_t) kIn * kIn * 3];
        else
            Free();

        return ok;
    }

    // `light`: kIn x kIn, 0..1. `ab`: kIn x kIn of a, then of b.
    bool Run(const float* light, float* ab)
    {
        if (session == nullptr)
            return false;

        for (int c = 0; c < 3; ++c)
            memcpy(in + (size_t) c * kIn * kIn, light, sizeof(float) * kIn * kIn);

        const int64_t shape[4] = { 1, 3, kIn, kIn };
        const char* inName = "input";
        const char* outName = "output";
        OrtValue* given = nullptr;
        OrtValue* got = nullptr;
        bool ok = Ok(api->CreateTensorWithDataAsOrtValue(memory, in, sizeof(float) * (size_t) kIn * kIn * 3, shape, 4, ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &given),
                     "the picture") &&
                  Ok(api->Run(session, nullptr, &inName, (const OrtValue* const*) &given, 1, &outName, 1, &got), "a run");
        void* data = nullptr;
        OrtTensorTypeAndShapeInfo* info = nullptr;
        size_t count = 0;

        if (ok)
            ok = Ok(api->GetTensorTypeAndShape(got, &info), "the answer") && Ok(api->GetTensorShapeElementCount(info, &count), "the answer") &&
                 Ok(api->GetTensorMutableData(got, &data), "the answer");

        if (ok && count != (size_t) kIn * kIn * 2)
        {
            snprintf(error, sizeof(error), "colour.onnx answers with %zu numbers where %d were expected: it is not the file this was built for", count, kIn * kIn * 2);
            ok = false;
        }

        if (ok)
            memcpy(ab, data, sizeof(float) * (size_t) kIn * kIn * 2);

        if (info != nullptr)
            api->ReleaseTensorTypeAndShapeInfo(info);

        if (got != nullptr)
            api->ReleaseValue(got);

        if (given != nullptr)
            api->ReleaseValue(given);

        return ok;
    }

    void Free()
    {
        if (api != nullptr)
        {
            if (session != nullptr)
                api->ReleaseSession(session);

            if (memory != nullptr)
                api->ReleaseMemoryInfo(memory);

            if (env != nullptr)
                api->ReleaseEnv(env);
        }

        delete[] in;
        in = nullptr;
        session = nullptr;
        memory = nullptr;
        env = nullptr;
        api = nullptr;
        // (the DLL stays loaded: the hand tracker may be using it)
        dll = nullptr;
    }
};

#endif
}
