// VaM DLSS - Model Resolution: the wearer's hands, from the headset's two cameras.
//
// Two small networks do the seeing (MediaPipe's, as converted for OpenCV's model zoo, run by ONNX
// Runtime on the processor): one finds palms in a picture, the other puts 21 points on a hand it
// is shown upright and close. Neither knows a fisheye lens, so each is given a plain (pinhole)
// picture cut straight out of the lens's: a wide one, looking ahead and a little down, for the
// palm finder; a narrow one aimed at the hand, turned so the fingers point up, for the points.
//
// A point found in one lens is a line of sight from that lens. The lenses stand eight centimetres
// apart, so the two lines for the same point cross, and where they cross is the point in the
// head's space; the head's place in the room at the camera frame's moment carries it to the room.
//
// A hand that was found is followed: the next frame's narrow pictures are aimed by where its
// points were, and the palm finder runs only while there are fewer hands than there could be.
//
// The palm finder is the weak part (it misses a hand one lens sees at a slant); the points network
// is not. So a hand is begun from a palm in EITHER lens and looked for in the other where its size
// says it must be, and a hand one lens loses is kept by the other, as far away as its size says.
//
// ONNX Runtime is loaded by its full path from beside this DLL, never linked: Windows ships an old
// one of the same name in System32, and without the file the rest of the plugin works as before.
#pragma once

#include <windows.h>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>

#include "deps/onnxruntime_c_api.h"

namespace vwshand
{
const int kHands = 2;       // at most
const int kPoints = 21;     // wrist; thumb 1-4; index 5-8; middle 9-12; ring 13-16; little 17-20
const int kPalmPoints = 7;  // what the palm finder gives: wrist, the four knuckles, the thumb's two
const int kDetSize = 192, kLmSize = 224;
const int kAnchors = 2016;
const int kMostPalms = 6;
const int kMostFresh = 3;   // palms a lens puts points on in one look
const int kPalmViews = 4;   // the pictures the palm finder is shown, in turn

// The camera's numbers, as the passthrough's config block has them.
const int kFocalAt = 7, kPolyAt = 8, kCentreAt = 12, kCamToHeadAt = 24;

// The palm finder's seven among the 21.
const int kPalmOf[kPalmPoints] = { 0, 5, 9, 13, 17, 1, 2 };

// The palm's bones -- wrist to each knuckle, knuckle to knuckle -- and what they add up to on a
// grown hand, metres (8.7, 8.8, 8.3, 7.7, 2.2, 2.0, 2.1 cm, measured). A hand seen by one lens
// only is as far away as makes its palm that size.
const int kPalmBones[7][2] = { { 0, 5 }, { 0, 9 }, { 0, 13 }, { 0, 17 }, { 5, 9 }, { 9, 13 }, { 13, 17 } };
const float kPalmSum = 0.398f;

enum Setting : int
{
    kSetPitch = 0,      // how far down the palm finder's picture looks, radians
    kSetReach = 1,      // ...and how far it reaches to each side, as a tangent
    kSetPalmMin = 2,    // a palm is one from this score
    kSetHandMin = 3,    // the points are believed from this score
    kSetGapMost = 4,    // ...and while the two lenses' lines pass this near each other, metres
    kSetLookEvery = 5,  // the palm finder runs every so many frames while a hand is missing
    kSetCutoff = 6,     // smoothing: how slow a movement is taken for noise, Hz
    kSetBeta = 7,       // ...and how quickly that rises with speed, Hz per metre a second
    kSetSwap = 8,       // 1: the network's left is the wearer's right
    kSetHold = 9,       // frames a hand stays where it was last seen before it is given up
    kSetMost = 10,      // hands looked for: 1 or 2
    kSetBright = 11,    // the palm finder's picture is brought up until the whole frame's mean would be this (0..1)
    kSetBrightNear = 12, // ...and the narrow picture the points are found in, until this
    kSetNoGate = 13,    // 1: a hand that turns over between two frames is believed
    kSetNoFold = 14,    // 1: fingers found bent back through the hand are left so
    kSetExposure = 15,  // the palm finder: 0 looks at each picture once, brought up to `bright`; 1 twice, the
                        // second time only to `brightLow`
    kSetExposureNear = 16, // the narrow picture the points are found in: 0 brought up by the whole frame's
                        // mean, 1 by the mean of its own middle, where the hand is
    kSetBrightLow = 17, // the palm finder's second, gentler strength
    kSettings = 24,
};

inline void Defaults(float* s)
{
    memset(s, 0, sizeof(float) * kSettings);
    s[kSetPitch] = 0.35f;
    s[kSetReach] = 1.0f;
    s[kSetPalmMin] = 0.3f;
    s[kSetHandMin] = 0.5f;
    s[kSetGapMost] = 0.03f;
    s[kSetLookEvery] = 3.0f;
    s[kSetCutoff] = 1.5f;
    s[kSetBeta] = 20.0f;
    s[kSetHold] = 10.0f;
    s[kSetMost] = 2.0f;
    s[kSetBright] = 0.7f;
    s[kSetBrightNear] = 0.6f;
    s[kSetExposure] = 1.0f;
    s[kSetExposureNear] = 1.0f;
    s[kSetBrightLow] = 0.3f;
}

// What is said of each hand: 4 + 63 floats.
struct HandOut
{
    float live;     // 1 while it is being followed
    float left;     // 1 the wearer's left hand, 0 the right
    float score;    // the network's, the weaker lens's
    float gap;      // how far apart the two lenses' lines pass, on average, metres; -1 while one lens alone has it
    float point[kPoints][3]; // in the room (or in the head's space, when the head's place was not known)
};

// ---- small vector help ---------------------------------------------------------------------------

inline float Dot(const float* a, const float* b)
{
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
}

inline void Cross(const float* a, const float* b, float* out)
{
    const float x = a[1] * b[2] - a[2] * b[1], y = a[2] * b[0] - a[0] * b[2], z = a[0] * b[1] - a[1] * b[0];
    out[0] = x;
    out[1] = y;
    out[2] = z;
}

inline float Unit(float* a)
{
    const float n = sqrtf(Dot(a, a));

    if (n > 1e-12f)
    {
        a[0] /= n;
        a[1] /= n;
        a[2] /= n;
    }

    return n;
}

// A plain picture's place: its right, up and back in the head's space, and how far it reaches.
struct View
{
    float right[3], up[3], back[3];
    float reach;

    // A place in the picture, in pixels, as a line of sight in the head's space.
    void Sight(float x, float y, int size, float* out) const
    {
        const float tx = (2.0f * x / (float) size - 1.0f) * reach, ty = (2.0f * y / (float) size - 1.0f) * reach;

        for (int i = 0; i < 3; ++i)
            out[i] = right[i] * tx - up[i] * ty - back[i];

        Unit(out);
    }
};

// The picture the points are looked for in, from seven lines of sight to a palm: the hand upright
// (wrist below the middle finger's knuckle), the palm a third of the picture, the fingers' room
// above it.
inline void Aim(const float dirs[kPalmPoints][3], View& view)
{
    float f[3] = { 0, 0, 0 };

    for (int i = 0; i < kPalmPoints; ++i)
        for (int j = 0; j < 3; ++j)
            f[j] += dirs[i][j];

    Unit(f);

    float up[3] = { dirs[2][0] - dirs[0][0], dirs[2][1] - dirs[0][1], dirs[2][2] - dirs[0][2] };
    float along = Dot(up, f);

    for (int j = 0; j < 3; ++j)
        up[j] -= f[j] * along;

    if (Unit(up) < 1e-6f)
    {
        // wrist and knuckle on one line of sight: any upright will do
        const float other[3] = { 0, 1, 0 };
        along = Dot(other, f);

        for (int j = 0; j < 3; ++j)
            up[j] = other[j] - f[j] * along;

        Unit(up);
    }

    const float back[3] = { -f[0], -f[1], -f[2] };
    float right[3];
    Cross(up, back, right);

    float x0 = 1e9f, x1 = -1e9f, y0 = 1e9f, y1 = -1e9f;

    for (int i = 0; i < kPalmPoints; ++i)
    {
        const float depth = Dot(dirs[i], f);
        const float x = Dot(dirs[i], right) / depth, y = Dot(dirs[i], up) / depth;
        x0 = x < x0 ? x : x0;
        x1 = x > x1 ? x : x1;
        y0 = y < y0 ? y : y0;
        y1 = y > y1 ? y : y1;
    }

    const float w = x1 - x0, h = y1 - y0;
    const float cx = (x0 + x1) * 0.5f, cy = (y0 + y1) * 0.5f + 0.4f * h;

    for (int j = 0; j < 3; ++j)
        f[j] += cx * right[j] + cy * up[j];

    Unit(f);
    along = Dot(up, f);

    for (int j = 0; j < 3; ++j)
    {
        view.up[j] = up[j] - f[j] * along;
        view.back[j] = -f[j];
    }

    Unit(view.up);
    Cross(view.up, view.back, view.right);
    view.reach = 1.5f * (w > h ? w : h);

    if (view.reach < 0.02f)
        view.reach = 0.02f;
}

// Where two lines of sight pass closest: the midpoint, how far apart they pass, and whether that
// is in front of both lenses.
inline bool Meet(const float* o0, const float* d0, const float* o1, const float* d1, float* point, float* gap)
{
    const float w[3] = { o0[0] - o1[0], o0[1] - o1[1], o0[2] - o1[2] };
    const float b = Dot(d0, d1), d = Dot(d0, w), e = Dot(d1, w);
    const float den = 1.0f - b * b;

    if (den < 1e-9f)
        return false;

    const float s = (b * e - d) / den, t = (e - b * d) / den;
    float apart = 0.0f;

    for (int i = 0; i < 3; ++i)
    {
        const float p0 = o0[i] + d0[i] * s, p1 = o1[i] + d1[i] * t;
        point[i] = (p0 + p1) * 0.5f;
        apart += (p0 - p1) * (p0 - p1);
    }

    *gap = sqrtf(apart);
    return s > 0.0f && t > 0.0f;
}

// ---- the two networks ------------------------------------------------------------------------------

struct Networks
{
    HMODULE dll = nullptr;
    const OrtApi* api = nullptr;
    OrtEnv* env = nullptr;
    OrtSession* palm = nullptr;
    OrtSession* hand = nullptr;
    OrtMemoryInfo* memory = nullptr;
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

    // `folder` ends in a backslash and holds onnxruntime.dll and the two models.
    bool Load(const wchar_t* folder)
    {
        if (hand != nullptr)
            return true;

        error[0] = 0;
        wchar_t path[MAX_PATH * 2];
        swprintf(path, MAX_PATH * 2, L"%sonnxruntime.dll", folder);
        dll = LoadLibraryExW(path, nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);

        if (dll == nullptr)
        {
            snprintf(error, sizeof(error), "onnxruntime.dll would not load (error %lu: it is not beside the plugin, or the Visual C++ runtime it needs is not installed)", GetLastError());
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
                  // one thread: a run happens on the thread that asks, and takes no others
                  Ok(api->SetIntraOpNumThreads(options, 1), "options") && Ok(api->SetInterOpNumThreads(options, 1), "options") &&
                  Ok(api->SetSessionExecutionMode(options, ORT_SEQUENTIAL), "options") &&
                  Ok(api->SetSessionGraphOptimizationLevel(options, ORT_ENABLE_ALL), "options") &&
                  Ok(api->CreateCpuMemoryInfo(OrtArenaAllocator, OrtMemTypeDefault, &memory), "memory");

        if (ok)
        {
            swprintf(path, MAX_PATH * 2, L"%shand-palm.onnx", folder);
            ok = Ok(api->CreateSession(env, path, options, &palm), "hand-palm.onnx");
        }

        if (ok)
        {
            swprintf(path, MAX_PATH * 2, L"%shand-points.onnx", folder);
            ok = Ok(api->CreateSession(env, path, options, &hand), "hand-points.onnx");
        }

        if (options != nullptr)
            api->ReleaseSessionOptions(options);

        if (!ok)
            Free();

        return ok;
    }

    void Free()
    {
        if (api != nullptr)
        {
            if (hand != nullptr)
                api->ReleaseSession(hand);

            if (palm != nullptr)
                api->ReleaseSession(palm);

            if (memory != nullptr)
                api->ReleaseMemoryInfo(memory);

            if (env != nullptr)
                api->ReleaseEnv(env);
        }

        hand = palm = nullptr;
        memory = nullptr;
        env = nullptr;
        api = nullptr;

        if (dll != nullptr)
            FreeLibrary(dll);

        dll = nullptr;
    }

    // One run: a square picture, three channels, 0..1. The answers are copied out, `counts[i]`
    // floats of each. May be called from several threads at once.
    bool Run(OrtSession* session, float* picture, int size, const char* const* names, int outputs, float* const* out, const int* counts)
    {
        const int64_t shape[4] = { 1, size, size, 3 };
        OrtValue* in = nullptr;
        OrtValue* got[4] = {};
        const char* inName = "input_1";

        OrtStatus* status = api->CreateTensorWithDataAsOrtValue(memory, picture, sizeof(float) * (size_t) size * size * 3, shape, 4,
                                                                 ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &in);

        if (status == nullptr)
            status = api->Run(session, nullptr, &inName, (const OrtValue* const*) &in, 1, names, (size_t) outputs, got);

        bool ok = status == nullptr;

        if (status != nullptr)
            api->ReleaseStatus(status);

        for (int i = 0; i < outputs; ++i)
        {
            void* data = nullptr;

            if (ok && got[i] != nullptr)
            {
                status = api->GetTensorMutableData(got[i], &data);

                if (status != nullptr)
                {
                    api->ReleaseStatus(status);
                    ok = false;
                }
                else
                {
                    memcpy(out[i], data, sizeof(float) * (size_t) counts[i]);
                }
            }

            if (got[i] != nullptr)
                api->ReleaseValue(got[i]);
        }

        if (in != nullptr)
            api->ReleaseValue(in);

        return ok;
    }
};

// ---- the tracker ---------------------------------------------------------------------------------

// One lens's look at one hand.
struct Seen
{
    bool ok;
    float score, rightness; // the network's: how sure it is of a hand, and how right a hand it looks to it
    float dir[kPoints][3];  // lines of sight from the lens, in the head's space
    float at[kPoints][3];   // the same in the narrow picture: pixels, and the network's nearer / further in the same measure
    float world[kPoints][3]; // the network's other answer: the hand in three dimensions, metres, as the picture is turned (x right, y down, z away)
    View view;              // that picture
};

struct Palm
{
    float score;
    float box[4];
    float dir[kPalmPoints][3];
    float middle[3];
};

// A hand that one lens has found and the other is asked to confirm.
struct Fresh
{
    bool ok;                  // both lenses have it, and their lines cross
    float guess[kPoints][3];  // where the first lens alone puts it, in the head's space
    float point[kPoints][3];  // where the two put it
    float gap, score, rightness;
    float palm;               // its size, as the two lenses make it
};

struct Tracker
{
    Networks* nets = nullptr;
    float set[kSettings];

    // the camera
    uint32_t w = 0, h = 0;
    int lensW = 0;
    float focal = 0, poly[4] = {}, centre[2][2] = {}, turn[2][9] = {}, place[2][3] = {};
    const uint8_t* grey = nullptr;
    uint8_t* halved[2] = {}; // each lens at half size, for the pictures coarser than the lens's own
    int smallW = 0, smallH = 0;

    // what each lens's thread is asked and answers
    struct Lens
    {
        bool follow[kHands];
        View followView[kHands];
        Seen seen[kHands];
        bool look;
        bool lookAt;              // ...at `lookView`, where a hand was lost, not at the next of the usual pictures
        View lookView;
        Palm palms[kMostPalms];
        int palmCount;
        int freshCount;           // palms of this lens to put points on
        int freshPalm[kMostFresh];
        Seen fresh[kMostFresh];
        int confirmCount;         // hands the other lens found, to find here
        Fresh confirm[kMostFresh];
        float* picture;  // one picture, as the networks take it
        float* boxes;
        float* scores;
    } lens[2];

    // the hands
    struct Hand
    {
        bool live;
        int missed;
        bool oneLens;             // the last frame had it in one lens only
        int lens;                 // the lens whose shape of it is used
        int settled;              // frames since it last passed between two lenses and one
        bool left;                // which hand it is: settled when it is begun, and kept
        int unlike;               // frames in a row the network has plainly said the other
        float rightness, score, gap;
        float palm;               // the lengths of kPalmBones added up, as the two lenses measure them on this hand
        bool framed;              // `frame` holds something
        int doubted;              // frames in a row the palm has been found turned too far to believe
        float frame[9];           // the palm as last believed: along the hand, out of the palm, across
        float doubt[9];           // ...and as it is now being found, if that goes on
        float shape[kPoints][3];  // the points as last believed, in the room
        float raw[kPoints][3];    // in the room, as last found: what the next narrow pictures are aimed by
        float bias[kPoints][3];   // the step taken up when it passed between two lenses and one, dying away
        float last[kPoints][3];   // raw and bias: what is smoothed
        float smooth[kPoints][3]; // ...smoothed
        float speed[kPoints][3];
        bool started;
    } hand[kHands];

    float headTurn[9] = { 1, 0, 0, 0, 1, 0, 0, 0, 1 }, headPlace[3] = {};
    double lastTime = 0.0;
    uint32_t frames = 0, looks = 0;
    bool lostOne = false;
    float frameMean = 0.2f; // the whole camera frame's, 0..1

    // What happened since it was last asked (the hand thread says it in the log): frames, looks
    // for palms, palms found in each lens, of those the ones the points network believed, hands
    // that came of them, hands given up, frames with a hand followed, and with one followed by
    // one lens alone, lost hands found again in time, hands dropped for being another's double,
    // frames a hand's sudden turn was not believed, and finger joints put back the right way round.
    struct Tally
    {
        uint32_t frames, looks, palms[2], believed, begun, lost, followed, oneLens, found, same, doubted, folded;
    } tally = {};

    // the second lens is worked on a thread of its own
    HANDLE helper = nullptr, go = nullptr, done = nullptr;
    volatile LONG quit = 0;
    int phase = 0;

    Tracker()
    {
        Defaults(set);
        memset(lens, 0, sizeof(lens));
        memset(hand, 0, sizeof(hand));

        for (Lens& l : lens)
        {
            l.picture = new float[kLmSize * kLmSize * 3];
            l.boxes = new float[kAnchors * 18];
            l.scores = new float[kAnchors];
        }
    }

    ~Tracker()
    {
        if (helper != nullptr)
        {
            InterlockedExchange(&quit, 1);
            SetEvent(go);
            WaitForSingleObject(helper, 5000);
            CloseHandle(helper);
        }

        if (go != nullptr)
            CloseHandle(go);

        if (done != nullptr)
            CloseHandle(done);

        for (Lens& l : lens)
        {
            delete[] l.picture;
            delete[] l.boxes;
            delete[] l.scores;
        }

        delete[] halved[0];
        delete[] halved[1];
    }

    void Forget()
    {
        memset(hand, 0, sizeof(hand));
        frames = looks = 0;
        lastTime = 0.0;
    }

    // ---- the lens ----

    // A line of sight in the head's space -> where the lens pictures it (pixels of its own picture).
    bool Pixel(int l, const float* d, float& u, float& v) const
    {
        const float* m = turn[l];
        const float x = m[0] * d[0] + m[3] * d[1] + m[6] * d[2], y = m[1] * d[0] + m[4] * d[1] + m[7] * d[2],
                    z = m[2] * d[0] + m[5] * d[1] + m[8] * d[2];
        const float across = sqrtf(x * x + y * y) + 1e-9f;
        const float angle = atan2f(across, -z);
        const float a2 = angle * angle;
        const float radius = focal * angle * (1.0f + a2 * (poly[0] + a2 * (poly[1] + a2 * (poly[2] + a2 * poly[3]))));
        u = centre[l][0] + radius * x / across;
        v = centre[l][1] - radius * y / across;
        return radius <= (float) lensW * 0.5f - 12.0f;
    }

    // A plain picture cut out of the lens's and brought up, as the networks take it. The frames
    // are dark (a mean of 45 in 255), and the networks want a hand they can see into.
    //
    // The palm finder's picture (`byMiddle` false): everything is multiplied by what would take the
    // whole frame's mean to `bright`, and what burns out burns out. Measured by day on a saved frame
    // over 72 views: stretching each picture between its darkest and brightest hundredths found
    // the palm in 39, this in 71.
    //
    // A picture aimed at a hand (`byMiddle`): the hand is in its middle, and what the hand needs
    // has nothing to do with what is around it. In front of a window by day it is the darkest
    // thing in the picture; under a lamp at night it is the brightest, and the whole frame's mean
    // burns it out. So the mean of the picture's middle is brought to `bright`.
    void Cut(int l, const View& view, int size, float bright, bool byMiddle, float* picture) const
    {
        // a picture coarser than the lens's own reads the half-size copy
        const bool coarse = (2.0f * view.reach / (float) size) * focal > 1.5f;
        const uint8_t* src = coarse ? halved[l] : grey + l * lensW;
        const int sw = coarse ? smallW : lensW, sh = coarse ? smallH : (int) h;
        const size_t stride = coarse ? (size_t) smallW : (size_t) w;
        const float scale = coarse ? 0.5f : 1.0f, shift = coarse ? -0.25f : 0.0f;
        const float gain = (bright > 0.0f ? bright : 0.7f) / (frameMean > 0.004f ? frameMean : 0.004f) / 255.0f;
        double middleLight = 0.0;
        uint32_t middleCount = 0;

        for (int y = 0; y < size; ++y)
        {
            const float ty = (2.0f * ((float) y + 0.5f) / (float) size - 1.0f) * view.reach;

            for (int x = 0; x < size; ++x)
            {
                const float tx = (2.0f * ((float) x + 0.5f) / (float) size - 1.0f) * view.reach;
                const float d[3] = { view.right[0] * tx - view.up[0] * ty - view.back[0], view.right[1] * tx - view.up[1] * ty - view.back[1],
                                     view.right[2] * tx - view.up[2] * ty - view.back[2] };
                float u, v;
                float value = 0.0f;

                if (Pixel(l, d, u, v))
                {
                    const float fx = u * scale + shift, fy = v * scale + shift;

                    if (fx >= 0.0f && fy >= 0.0f && fx <= (float) sw - 1.001f && fy <= (float) sh - 1.001f)
                    {
                        const int x0 = (int) fx, y0 = (int) fy;
                        const float ax = fx - (float) x0, ay = fy - (float) y0;
                        const uint8_t* p = src + (size_t) y0 * stride + x0;
                        value = ((float) p[0] * (1.0f - ax) + (float) p[1] * ax) * (1.0f - ay) +
                                ((float) p[stride] * (1.0f - ax) + (float) p[stride + 1] * ax) * ay;

                        if (!byMiddle)
                        {
                            value *= gain;
                            value = value > 1.0f ? 1.0f : value;
                        }
                        else if (fabsf(tx) < 0.45f * view.reach && fabsf(ty) < 0.45f * view.reach)
                        {
                            middleLight += value;
                            ++middleCount;
                        }
                    }
                }

                float* to = picture + (size_t) (y * size + x) * 3;
                to[0] = to[1] = to[2] = value;
            }
        }

        if (!byMiddle)
            return;

        // (a middle that is outside the lens altogether: as the palm finder's picture is brought up)
        const float light = middleCount > 16 ? (float) (middleLight / (double) middleCount) : frameMean * 255.0f;
        float lift = (bright > 0.0f ? bright : 0.6f) * 255.0f / (light > 1.0f ? light : 1.0f);
        lift = lift < 0.8f ? 0.8f : lift > 12.0f ? 12.0f : lift;
        const size_t all = (size_t) size * size * 3;

        for (size_t i = 0; i < all; i += 3)
        {
            const float out = picture[i] * lift / 255.0f;
            picture[i] = picture[i + 1] = picture[i + 2] = out > 1.0f ? 1.0f : out;
        }
    }

    // The palm finder's pictures. One wide view shows a hand held low or to the side too small
    // and too slanted for it (in a frame from the headset it found neither hand that way, and both
    // in narrower views aimed at them), so the looks take turns: wide, then narrower to the left,
    // to the right, and down the middle.
    void PalmView(int which, View& view) const
    {
        static const float kTurns[kPalmViews][3] = { // reach as a factor, further down, to the right (radians)
            { 1.0f, 0.0f, 0.0f }, { 0.7f, 0.15f, -0.4f }, { 0.7f, 0.15f, 0.4f }, { 0.7f, 0.45f, 0.0f } };
        const float* t = kTurns[which % kPalmViews];
        const float pitch = set[kSetPitch] + t[1];
        const float c = cosf(pitch), s = sinf(pitch), cy = cosf(t[2]), sy = sinf(t[2]);
        // looking down by `pitch`, then turned to the right about the head's upright
        const float right[3] = { 1, 0, 0 }, up[3] = { 0, c, -s }, back[3] = { 0, s, c };
        const float* from[3] = { right, up, back };
        float* to[3] = { view.right, view.up, view.back };

        for (int i = 0; i < 3; ++i)
        {
            to[i][0] = cy * from[i][0] - sy * from[i][2];
            to[i][1] = from[i][1];
            to[i][2] = sy * from[i][0] + cy * from[i][2];
        }

        view.reach = set[kSetReach] * t[0];
    }

    // ---- what each lens's thread does ----

    void Points(int l, const View& view, Seen& seen)
    {
        Lens& my = lens[l];
        Cut(l, view, kLmSize, set[kSetBrightNear], set[kSetExposureNear] > 0.5f, my.picture);

        static const char* const names[4] = { "Identity", "Identity_1", "Identity_2", "Identity_3" };
        float points[63], score = 0.0f, rightness = 0.0f, world[63];
        float* const out[4] = { points, &score, &rightness, world };
        const int counts[4] = { 63, 1, 1, 63 };
        seen.ok = false;
        seen.score = 0.0f;

        if (!nets->Run(nets->hand, my.picture, kLmSize, names, 4, out, counts))
            return;

        seen.score = score;
        seen.rightness = rightness;
        seen.ok = score >= set[kSetHandMin];
        seen.view = view;
        memcpy(seen.at, points, sizeof(seen.at));
        memcpy(seen.world, world, sizeof(seen.world));

        for (int i = 0; i < kPoints; ++i)
            view.Sight(points[i * 3], points[i * 3 + 1], kLmSize, seen.dir[i]);
    }

    static float Overlap(const float* a, const float* b)
    {
        const float ow = (a[2] < b[2] ? a[2] : b[2]) - (a[0] > b[0] ? a[0] : b[0]);
        const float oh = (a[3] < b[3] ? a[3] : b[3]) - (a[1] > b[1] ? a[1] : b[1]);

        if (ow <= 0.0f || oh <= 0.0f)
            return 0.0f;

        const float both = ow * oh;
        return both / ((a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - both);
    }

    void Palms(int l)
    {
        Lens& my = lens[l];
        View view;

        if (my.lookAt)
            view = my.lookView;
        else
            PalmView((int) looks, view);

        // No one strength shows a hand everywhere. By day, in front of a window, the hand is the
        // dark thing and wants the frame brought far up; under a lamp at night it is the bright
        // thing, and the same strength burns it and the wall behind it to one white (the palm
        // finder then takes a fingertip or a chair's armrest for a palm). So the picture is looked
        // at twice, strongly and gently, and the palms of both are kept. Measured on recordings,
        // frames with a hand out of 64: at night under a lamp 11 with one strength, 58 with two; by
        // day 64 either way. A look at where a hand was lost is aimed at it: by its own middle.
        const int times = set[kSetExposure] > 0.5f && !my.lookAt ? 2 : 1;
        my.palmCount = 0;

        static const char* const names[2] = { "Identity", "Identity_1" };
        float* const out[2] = { my.boxes, my.scores };
        const int counts[2] = { kAnchors * 18, kAnchors };

        // the score that passes, before the squashing: 1 / (1 + e^-x) >= min
        const float pass = -logf(1.0f / set[kSetPalmMin] - 1.0f);

        for (int time = 0; time < times; ++time)
        {
            if (my.lookAt && set[kSetExposure] > 0.5f)
                Cut(l, view, kDetSize, 0.5f, true, my.picture);
            else
                Cut(l, view, kDetSize, time != 0 ? set[kSetBrightLow] : set[kSetBright], false, my.picture);

            if (!nets->Run(nets->palm, my.picture, kDetSize, names, 2, out, counts))
                return;

            Gather(my, view, pass, times == 1 || time != 0 ? kMostPalms : kMostPalms / 2);
        }
    }

    // The palms in what the palm network just answered, best first, up to `room` in the lens's
    // list (each strength may fill half of it, so that one full of things that are no palms leaves
    // room for the other's).
    void Gather(Lens& my, const View& view, float pass, int room) const
    {
        bool used[kAnchors] = {};

        while (my.palmCount < room)
        {
            int best = -1;

            for (int i = 0; i < kAnchors; ++i)
                if (!used[i] && my.scores[i] >= pass && (best < 0 || my.scores[i] > my.scores[best]))
                    best = i;

            if (best < 0)
                break;

            used[best] = true;

            // The anchors: a 24 x 24 grid with two at each place, then a 12 x 12 grid with six.
            float ax, ay;

            if (best < 24 * 24 * 2)
            {
                ax = ((float) ((best / 2) % 24) + 0.5f) / 24.0f;
                ay = ((float) ((best / 2) / 24) + 0.5f) / 24.0f;
            }
            else
            {
                const int at = (best - 24 * 24 * 2) / 6;
                ax = ((float) (at % 12) + 0.5f) / 12.0f;
                ay = ((float) (at / 12) + 0.5f) / 12.0f;
            }

            const float* b = my.boxes + best * 18;
            const float cx = b[0] + ax * kDetSize, cy = b[1] + ay * kDetSize;
            Palm palm;
            palm.score = 1.0f / (1.0f + expf(-my.scores[best]));
            palm.box[0] = cx - b[2] * 0.5f;
            palm.box[1] = cy - b[3] * 0.5f;
            palm.box[2] = cx + b[2] * 0.5f;
            palm.box[3] = cy + b[3] * 0.5f;

            bool again = false;

            for (int i = 0; i < my.palmCount; ++i)
                again = again || Overlap(palm.box, my.palms[i].box) > 0.3f;

            if (again)
                continue;

            palm.middle[0] = palm.middle[1] = palm.middle[2] = 0.0f;

            for (int i = 0; i < kPalmPoints; ++i)
            {
                view.Sight(b[4 + i * 2] + ax * kDetSize, b[5 + i * 2] + ay * kDetSize, kDetSize, palm.dir[i]);

                for (int j = 0; j < 3; ++j)
                    palm.middle[j] += palm.dir[i][j];
            }

            Unit(palm.middle);
            my.palms[my.palmCount++] = palm;
        }
    }

    // The 21 points in the head's space from one lens alone.
    //
    // The network answers twice: where each joint is in its picture, and a hand in three
    // dimensions ("world landmarks"). The first is exact across the picture and nearly blind in
    // depth -- a fist built from it has its fingers folded flat, first bone straight and the second
    // bent back 160 degrees, which VaM's finger joints (they stop at 75) make a claw of. The second
    // is made for the shape. So the shape is the 3D hand, turned as the picture is, made `palm`
    // large (the lengths of kPalmBones added up -- one lens cannot tell a near small hand from a
    // far large one), and moved to where its joints lie nearest the picture's lines of sight.
    // False when that is no place for a hand.
    bool Alone(int l, const Seen& seen, float palm, float points[kPoints][3]) const
    {
        float shape[kPoints][3];

        for (int i = 0; i < kPoints; ++i)
            for (int j = 0; j < 3; ++j)
                shape[i][j] = seen.view.right[j] * seen.world[i][0] - seen.view.up[j] * seen.world[i][1] - seen.view.back[j] * seen.world[i][2];

        const float own = PalmSize(shape);

        if (own < 0.05f)
            return false;

        const float scale = palm / own;

        // The move t that brings the joints nearest their lines: sum of (I - d d') (s + t) = 0.
        float a[3][3] = {}, b[3] = {};

        for (int i = 0; i < kPoints; ++i)
        {
            const float* d = seen.dir[i];
            float s[3];

            for (int j = 0; j < 3; ++j)
                s[j] = shape[i][j] *= scale;

            const float along = Dot(d, s);

            for (int r = 0; r < 3; ++r)
            {
                for (int c = 0; c < 3; ++c)
                    a[r][c] += (r == c ? 1.0f : 0.0f) - d[r] * d[c];

                b[r] -= s[r] - d[r] * along;
            }
        }

        const float det = a[0][0] * (a[1][1] * a[2][2] - a[1][2] * a[2][1]) - a[0][1] * (a[1][0] * a[2][2] - a[1][2] * a[2][0]) +
                          a[0][2] * (a[1][0] * a[2][1] - a[1][1] * a[2][0]);

        if (fabsf(det) < 1e-9f)
            return false;

        float t[3];

        for (int c = 0; c < 3; ++c)
        {
            float m[3][3];
            memcpy(m, a, sizeof(m));

            for (int r = 0; r < 3; ++r)
                m[r][c] = b[r];

            t[c] = (m[0][0] * (m[1][1] * m[2][2] - m[1][2] * m[2][1]) - m[0][1] * (m[1][0] * m[2][2] - m[1][2] * m[2][0]) +
                    m[0][2] * (m[1][0] * m[2][1] - m[1][1] * m[2][0])) / det;
        }

        const float wrist[3] = { shape[0][0] + t[0], shape[0][1] + t[1], shape[0][2] + t[2] };
        const float away = sqrtf(Dot(wrist, wrist));

        // (behind the lens, or nowhere a hand goes)
        if (away < 0.06f || away > 1.3f || Dot(wrist, seen.dir[0]) <= 0.0f)
            return false;

        for (int i = 0; i < kPoints; ++i)
            for (int j = 0; j < 3; ++j)
                points[i][j] = shape[i][j] + t[j] + place[l][j];

        return true;
    }
    // A hand the other lens found, looked for in this one: where its size puts it, then nearer,
    // then further.
    void Confirm(int l, Fresh& fresh, const Seen& theirs)
    {
        static const float kTries[3] = { 1.0f, 0.75f, 1.35f };
        const int other = 1 - l;
        fresh.ok = false;

        for (float scale : kTries)
        {
            float moved[kPoints][3], dirs[kPalmPoints][3];

            for (int i = 0; i < kPoints; ++i)
                for (int j = 0; j < 3; ++j)
                    moved[i][j] = place[other][j] + (fresh.guess[i][j] - place[other][j]) * scale;

            PalmDirs(moved, l, dirs);
            View view;
            Aim(dirs, view);
            Seen mine;
            Points(l, view, mine);

            if (!mine.ok)
                continue;

            float gap = 0.0f;
            const Seen& a = l == 0 ? mine : theirs;
            const Seen& b = l == 0 ? theirs : mine;

            float ratio = 1.0f;
            int used = 0;

            if (Fuse(a, b, -1, kPalmSum, fresh.point, &gap, &ratio, &used))
            {
                fresh.palm = kPalmSum * ratio;
                fresh.ok = true;
                fresh.gap = gap;
                fresh.score = mine.score < theirs.score ? mine.score : theirs.score;
                fresh.rightness = (mine.rightness + theirs.rightness) * 0.5f;
                return;
            }
        }
    }

    void Pass(int l)
    {
        Lens& my = lens[l];

        if (phase == 1)
        {
            for (int i = 0; i < kHands; ++i)
            {
                my.seen[i].ok = false;

                if (my.follow[i])
                    Points(l, my.followView[i], my.seen[i]);
            }

            my.palmCount = 0;

            if (my.look)
                Palms(l);
        }
        else if (phase == 2)
        {
            for (int i = 0; i < my.freshCount; ++i)
            {
                View view;
                Aim(my.palms[my.freshPalm[i]].dir, view);
                Points(l, view, my.fresh[i]);
            }
        }
        else
        {
            for (int i = 0; i < my.confirmCount; ++i)
                Confirm(l, my.confirm[i], lens[1 - l].fresh[i]);
        }
    }

    static DWORD WINAPI Helper(void* param)
    {
        Tracker* self = (Tracker*) param;

        while (WaitForSingleObject(self->go, INFINITE) == WAIT_OBJECT_0 && self->quit == 0)
        {
            self->Pass(0);
            SetEvent(self->done);
        }

        return 0;
    }

    void Both(int which)
    {
        phase = which;

        if (helper == nullptr)
        {
            go = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            done = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            helper = go != nullptr && done != nullptr ? CreateThread(nullptr, 0, Helper, this, 0, nullptr) : nullptr;

            if (helper != nullptr)
                SetThreadPriority(helper, GetThreadPriority(GetCurrentThread()));
        }

        if (helper == nullptr)
        {
            Pass(0);
            Pass(1);
            return;
        }

        SetEvent(go);
        Pass(1);
        WaitForSingleObject(done, INFINITE);
    }

    // ---- between the head and the room ----

    void ToRoom(const float* p, float* out) const
    {
        for (int i = 0; i < 3; ++i)
            out[i] = headTurn[i * 3] * p[0] + headTurn[i * 3 + 1] * p[1] + headTurn[i * 3 + 2] * p[2] + headPlace[i];
    }

    void ToHead(const float* p, float* out) const
    {
        const float q[3] = { p[0] - headPlace[0], p[1] - headPlace[1], p[2] - headPlace[2] };

        for (int i = 0; i < 3; ++i)
            out[i] = headTurn[i] * q[0] + headTurn[3 + i] * q[1] + headTurn[6 + i] * q[2];
    }

    // The two lenses' looks at one hand, made into one set of points in the head's space.
    //
    // Crossing the two lines of sight point by point is exact when both lenses have put the point
    // on the same place of the hand, and wild when they have not (a fist seen from two sides: the
    // lines of a fingertip the lenses disagree on cross a foot away). So each lens gives its own
    // whole hand (`Alone`) -- always a hand -- and the two together say only how far away it is:
    // for the points whose lines do pass close, the crossing's distance against the hand's, and of
    // those ratios the middle one, each lens's hand being drawn in or out from its lens by its own.
    // The two hands are then one hand found twice: their average, if they agree.
    //
    // `prefer`: the lens whose hand was used last when they did not agree (kept unless the other
    // is plainly surer), or -1. `palm`: the hand's size as Alone takes it. Gives the average
    // distance the agreeing lines pass at, the ratio found, and the lens used. False when fewer
    // than half the points' lines agree or the result is no place for a hand.
    bool Fuse(const Seen& a, const Seen& b, int prefer, float palm, float points[kPoints][3], float* gap, float* ratio, int* lensUsed) const
    {
        const Seen* seen[2] = { &a, &b };
        float alone[2][kPoints][3];

        if (!Alone(0, a, palm, alone[0]) || !Alone(1, b, palm, alone[1]))
            return false;

        float crossed[kPoints][3], apart[kPoints];
        bool agree[kPoints];
        int good = 0;
        float sum = 0.0f;

        for (int i = 0; i < kPoints; ++i)
        {
            agree[i] = Meet(place[0], a.dir[i], place[1], b.dir[i], crossed[i], &apart[i]) && apart[i] < 0.015f;

            if (agree[i])
            {
                ++good;
                sum += apart[i];
            }
        }

        if (good < kPoints / 2)
            return false;

        float scales[2];

        for (int l = 0; l < 2; ++l)
        {
            float ratios[kPoints];
            int count = 0;

            for (int i = 0; i < kPoints; ++i)
            {
                if (!agree[i])
                    continue;

                const float c[3] = { crossed[i][0] - place[l][0], crossed[i][1] - place[l][1], crossed[i][2] - place[l][2] };
                const float s[3] = { alone[l][i][0] - place[l][0], alone[l][i][1] - place[l][1], alone[l][i][2] - place[l][2] };
                const float shape = sqrtf(Dot(s, s));

                if (shape > 1e-4f)
                    ratios[count++] = sqrtf(Dot(c, c)) / shape;
            }

            if (count < kPoints / 2)
                return false;

            // the middle one
            for (int i = 1; i < count; ++i)
            {
                const float v = ratios[i];
                int k = i - 1;

                for (; k >= 0 && ratios[k] > v; --k)
                    ratios[k + 1] = ratios[k];

                ratios[k + 1] = v;
            }

            scales[l] = ratios[count / 2];

            // Lines that are nearly side by side pass close a long way off: two lenses that have a
            // point on different things can agree on a hand at twice the distance. A hand is a
            // hand's size, though, give or take a quarter -- the crossing is believed that far.
            const float least = 0.8f * kPalmSum / palm, most = 1.25f * kPalmSum / palm;
            scales[l] = scales[l] < least ? least : (scales[l] > most ? most : scales[l]);

            for (int i = 0; i < kPoints; ++i)
                for (int j = 0; j < 3; ++j)
                    alone[l][i][j] = place[l][j] + (alone[l][i][j] - place[l][j]) * scales[l];
        }

        int l = a.score >= b.score ? 0 : 1;

        if ((prefer == 0 || prefer == 1) && seen[prefer]->score + 0.1f >= seen[1 - prefer]->score)
            l = prefer;

        // One hand found twice, or two lenses of two minds: then the surer lens's.
        if (Apart(alone[0], alone[1]) < 0.025f)
        {
            for (int i = 0; i < kPoints; ++i)
                for (int j = 0; j < 3; ++j)
                    points[i][j] = (alone[0][i][j] + alone[1][i][j]) * 0.5f;
        }
        else
        {
            memcpy(points, alone[l], sizeof(alone[l]));
        }

        *gap = sum / (float) good;
        *ratio = (scales[0] + scales[1]) * 0.5f;
        *lensUsed = l;
        const float far2 = Dot(points[0], points[0]);
        return *gap <= set[kSetGapMost] && far2 > 0.05f * 0.05f && far2 < 1.5f * 1.5f;
    }
    void PalmDirs(const float points[kPoints][3], int l, float dirs[kPalmPoints][3]) const
    {
        for (int i = 0; i < kPalmPoints; ++i)
        {
            for (int j = 0; j < 3; ++j)
                dirs[i][j] = points[kPalmOf[i]][j] - place[l][j];

            Unit(dirs[i]);
        }
    }

    static float PalmSize(const float points[kPoints][3])
    {
        float sum = 0.0f;

        for (const int* bone : kPalmBones)
        {
            const float d[3] = { points[bone[1]][0] - points[bone[0]][0], points[bone[1]][1] - points[bone[0]][1],
                                 points[bone[1]][2] - points[bone[0]][2] };
            sum += sqrtf(Dot(d, d));
        }

        return sum;
    }

    // The points as found, smoothed: slow movement is taken for noise, quick movement followed (a
    // "one euro" filter, by each point's own speed).
    void Smooth(Hand& hd, const float fresh[kPoints][3], float dt)
    {
        memcpy(hd.last, fresh, sizeof(hd.last));

        if (!hd.started || dt <= 0.0f || dt > 0.5f)
        {
            memcpy(hd.smooth, fresh, sizeof(hd.smooth));
            memset(hd.speed, 0, sizeof(hd.speed));
            hd.started = true;
            return;
        }

        const float pi2 = 6.2831853f;
        const float speedBlend = 1.0f / (1.0f + 1.0f / (pi2 * 1.0f * dt));

        for (int i = 0; i < kPoints; ++i)
        {
            float fast = 0.0f;

            for (int j = 0; j < 3; ++j)
            {
                const float now = (fresh[i][j] - hd.smooth[i][j]) / dt;
                hd.speed[i][j] += (now - hd.speed[i][j]) * speedBlend;
                fast += hd.speed[i][j] * hd.speed[i][j];
            }

            const float cutoff = set[kSetCutoff] + set[kSetBeta] * sqrtf(fast);
            const float blend = 1.0f / (1.0f + 1.0f / (pi2 * cutoff * dt));

            for (int j = 0; j < 3; ++j)
                hd.smooth[i][j] += (fresh[i][j] - hd.smooth[i][j]) * blend;
        }
    }

    // The palm's own frame from the points: along the hand, out of the palm, across. (In SteamVR's
    // right-handed room the knuckles' fan turns one way for a right hand, the other for a left.)
    static void PalmFrame(const float p[kPoints][3], bool left, float f[9])
    {
        static const int kFan[4] = { 5, 9, 13, 17 };
        float along[3], out[3] = { 0, 0, 0 };

        for (int j = 0; j < 3; ++j)
            along[j] = p[9][j] - p[0][j];

        Unit(along);

        for (int k = 0; k < 3; ++k)
        {
            float a[3], b[3], c[3];

            for (int j = 0; j < 3; ++j)
            {
                a[j] = p[kFan[k]][j] - p[0][j];
                b[j] = p[kFan[k + 1]][j] - p[0][j];
            }

            Cross(a, b, c);

            for (int j = 0; j < 3; ++j)
                out[j] += left ? -c[j] : c[j];
        }

        const float lean = Dot(out, along);

        for (int j = 0; j < 3; ++j)
            out[j] -= along[j] * lean;

        Unit(out);
        memcpy(f, along, sizeof(along));
        memcpy(f + 3, out, sizeof(out));
        Cross(along, out, f + 6);
    }

    // How far one palm frame is turned from another, radians.
    static float Turned(const float* a, const float* b)
    {
        const float c = (Dot(a, b) + Dot(a + 3, b + 3) + Dot(a + 6, b + 6) - 1.0f) * 0.5f;
        return acosf(c < -1.0f ? -1.0f : (c > 1.0f ? 1.0f : c));
    }

    // The points as just found go into the hand: kept as they are for aiming the next look, and
    // steadied for showing.
    //
    // A hand does not turn over between one frame and the next; the points sometimes do (a fist
    // from behind, or one lens alone, guessing which way the fingers go). A palm found turned
    // further than a hand turns in that time is not believed: the hand keeps the shape it had,
    // carried to where the wrist is now, until the new turn has been found the same four frames
    // running -- then it was real. `anew`: a hand begun or found again, believed as it is.
    //
    // Fingers found folded back through the hand (further than a finger bends that way) are the
    // same mistake in one finger: they are put the same way round on the palm's side.
    //
    // Two lenses and one lens do not put a hand in quite the same place (one lens goes by the
    // hand's size); when a hand passes from the one to the other the step is taken up whole and
    // let go over a quarter of a second, so the hand does not jump.
    void Place(Hand& hd, const float room[kPoints][3], float dt, bool passed, bool anew)
    {
        float kept[kPoints][3], frame[9];
        memcpy(kept, room, sizeof(kept));
        PalmFrame(room, hd.left, frame);
        bool sure = true;

        if (hd.framed && !anew && dt > 0.0f && set[kSetNoGate] < 0.5f)
        {
            // a hundred and twenty degrees in a thirtieth of a second, more if the frames come
            // slower: no hand turns so; a hand misread upside down does. (At fifty degrees it held
            // back hands that were simply being opened and turned.)
            const float most = 2.1f * (dt < 0.033f ? 1.0f : (dt > 0.046f ? 1.4f : dt / 0.033f));

            if (Turned(hd.frame, frame) > most)
            {
                if (hd.doubted > 0 && Turned(hd.doubt, frame) < 0.7f)
                {
                    ++hd.doubted;
                }
                else
                {
                    memcpy(hd.doubt, frame, sizeof(frame));
                    hd.doubted = 1;
                }

                sure = hd.doubted >= 4;
            }
        }

        if (sure)
        {
            memcpy(hd.frame, frame, sizeof(frame));
            hd.framed = true;
            hd.doubted = 0;
            const float* out = frame + 3;

            for (int f = 1; f < 5 && set[kSetNoFold] < 0.5f; ++f)
            {
                const int knuckle = 1 + f * 4;

                for (int k = 1; k < 4; ++k)
                {
                    float* q = kept[knuckle + k];
                    const float v[3] = { q[0] - kept[knuckle][0], q[1] - kept[knuckle][1], q[2] - kept[knuckle][2] };
                    const float behind = Dot(v, out);

                    // more than some thirty-seven degrees behind the palm's plane
                    if (behind < -0.6f * sqrtf(Dot(v, v)))
                    {
                        for (int j = 0; j < 3; ++j)
                            q[j] -= 2.0f * behind * out[j];

                        ++tally.folded;
                    }
                }
            }

            memcpy(hd.shape, kept, sizeof(kept));
        }
        else
        {
            for (int p = 0; p < kPoints; ++p)
                for (int j = 0; j < 3; ++j)
                    kept[p][j] = room[0][j] + hd.shape[p][j] - hd.shape[0][j];

            ++tally.doubted;
        }

        const float keep = dt > 0.0f && hd.started ? expf(-dt / 0.25f) : 0.0f;
        float shown[kPoints][3];

        for (int p = 0; p < kPoints; ++p)
        {
            for (int j = 0; j < 3; ++j)
            {
                hd.bias[p][j] = passed && hd.started ? hd.last[p][j] - kept[p][j] : hd.bias[p][j] * keep;
                shown[p][j] = kept[p][j] + hd.bias[p][j];
            }
        }

        memcpy(hd.raw, room, sizeof(hd.raw));
        Smooth(hd, shown, dt);
    }
    // How far apart two hands are, point for point, on average (both in the same space).
    static float Apart(const float a[kPoints][3], const float b[kPoints][3])
    {
        float sum = 0.0f;

        for (int p = 0; p < kPoints; ++p)
        {
            const float d[3] = { a[p][0] - b[p][0], a[p][1] - b[p][1], a[p][2] - b[p][2] };
            sum += sqrtf(Dot(d, d));
        }

        return sum / kPoints;
    }

    // ---- a frame ----

    // `grey`: both lenses side by side. `cfg`: the passthrough's numbers. `head`: the head in the
    // room at the frame's moment, 3x4, or null. `time`: seconds, for the smoothing. `out`: kHands.
    void Track(const uint8_t* frame, uint32_t width, uint32_t height, const float* cfg, const float* head, double time, HandOut* out)
    {
        memset(out, 0, sizeof(HandOut) * kHands);

        if (nets == nullptr || nets->hand == nullptr || width < 64 || height < 64)
            return;

        if (width != w || height != h)
        {
            w = width;
            h = height;
            lensW = (int) (w / 2);
            smallW = lensW / 2;
            smallH = (int) h / 2;
            delete[] halved[0];
            delete[] halved[1];
            halved[0] = new uint8_t[(size_t) smallW * smallH];
            halved[1] = new uint8_t[(size_t) smallW * smallH];
            Forget();
        }

        grey = frame;
        focal = cfg[kFocalAt];
        memcpy(poly, cfg + kPolyAt, sizeof(poly));
        uint64_t light = 0;

        for (int l = 0; l < 2; ++l)
        {
            const float* m = cfg + kCamToHeadAt + l * 12;
            centre[l][0] = cfg[kCentreAt + l * 2];
            centre[l][1] = cfg[kCentreAt + l * 2 + 1];

            for (int r = 0; r < 3; ++r)
            {
                for (int c = 0; c < 3; ++c)
                    turn[l][r * 3 + c] = m[r * 4 + c];

                place[l][r] = m[r * 4 + 3];
            }

            for (int y = 0; y < smallH; ++y)
            {
                const uint8_t* a = frame + (size_t) (y * 2) * w + l * lensW;
                const uint8_t* b = a + w;
                uint8_t* to = halved[l] + (size_t) y * smallW;

                for (int x = 0; x < smallW; ++x)
                {
                    to[x] = (uint8_t) ((a[x * 2] + a[x * 2 + 1] + b[x * 2] + b[x * 2 + 1] + 2) >> 2);
                    light += to[x];
                }
            }
        }

        frameMean = (float) ((double) light / ((double) smallW * smallH * 2.0) / 255.0);

        if (head != nullptr)
        {
            for (int r = 0; r < 3; ++r)
            {
                for (int c = 0; c < 3; ++c)
                    headTurn[r * 3 + c] = head[r * 4 + c];

                headPlace[r] = head[r * 4 + 3];
            }
        }
        else
        {
            const float same[9] = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
            memcpy(headTurn, same, sizeof(headTurn));
            headPlace[0] = headPlace[1] = headPlace[2] = 0.0f;
        }

        const float dt = lastTime > 0.0 ? (float) (time - lastTime) : 0.0f;
        lastTime = time;
        const int most = set[kSetMost] < 1.5f ? 1 : kHands;
        const int every = set[kSetLookEvery] < 1.0f ? 1 : (int) set[kSetLookEvery];

        // The hands being followed: each lens's narrow picture, aimed by where the points were.
        int live = 0, astray = -1;
        float was[kHands][kPoints][3];

        for (int i = 0; i < kHands; ++i)
        {
            lens[0].follow[i] = lens[1].follow[i] = hand[i].live;

            if (!hand[i].live)
                continue;

            ++live;

            if (hand[i].missed > 0 && astray < 0)
                astray = i;

            for (int p = 0; p < kPoints; ++p)
                ToHead(hand[i].raw[p], was[i][p]);

            for (int l = 0; l < 2; ++l)
            {
                float dirs[kPalmPoints][3];
                PalmDirs(was[i], l, dirs);
                Aim(dirs, lens[l].followView[i]);
            }
        }

        // The palm finder: every so often while a hand is missing altogether -- and at once, aimed at
        // where it was, for a hand that was being followed and was not found last frame. A hand
        // lost that way is back in its own place within a frame or two, not begun anew as another.
        const bool look = astray >= 0 || (live < most && (lostOne || frames % (uint32_t) every == 0));

        for (int l = 0; l < 2; ++l)
        {
            // (every other look where it was, the others at the usual pictures in turn: it may have gone far)
            const bool there = astray >= 0 && (frames & 1) == 0;
            lens[l].look = look;
            lens[l].lookAt = there;

            if (there)
            {
                float dirs[kPalmPoints][3], middle[3] = { 0, 0, 0 };
                PalmDirs(was[astray], l, dirs);

                for (int p = 0; p < kPalmPoints; ++p)
                    for (int j = 0; j < 3; ++j)
                        middle[j] += dirs[p][j];

                Unit(middle);
                View& view = lens[l].lookView;
                // upright, unless the hand is straight above or below
                const float up0[3] = { 0, fabsf(middle[1]) < 0.95f ? 1.0f : 0.0f, fabsf(middle[1]) < 0.95f ? 0.0f : -1.0f };

                for (int j = 0; j < 3; ++j)
                    view.back[j] = -middle[j];

                Cross(up0, view.back, view.right);
                Unit(view.right);
                Cross(view.back, view.right, view.up);
                view.reach = 0.5f;
            }
        }

        lostOne = false;
        ++frames;
        ++tally.frames;
        tally.looks += look ? 1 : 0;
        tally.followed += live > 0 ? 1 : 0;
        Both(1);

        if (look && !lens[0].lookAt)
            ++looks;

        for (int i = 0; i < kHands; ++i)
        {
            Hand& hd = hand[i];

            if (!hd.live)
                continue;

            float points[kPoints][3], gap = 0.0f;
            const Seen& a = lens[0].seen[i];
            const Seen& b = lens[1].seen[i];
            const bool wasOne = hd.oneLens;
            bool found = false;

            float ratio = 1.0f;
            int used = 0;

            if (a.ok && b.ok && Fuse(a, b, hd.lens, hd.palm, points, &gap, &ratio, &used))
            {
                found = true;
                hd.oneLens = false;
                hd.lens = used;
                hd.gap = gap;
                hd.score = a.score < b.score ? a.score : b.score;
                hd.rightness += ((a.rightness + b.rightness) * 0.5f - hd.rightness) * 0.2f;

                // this hand's own size, for when one lens has to do: what would have made the ratio one
                const float size = hd.palm * ratio;

                if (size > kPalmSum * 0.79f && size < kPalmSum * 1.26f)
                    hd.palm += (size - hd.palm) * 0.2f;
            }
            else if (a.ok || b.ok)
            {
                // One lens has lost it (the hand is at that lens's edge, or it sees it badly), or
                // the two disagree: the surer lens alone, the hand as far away as its size says.
                int l = !b.ok || (a.ok && a.score >= b.score) ? 0 : 1;

                // (the lens whose shape it has been, if that one still has it: two lenses' shapes differ a little)
                if (a.ok && b.ok && (hd.lens == 0 || hd.lens == 1))
                    l = hd.lens;

                const Seen& one = l == 0 ? a : b;

                if (Alone(l, one, hd.palm, points))
                {
                    found = true;
                    hd.oneLens = true;
                    hd.lens = l;
                    hd.gap = -1.0f;
                    hd.score = one.score;
                    hd.rightness += (one.rightness - hd.rightness) * 0.2f;
                    ++tally.oneLens;
                }
            }

            if (found)
            {
                float room[kPoints][3];

                for (int p = 0; p < kPoints; ++p)
                    ToRoom(points[p], room[p]);

                // (a hand that goes to and fro every frame is not held still by taking up each step)
                const bool passed = wasOne != hd.oneLens;
                Place(hd, room, dt, passed && hd.settled > 6, false);
                hd.settled = passed ? 0 : hd.settled + 1;
                hd.missed = 0;
            }
            else if (++hd.missed > (int) set[kSetHold])
            {
                hd.live = false;
                lostOne = true;
                ++tally.lost;
                --live;
            }
        }

        // New hands. A palm in either lens that is no hand seen this frame gets its points found
        // there; if the network believes them, the hand is looked for in the other lens where its
        // size says it is, and taken when the two lenses' lines cross. (The palm finder alone is
        // not asked for both lenses: it often has a hand in one and not in the other.)
        if (look)
        {
            tally.palms[0] += (uint32_t) lens[0].palmCount;
            tally.palms[1] += (uint32_t) lens[1].palmCount;
        }

        bool anyAstray = false;

        for (int i = 0; i < kHands; ++i)
            anyAstray = anyAstray || (hand[i].live && hand[i].missed > 0);

        if (look && (live < most || anyAstray))
        {
            for (int l = 0; l < 2; ++l)
            {
                Lens& my = lens[l];
                my.freshCount = 0;

                for (int k = 0; k < my.palmCount && my.freshCount < kMostFresh; ++k)
                {
                    bool known = false;

                    for (int i = 0; i < kHands && !known; ++i)
                    {
                        // (a hand that was not found this frame is just what a palm here may be)
                        if (!hand[i].live || hand[i].missed > 0)
                            continue;

                        float points[kPoints][3], dirs[kPalmPoints][3], middle[3] = { 0, 0, 0 };

                        for (int p = 0; p < kPoints; ++p)
                            ToHead(hand[i].raw[p], points[p]);

                        PalmDirs(points, l, dirs);

                        for (int p = 0; p < kPalmPoints; ++p)
                            for (int j = 0; j < 3; ++j)
                                middle[j] += dirs[p][j];

                        Unit(middle);

                        // within eleven degrees of a followed hand's palm: that hand
                        known = Dot(middle, my.palms[k].middle) > 0.98f;
                    }

                    if (!known)
                        my.freshPalm[my.freshCount++] = k;
                }
            }

            if (lens[0].freshCount + lens[1].freshCount > 0)
                Both(2);

            // What each lens believed goes to the other to confirm, at the same place in its list.
            bool any = false;

            for (int l = 0; l < 2; ++l)
            {
                Lens& mine = lens[l];
                Lens& other = lens[1 - l];
                other.confirmCount = mine.freshCount;

                for (int k = 0; k < mine.freshCount; ++k)
                {
                    Fresh& f = other.confirm[k];
                    f.ok = false;

                    if (mine.fresh[k].ok && Alone(l, mine.fresh[k], kPalmSum, f.guess))
                    {
                        ++tally.believed;
                        any = true;
                    }
                    else
                    {
                        mine.fresh[k].ok = false;
                    }
                }
            }

            if (any)
            {
                // only the believed ones are worth the other lens's time
                for (int l = 0; l < 2; ++l)
                {
                    Lens& mine = lens[l];
                    Lens& other = lens[1 - l];
                    int kept = 0;

                    for (int k = 0; k < mine.freshCount; ++k)
                    {
                        if (!mine.fresh[k].ok)
                            continue;

                        if (kept != k)
                        {
                            mine.fresh[kept] = mine.fresh[k];
                            other.confirm[kept] = other.confirm[k];
                        }

                        ++kept;
                    }

                    mine.freshCount = other.confirmCount = kept;
                }

                Both(3);

                const bool swap = set[kSetSwap] > 0.5f;

                // First the finds that are a lost hand of their own side, then the rest: a lost
                // hand close by whatever the network makes of its side, or a hand begun.
                for (int pass = 0; pass < 2; ++pass)
                {
                    for (int l = 0; l < 2; ++l)
                    {
                        for (int k = 0; k < lens[l].confirmCount; ++k)
                        {
                            Fresh& f = lens[l].confirm[k];

                            if (!f.ok)
                                continue;

                            const bool looksLeft = (f.rightness < 0.5f) != swap;
                            float room[kPoints][3];

                            for (int p = 0; p < kPoints; ++p)
                                ToRoom(f.point[p], room[p]);

                            // A hand seen this frame, found once more (from the other lens, or by
                            // the palm finder a little off): nothing new.
                            bool known = false;
                            int back = -1;
                            float nearest = 0.15f;

                            for (int i = 0; i < kHands; ++i)
                            {
                                if (!hand[i].live)
                                    continue;

                                const float apart = Apart(hand[i].last, room);

                                if (hand[i].missed == 0)
                                {
                                    known = known || apart < 0.08f;
                                }
                                else if (pass == 0)
                                {
                                    // the lost hand of that side: this is it, wherever it has got to
                                    if (hand[i].left == looksLeft && apart < 0.6f)
                                        back = i;
                                }
                                else if (apart < nearest)
                                {
                                    back = i;
                                    nearest = apart;
                                }
                            }

                            if (known)
                            {
                                f.ok = false;
                                continue;
                            }

                            if (back >= 0)
                            {
                                // The hand that was lost, back in its own place: it keeps its side and its smoothing.
                                Hand& hd = hand[back];
                                hd.missed = 0;
                                hd.oneLens = false;
                                hd.gap = f.gap;
                                hd.score = f.score;
                                hd.rightness += (f.rightness - hd.rightness) * 0.2f;
                                Place(hd, room, dt, false, true);
                                ++tally.found;
                                f.ok = false;
                                continue;
                            }

                            if (pass == 0 || live >= most)
                                continue;

                            for (int i = 0; i < kHands; ++i)
                            {
                                Hand& hd = hand[i];

                                if (hd.live)
                                    continue;

                                memset(&hd, 0, sizeof(hd));
                                ++tally.begun;
                                hd.live = true;
                                hd.gap = f.gap;
                                hd.score = f.score;
                                hd.rightness = f.rightness;
                                hd.palm = kPalmSum;
                                hd.lens = -1;

                                // Which hand it is, settled now. Two hands are never the same one, and
                                // the one already being followed has shown which it is for longer.
                                hd.left = looksLeft;

                                for (int o = 0; o < kHands; ++o)
                                    if (o != i && hand[o].live && hand[o].left == hd.left)
                                        hd.left = !hd.left;

                                if (f.palm > kPalmSum * 0.79f && f.palm < kPalmSum * 1.26f)
                                    hd.palm = f.palm;

                                Place(hd, room, 0.0f, false, true);
                                ++live;
                                break;
                            }

                            f.ok = false;
                        }
                    }
                }
            }
        }
        // Two that have settled on the same hand: the one that is seen, by both lenses, the surer, stays.
        if (hand[0].live && hand[1].live && Apart(hand[0].last, hand[1].last) < 0.04f)
        {
            int drop;

            if ((hand[0].missed > 0) != (hand[1].missed > 0))
                drop = hand[0].missed > 0 ? 0 : 1;
            else if (hand[0].oneLens != hand[1].oneLens)
                drop = hand[0].oneLens ? 0 : 1;
            else
                drop = hand[0].score >= hand[1].score ? 1 : 0;

            hand[drop].live = false;
            ++tally.same;
        }

        // Which is which was settled when each was begun, by how right a hand it looked to the
        // network (in the headset that proved to mean the wearer's right, as it stands). It is
        // changed only when the network has plainly said otherwise for most of a second -- and, with
        // two hands, only when it says so of both.
        const bool swap = set[kSetSwap] > 0.5f;

        for (int i = 0; i < kHands; ++i)
        {
            Hand& hd = hand[i];

            if (!hd.live || hd.missed > 0)
                continue;

            const bool looksLeft = (hd.rightness < 0.5f) != swap;
            hd.unlike = looksLeft != hd.left && fabsf(hd.rightness - 0.5f) > 0.3f ? hd.unlike + 1 : 0;
        }

        for (int i = 0; i < kHands; ++i)
        {
            Hand& hd = hand[i];
            Hand& other = hand[1 - i];

            if (!hd.live || hd.unlike <= 20)
                continue;

            if (!other.live)
            {
                hd.left = !hd.left;
                hd.unlike = 0;
            }
            else if (other.unlike > 20)
            {
                hd.left = !hd.left;
                other.left = !other.left;
                hd.unlike = other.unlike = 0;
            }
        }

        for (int i = 0; i < kHands; ++i)
        {
            const Hand& hd = hand[i];
            out[i].live = hd.live ? 1.0f : 0.0f;
            out[i].left = hd.left ? 1.0f : 0.0f;
            out[i].score = hd.score;
            out[i].gap = hd.gap;
            memcpy(out[i].point, hd.smooth, sizeof(hd.smooth));
        }
    }
};
}
