// VaM DLSS - Model Resolution: the game's window presented the modern way.
//
// Unity 2018 makes its window's swap chain the old way (bit-block transfer): every frame is copied
// to the desktop compositor, which shows what it has when the screen refreshes and drops the rest.
// Frames made by frame generation are among the dropped ones, and nothing that needs the flip model
// -- RTX HDR for one -- can take the window. Here the swap chain is made flip-model instead.
//
// A flip-model back buffer cannot be what Unity asks for (an sRGB format, one buffer), so Unity is
// not given it. It draws into a stand-in texture of exactly the kind it asked for, handed out by
// GetBuffer, and at Present the stand-in is copied onto the real back buffer. To the game nothing
// has changed; to the system and the driver the window is a flip-model one.
//
// DXGI's factory and swap chain are reached through their method tables, which every object of the
// class shares: the entries for CreateSwapChain(ForHwnd), Present, GetBuffer and ResizeBuffers are
// pointed here. That has to be done before Unity makes its swap chain, so it is armed from a BepInEx
// preloader patcher (managed\Early.cs), not from the plugin, which loads long after.
//
// Pacing. Frame generation presents its frames as it makes them: a few in quick succession, then
// nothing while the next real frame is rendered. On screen that is a burst and a hold, and the eye
// sees the base rate. A flip-model swap chain is a queue, though: a frame presented with a sync
// interval of n waits its turn and is then shown for n refreshes, and Present returns at once. So
// with pacing on every present is given the number of refreshes that spreads the frames evenly at
// the rate they are arriving -- the burst sits in the queue and comes out in rhythm, while the
// render thread is already on the next frame. It costs the time a frame waits in the queue.

#pragma once

#include <dxgi1_5.h>
#include <intrin.h>

namespace vwsflip
{
const int kChains = 4;
const int kTables = 4;
const int kSeen = 24;
const UINT kBuffers = 7;  // room for a burst of generated frames to wait in
const int kStamps = 128;  // the presents the arriving rate is taken over

enum State { kOff = 0, kArmed = 1, kAtWork = 2, kLeftAlone = 3, kFailed = 4 };

typedef HRESULT (STDMETHODCALLTYPE* PresentFn)(IDXGISwapChain*, UINT, UINT);
typedef HRESULT (STDMETHODCALLTYPE* GetBufferFn)(IDXGISwapChain*, UINT, REFIID, void**);
typedef HRESULT (STDMETHODCALLTYPE* ResizeFn)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);
typedef HRESULT (STDMETHODCALLTYPE* Present1Fn)(IDXGISwapChain1*, UINT, UINT, const DXGI_PRESENT_PARAMETERS*);
typedef HRESULT (STDMETHODCALLTYPE* CreateFn)(IDXGIFactory*, IUnknown*, DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**);
typedef HRESULT (STDMETHODCALLTYPE* CreateHwndFn)(IDXGIFactory2*, IUnknown*, HWND, const DXGI_SWAP_CHAIN_DESC1*,
                                                   const DXGI_SWAP_CHAIN_FULLSCREEN_DESC*, IDXGIOutput*, IDXGISwapChain1**);

// One swap chain the game was given a stand-in for.
struct Chain
{
    void* self;                   // the swap chain as the game holds it
    ID3D11DeviceContext* context;
    ID3D11Texture2D* stand;       // what the game draws into
    DXGI_FORMAT asked;            // the back buffer format the game asked for
    bool tearing;                 // made with ALLOW_TEARING
    bool tearRefused;             // a tearing present was refused (exclusive fullscreen); until the next resize
    DWORD inside;                 // the thread that is inside the real Present, or 0
    bool mismatch;                // said once: the real back buffer cannot take a copy of the stand-in
};

// One swap chain class's method table, and what its entries were.
struct Table
{
    void** methods;
    PresentFn present;
    GetBufferFn getBuffer;
    ResizeFn resize;
    Present1Fn present1;
};

SRWLOCK g_lock = SRWLOCK_INIT;
Chain g_chains[kChains];
Table g_tables[kTables];
CreateFn g_create = nullptr;
CreateHwndFn g_createHwnd = nullptr;
bool g_armed = false, g_tearing = false;
volatile LONG g_state = kOff;
volatile LONG g_presents = 0, g_width = 0, g_height = 0;

// Pacing: asked for from the plugin's thread, everything else is the presenting thread's.
struct Pace
{
    volatile LONG on = 0;
    volatile LONG multiplier = 2;  // frames shown for each rendered one, for how deep the queue may get
    bool begun = false;
    bool haveLatency = false;
    UINT latencyWas = 0;
    double hz = 60.0;
    LONGLONG stamps[kStamps] = {};
    int at = 0, count = 0;
    double carry = 0.0;
    // for the status line, in thousandths
    volatile LONG rate = 0, refresh = 0, each = 0, queue = 0, dropped = 0;
};

Pace g_pace;

struct Seen { int what; HMODULE from; };
Seen g_seen[kSeen];
int g_seenCount = 0;

// {6B1D4B5E-3C0A-4E0D-9B7E-56D1F0A3C2E1}: under this a swap chain carries the object whose release
// tells that the swap chain is gone.
const GUID kKeeper = { 0x6b1d4b5e, 0x3c0a, 0x4e0d, { 0x9b, 0x7e, 0x56, 0xd1, 0xf0, 0xa3, 0xc2, 0xe1 } };

// Who calls -- the game, the driver's present layer, an overlay -- said once for each caller and
// method: the layers above and below this one are not ours, and what they do shows only here.
void Note(int what, const char* name, void* caller, const char* fmt = nullptr, ...)
{
    HMODULE from = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR) caller, &from);

    AcquireSRWLockExclusive(&g_lock);
    bool known = g_seenCount >= kSeen;

    for (int i = 0; i < g_seenCount && !known; i++)
        known = g_seen[i].what == what && g_seen[i].from == from;

    if (!known)
        g_seen[g_seenCount++] = { what, from };

    ReleaseSRWLockExclusive(&g_lock);

    if (known)
        return;

    char path[MAX_PATH] = "?";

    if (from != nullptr)
        GetModuleFileNameA(from, path, sizeof(path));

    const char* slash = strrchr(path, '\\');
    char more[160] = "";

    if (fmt != nullptr)
    {
        va_list args;
        va_start(args, fmt);
        vsnprintf(more, sizeof(more) - 1, fmt, args);
        va_end(args);
    }

    Log("flip: %s called from %s%s%s", name, slash ? slash + 1 : path, more[0] ? ": " : "", more);
}

DXGI_FORMAT Plain(DXGI_FORMAT f)
{
    return f == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB ? DXGI_FORMAT_R8G8B8A8_UNORM :
           f == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB ? DXGI_FORMAT_B8G8R8A8_UNORM : f;
}

bool Swap(void** slot, void* mine, void** was)
{
    DWORD old = 0;

    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &old))
        return false;

    *was = *slot;
    *slot = mine;
    VirtualProtect(slot, sizeof(void*), old, &old);
    return true;
}

Table* TableOf(void* object)
{
    void** methods = *(void***) object;

    for (int i = 0; i < kTables; i++)
        if (g_tables[i].methods == methods)
            return &g_tables[i];

    return g_tables[0].methods != nullptr ? &g_tables[0] : nullptr;
}

// A copy of the chain's record, with the stand-in and the context held for as long as it is used.
bool Hold(void* self, Chain* out)
{
    bool found = false;
    AcquireSRWLockExclusive(&g_lock);

    for (int i = 0; i < kChains && !found; i++)
    {
        if (g_chains[i].self == self && g_chains[i].stand != nullptr)
        {
            *out = g_chains[i];
            out->stand->AddRef();
            out->context->AddRef();
            found = true;
        }
    }

    ReleaseSRWLockExclusive(&g_lock);
    return found;
}

void Let(Chain& held)
{
    held.stand->Release();
    held.context->Release();
}

Chain* Find(void* self) // under the lock
{
    for (int i = 0; i < kChains; i++)
        if (g_chains[i].self == self)
            return &g_chains[i];

    return nullptr;
}

void Forget(void* self)
{
    ID3D11Texture2D* stand = nullptr;
    ID3D11DeviceContext* context = nullptr;

    AcquireSRWLockExclusive(&g_lock);

    if (Chain* c = Find(self))
    {
        stand = c->stand;
        context = c->context;
        *c = Chain {};
    }

    bool any = false;

    for (int i = 0; i < kChains; i++)
        any = any || g_chains[i].self != nullptr;

    ReleaseSRWLockExclusive(&g_lock);

    if (stand) stand->Release();
    if (context) context->Release();

    g_pace.begun = false;

    if (!any && g_state == kAtWork)
        InterlockedExchange(&g_state, kArmed);
}

// Rides on the swap chain as private data; the swap chain lets go of it when it is destroyed.
struct Keeper : IUnknown
{
    LONG refs = 1;
    void* chain = nullptr;

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** out) override
    {
        if (out == nullptr)
            return E_POINTER;

        if (riid == __uuidof(IUnknown))
        {
            *out = this;
            AddRef();
            return S_OK;
        }

        *out = nullptr;
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return (ULONG) InterlockedIncrement(&refs); }

    ULONG STDMETHODCALLTYPE Release() override
    {
        const LONG left = InterlockedDecrement(&refs);

        if (left == 0)
        {
            Forget(chain);
            delete this;
        }

        return (ULONG) left;
    }
};

// The stand-in for the chain's back buffer: its size, the format the game asked for.
ID3D11Texture2D* MakeStand(IDXGISwapChain* chain, DXGI_FORMAT asked)
{
    DXGI_SWAP_CHAIN_DESC real {};
    ID3D11Device* device = nullptr;
    ID3D11Texture2D* stand = nullptr;

    if (FAILED(chain->GetDesc(&real)) || FAILED(chain->GetDevice(__uuidof(ID3D11Device), (void**) &device)))
        return nullptr;

    D3D11_TEXTURE2D_DESC d {};
    d.Width = real.BufferDesc.Width;
    d.Height = real.BufferDesc.Height;
    d.MipLevels = 1;
    d.ArraySize = 1;
    d.Format = asked;
    d.SampleDesc.Count = 1;
    d.Usage = D3D11_USAGE_DEFAULT;
    d.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;

    const HRESULT hr = device->CreateTexture2D(&d, nullptr, &stand);
    device->Release();

    if (FAILED(hr))
    {
        Log("flip: the stand-in back buffer %ux%u format %d could not be made (0x%08X)", d.Width, d.Height, (int) asked, (unsigned) hr);
        return nullptr;
    }

    InterlockedExchange(&g_width, (LONG) d.Width);
    InterlockedExchange(&g_height, (LONG) d.Height);
    return stand;
}

HRESULT STDMETHODCALLTYPE OnPresent(IDXGISwapChain* self, UINT sync, UINT flags);
HRESULT STDMETHODCALLTYPE OnPresent1(IDXGISwapChain1* self, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* params);
HRESULT STDMETHODCALLTYPE OnGetBuffer(IDXGISwapChain* self, UINT index, REFIID riid, void** out);
HRESULT STDMETHODCALLTYPE OnResize(IDXGISwapChain* self, UINT count, UINT width, UINT height, DXGI_FORMAT format, UINT flags);

// Points this swap chain class's method table here, once for each class.
bool Take(IDXGISwapChain* chain)
{
    void** methods = *(void***) chain;
    IDXGISwapChain1* one = nullptr;
    const bool hasOne = SUCCEEDED(chain->QueryInterface(__uuidof(IDXGISwapChain1), (void**) &one)) && (void*) one == (void*) chain;

    if (one) one->Release();

    AcquireSRWLockExclusive(&g_lock);
    Table* t = nullptr;

    for (int i = 0; i < kTables && t == nullptr; i++)
        if (g_tables[i].methods == methods || g_tables[i].methods == nullptr)
            t = &g_tables[i];

    bool ok = t != nullptr;

    if (ok && t->methods == nullptr)
    {
        ok = Swap(&methods[8], (void*) &OnPresent, (void**) &t->present) &&
             Swap(&methods[9], (void*) &OnGetBuffer, (void**) &t->getBuffer) &&
             Swap(&methods[13], (void*) &OnResize, (void**) &t->resize) &&
             (!hasOne || Swap(&methods[22], (void*) &OnPresent1, (void**) &t->present1));

        if (ok)
            t->methods = methods;
    }

    ReleaseSRWLockExclusive(&g_lock);
    return ok;
}

// A swap chain just made flip-model: the game is given a stand-in from here on.
void Adopt(IDXGISwapChain* chain, DXGI_FORMAT asked, bool tearing)
{
    ID3D11Device* device = nullptr;
    ID3D11DeviceContext* context = nullptr;

    if (FAILED(chain->GetDevice(__uuidof(ID3D11Device), (void**) &device)))
        return;

    device->GetImmediateContext(&context);
    device->Release();
    ID3D11Texture2D* stand = MakeStand(chain, asked);

    if (stand == nullptr || !Take(chain))
    {
        // Without the stand-in the game would be handed a back buffer of a format it did not ask
        // for; nothing can be done about that here any more.
        Log("flip: the swap chain is flip-model but could not be given a stand-in -- expect a wrong picture; switch FlipModel off");
        if (stand) stand->Release();
        context->Release();
        InterlockedExchange(&g_state, kFailed);
        return;
    }

    AcquireSRWLockExclusive(&g_lock);
    Chain* c = Find(chain);

    if (c == nullptr)
        c = Find(nullptr);

    ID3D11Texture2D* oldStand = nullptr;
    ID3D11DeviceContext* oldContext = nullptr;

    if (c != nullptr)
    {
        oldStand = c->stand;
        oldContext = c->context;
        *c = Chain {};
        c->self = chain;
        c->context = context;
        c->stand = stand;
        c->asked = asked;
        c->tearing = tearing;
    }

    ReleaseSRWLockExclusive(&g_lock);

    if (oldStand) oldStand->Release();
    if (oldContext) oldContext->Release();

    if (c == nullptr)
    {
        stand->Release();
        context->Release();
        return;
    }

    Keeper* keeper = new Keeper();
    keeper->chain = chain;

    if (FAILED(chain->SetPrivateDataInterface(kKeeper, keeper)))
        Log("flip: the swap chain takes no private data; its stand-in is kept until the next one is made");

    keeper->Release();
    InterlockedExchange(&g_state, kAtWork);
}

// Why a swap chain is left as the game asked for it, or null.
const char* Refusal(IUnknown* device, DXGI_SWAP_EFFECT effect, UINT samples, bool stereo, UINT width, UINT height)
{
    ID3D11Device* d3d = nullptr;

    // A few pixels on a window of its own: made by a mod to find DXGI's methods, never shown.
    if ((width != 0 && width < 64) || (height != 0 && height < 64))
        return "it is a helper's, too small to be a window's picture";

    if (device == nullptr || FAILED(device->QueryInterface(__uuidof(ID3D11Device), (void**) &d3d)))
        return "not a Direct3D 11 device";

    d3d->Release();

    if (effect != DXGI_SWAP_EFFECT_DISCARD && effect != DXGI_SWAP_EFFECT_SEQUENTIAL)
        return "it is flip-model already";

    if (samples > 1)
        return "the game's own anti-aliasing is on (a flip-model window cannot be multisampled); set it to off in VaM's settings";

    if (stereo)
        return "it is a stereo swap chain";

    return nullptr;
}

HRESULT STDMETHODCALLTYPE OnCreateHwnd(IDXGIFactory2* self, IUnknown* device, HWND window, const DXGI_SWAP_CHAIN_DESC1* desc,
                                       const DXGI_SWAP_CHAIN_FULLSCREEN_DESC* full, IDXGIOutput* output, IDXGISwapChain1** out)
{
    void* caller = _ReturnAddress();

    if (desc == nullptr || out == nullptr)
        return g_createHwnd(self, device, window, desc, full, output, out);

    Note(1, "CreateSwapChainForHwnd", caller, "%ux%u format %d, %u buffer(s), swap effect %d, %u sample(s), flags 0x%X", desc->Width, desc->Height,
         (int) desc->Format, desc->BufferCount, (int) desc->SwapEffect, desc->SampleDesc.Count, desc->Flags);

    if (const char* why = Refusal(device, desc->SwapEffect, desc->SampleDesc.Count, desc->Stereo != FALSE, desc->Width, desc->Height))
    {
        Log("flip: swap chain left alone: %s", why);

        if (g_state != kAtWork)
            InterlockedExchange(&g_state, desc->SampleDesc.Count > 1 ? kLeftAlone : (LONG) g_state);

        return g_createHwnd(self, device, window, desc, full, output, out);
    }

    DXGI_SWAP_CHAIN_DESC1 d = *desc;
    d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    d.BufferCount = d.BufferCount < kBuffers ? kBuffers : d.BufferCount;
    d.Format = Plain(d.Format);
    d.Flags |= g_tearing ? DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : 0;

    HRESULT hr = g_createHwnd(self, device, window, &d, full, output, out);

    if (FAILED(hr))
    {
        Log("flip: the flip-model swap chain was refused (0x%08X); made as the game asked instead", (unsigned) hr);
        InterlockedExchange(&g_state, kFailed);
        return g_createHwnd(self, device, window, desc, full, output, out);
    }

    Log("flip: swap chain made flip-model: format %d, %u buffers, flags 0x%X%s", (int) d.Format, d.BufferCount, d.Flags,
        g_tearing ? " (tearing allowed)" : "");
    Adopt(*out, desc->Format, g_tearing);
    return hr;
}

HRESULT STDMETHODCALLTYPE OnCreate(IDXGIFactory* self, IUnknown* device, DXGI_SWAP_CHAIN_DESC* desc, IDXGISwapChain** out)
{
    void* caller = _ReturnAddress();

    if (desc == nullptr || out == nullptr)
        return g_create(self, device, desc, out);

    Note(2, "CreateSwapChain", caller, "%ux%u format %d, %u buffer(s), swap effect %d, %u sample(s), flags 0x%X", desc->BufferDesc.Width,
         desc->BufferDesc.Height, (int) desc->BufferDesc.Format, desc->BufferCount, (int) desc->SwapEffect, desc->SampleDesc.Count, desc->Flags);

    if (const char* why = Refusal(device, desc->SwapEffect, desc->SampleDesc.Count, false, desc->BufferDesc.Width, desc->BufferDesc.Height))
    {
        Log("flip: swap chain left alone: %s", why);

        if (g_state != kAtWork)
            InterlockedExchange(&g_state, desc->SampleDesc.Count > 1 ? kLeftAlone : (LONG) g_state);

        return g_create(self, device, desc, out);
    }

    DXGI_SWAP_CHAIN_DESC d = *desc;
    d.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    d.BufferCount = d.BufferCount < kBuffers ? kBuffers : d.BufferCount;
    d.BufferDesc.Format = Plain(d.BufferDesc.Format);
    d.Flags |= g_tearing ? DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : 0;

    HRESULT hr = g_create(self, device, &d, out);

    if (FAILED(hr))
    {
        Log("flip: the flip-model swap chain was refused (0x%08X); made as the game asked instead", (unsigned) hr);
        InterlockedExchange(&g_state, kFailed);
        return g_create(self, device, desc, out);
    }

    Log("flip: swap chain made flip-model: format %d, %u buffers, flags 0x%X%s", (int) d.BufferDesc.Format, d.BufferCount, d.Flags,
        g_tearing ? " (tearing allowed)" : "");
    Adopt(*out, desc->BufferDesc.Format, g_tearing);
    return hr;
}

void Inside(void* self, DWORD thread)
{
    AcquireSRWLockExclusive(&g_lock);

    if (Chain* c = Find(self))
        c->inside = thread;

    ReleaseSRWLockExclusive(&g_lock);
}

// What the game drew, onto the real back buffer. Same size and the same family of formats (the
// stand-in is the sRGB twin of the back buffer), so it is a plain copy.
void Deliver(IDXGISwapChain* self, Table* t, Chain& held)
{
    ID3D11Texture2D* real = nullptr;

    if (FAILED(t->getBuffer(self, 0, __uuidof(ID3D11Texture2D), (void**) &real)) || real == nullptr)
        return;

    D3D11_TEXTURE2D_DESC a {}, b {};
    real->GetDesc(&a);
    held.stand->GetDesc(&b);

    if (a.Width == b.Width && a.Height == b.Height && a.Format == Plain(b.Format) && a.SampleDesc.Count == 1)
    {
        held.context->CopyResource(real, held.stand);
    }
    else if (!held.mismatch)
    {
        Log("flip: the real back buffer is %ux%u format %d, the stand-in %ux%u format %d -- not copied", a.Width, a.Height, (int) a.Format,
            b.Width, b.Height, (int) b.Format);
        AcquireSRWLockExclusive(&g_lock);

        if (Chain* c = Find(self))
            c->mismatch = true;

        ReleaseSRWLockExclusive(&g_lock);
    }

    real->Release();
}

void SetLatency(IDXGISwapChain* self, bool raise)
{
    IDXGIDevice1* device = nullptr;

    if (FAILED(self->GetDevice(__uuidof(IDXGIDevice1), (void**) &device)))
        return;

    if (raise)
    {
        UINT now = 0;

        // What the game set is kept the first time only: after that it is our own number.
        if (!g_pace.haveLatency && SUCCEEDED(device->GetMaximumFrameLatency(&now)))
        {
            g_pace.latencyWas = now;
            g_pace.haveLatency = true;
        }

        device->SetMaximumFrameLatency(kBuffers - 1);
    }
    else if (g_pace.haveLatency)
    {
        device->SetMaximumFrameLatency(g_pace.latencyWas);
        g_pace.haveLatency = false;
    }

    device->Release();
}

// Pacing starts (or starts over after a resize): how often the screen refreshes, and a queue deep
// enough that Present does not wait while a burst is put into it.
void PaceBegin(IDXGISwapChain* self)
{
    Pace& p = g_pace;
    IDXGIOutput* output = nullptr;
    p.hz = 60.0;

    if (SUCCEEDED(self->GetContainingOutput(&output)) && output != nullptr)
    {
        DXGI_OUTPUT_DESC od {};
        DEVMODEW mode {};
        mode.dmSize = sizeof(mode);

        if (SUCCEEDED(output->GetDesc(&od)) && EnumDisplaySettingsW(od.DeviceName, ENUM_CURRENT_SETTINGS, &mode) && mode.dmDisplayFrequency > 1)
            p.hz = (double) mode.dmDisplayFrequency;

        output->Release();
    }

    SetLatency(self, true);
    p.at = p.count = 0;
    p.carry = 0.0;
    p.begun = true;
    InterlockedExchange(&p.refresh, (LONG) (p.hz * 1000.0));
    Log("flip: pacing on a %.0f Hz screen, up to %u frames queued", p.hz, kBuffers - 1);
}

void PaceEnd(IDXGISwapChain* self)
{
    SetLatency(self, false);
    g_pace.begun = false;
    InterlockedExchange(&g_pace.rate, 0);
    InterlockedExchange(&g_pace.each, 0);
    InterlockedExchange(&g_pace.queue, 0);
    Log("flip: pacing off");
}

// For how many refreshes the frame now presented is to be shown; 0 = not at all (more frames are
// arriving than the screen can show, or the queue has run too deep).
UINT PaceNext(IDXGISwapChain* self)
{
    Pace& p = g_pace;

    if (!p.begun)
        PaceBegin(self);

    LARGE_INTEGER now {}, freq {};
    QueryPerformanceCounter(&now);
    QueryPerformanceFrequency(&freq);

    p.stamps[p.at] = now.QuadPart;
    p.at = (p.at + 1) % kStamps;

    if (p.count < kStamps)
        p.count++;

    if (p.count < 16)
        return 1;

    // The rate frames arrive at, over the last presents; from it, the refreshes one frame gets.
    const LONGLONG oldest = p.stamps[(p.at - p.count + kStamps) % kStamps];
    const double span = (double) (now.QuadPart - oldest) / (double) freq.QuadPart;
    const double rate = span > 0.0 ? (double) (p.count - 1) / span : p.hz;
    double each = p.hz / rate;

    if (each > 4.0)
        each = 4.0;

    p.carry += each;
    UINT n = (UINT) p.carry;
    p.carry -= (double) n;

    // How many frames are waiting, from the system's own count of what it has shown. A whole
    // rendered frame's worth (and one) is what the rhythm needs; beyond that the queue is only delay.
    DXGI_FRAME_STATISTICS shown {};
    UINT last = 0;
    int waiting = -1;

    if (SUCCEEDED(self->GetFrameStatistics(&shown)) && SUCCEEDED(self->GetLastPresentCount(&last)))
        waiting = (int) (last - shown.PresentCount);

    const int most = (int) p.multiplier + 1 < (int) kBuffers - 1 ? (int) p.multiplier + 1 : (int) kBuffers - 2;

    if (waiting > most)
        n = 0;
    else if (waiting == most && n > 1)
        n -= 1;

    if (n > 4)
        n = 4;

    if (n == 0)
        InterlockedIncrement(&p.dropped);

    InterlockedExchange(&p.rate, (LONG) (rate * 1000.0));
    InterlockedExchange(&p.each, (LONG) (each * 1000.0));
    InterlockedExchange(&p.queue, waiting < 0 ? -1 : (LONG) waiting * 1000);
    return n;
}

template <typename Call> HRESULT Presenting(IDXGISwapChain* self, Table* t, UINT sync, UINT flags, Call call)
{
    Chain held {};

    if ((flags & DXGI_PRESENT_TEST) != 0 || !Hold(self, &held))
        return call(sync, flags);

    UINT paced = sync;
    const bool pacing = g_pace.on != 0;

    if (pacing)
    {
        paced = PaceNext(self);

        if (paced == 0)
        {
            Let(held);
            return S_OK;
        }
    }
    else if (g_pace.begun)
    {
        PaceEnd(self);
    }

    Deliver(self, t, held);

    // Without vertical sync a flip-model window shows a frame the moment it is presented only if
    // tearing is asked for with each present (which is also what lets a variable-refresh screen
    // follow the game); in exclusive fullscreen the flag is refused, and left out from then on.
    // A paced frame waits its turn in the queue instead.
    const UINT plain = pacing ? flags & ~(UINT) DXGI_PRESENT_ALLOW_TEARING : flags;
    const UINT with = !pacing && held.tearing && !held.tearRefused && sync == 0 ? flags | DXGI_PRESENT_ALLOW_TEARING : plain;

    Inside(self, GetCurrentThreadId());
    HRESULT hr = call(paced, with);

    if (hr == DXGI_ERROR_INVALID_CALL && with != plain)
    {
        AcquireSRWLockExclusive(&g_lock);

        if (Chain* c = Find(self))
            c->tearRefused = true;

        ReleaseSRWLockExclusive(&g_lock);
        hr = call(paced, plain);
    }

    Inside(self, 0);
    Let(held);
    InterlockedIncrement(&g_presents);
    return hr;
}

HRESULT STDMETHODCALLTYPE OnPresent(IDXGISwapChain* self, UINT sync, UINT flags)
{
    Table* t = TableOf(self);
    Note(3, "Present", _ReturnAddress(), "sync %u, flags 0x%X", sync, flags);
    return Presenting(self, t, sync, flags, [&](UINT s, UINT f) { return t->present(self, s, f); });
}

HRESULT STDMETHODCALLTYPE OnPresent1(IDXGISwapChain1* self, UINT sync, UINT flags, const DXGI_PRESENT_PARAMETERS* params)
{
    Table* t = TableOf(self);
    Note(4, "Present1", _ReturnAddress(), "sync %u, flags 0x%X", sync, flags);
    return Presenting(self, t, sync, flags, [&](UINT s, UINT f) { return t->present1(self, s, f, params); });
}

HRESULT STDMETHODCALLTYPE OnGetBuffer(IDXGISwapChain* self, UINT index, REFIID riid, void** out)
{
    Table* t = TableOf(self);
    Chain held {};

    // Whoever asks from inside the real Present -- an overlay drawing on the finished frame -- is
    // below this layer and gets the real back buffer.
    if (index != 0 || out == nullptr || !Hold(self, &held))
        return t->getBuffer(self, index, riid, out);

    if (held.inside == GetCurrentThreadId())
    {
        Let(held);
        Note(5, "GetBuffer (inside Present, real buffer given)", _ReturnAddress());
        return t->getBuffer(self, index, riid, out);
    }

    Note(6, "GetBuffer", _ReturnAddress());
    const HRESULT hr = held.stand->QueryInterface(riid, out);
    Let(held);
    return hr;
}

HRESULT STDMETHODCALLTYPE OnResize(IDXGISwapChain* self, UINT count, UINT width, UINT height, DXGI_FORMAT format, UINT flags)
{
    Table* t = TableOf(self);
    Chain held {};

    if (!Hold(self, &held))
        return t->resize(self, count, width, height, format, flags);

    Note(7, "ResizeBuffers", _ReturnAddress(), "%ux%u format %d, %u buffer(s), flags 0x%X", width, height, (int) format, count, flags);
    Let(held);

    // The stand-in goes first (the game has let go of its own hold on it before asking), and comes
    // back in the new size. An 8-bit format is the game's wish for its back buffer; anything else
    // -- "as it is", or what a layer in between sets for itself -- leaves the game's format alone.
    const bool games = format == DXGI_FORMAT_R8G8B8A8_UNORM || format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB ||
                       format == DXGI_FORMAT_B8G8R8A8_UNORM || format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
    const DXGI_FORMAT asked = games ? format : held.asked;
    ID3D11Texture2D* old = nullptr;

    AcquireSRWLockExclusive(&g_lock);

    if (Chain* c = Find(self))
    {
        old = c->stand;
        c->stand = nullptr;
    }

    ReleaseSRWLockExclusive(&g_lock);

    if (old) old->Release();

    // The number of buffers stays what it was made with, whatever is asked for here.
    const HRESULT hr = t->resize(self, 0, width, height, Plain(format), held.tearing ? flags | DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING : flags);
    g_pace.begun = false; // the screen's mode may have changed with it

    if (FAILED(hr))
        Log("flip: ResizeBuffers %ux%u format %d failed (0x%08X)", width, height, (int) format, (unsigned) hr);

    ID3D11Texture2D* stand = MakeStand(self, asked);

    AcquireSRWLockExclusive(&g_lock);

    if (Chain* c = Find(self))
    {
        c->stand = stand;
        c->asked = asked;
        c->tearRefused = false;
        c->mismatch = false;
        stand = nullptr;
    }

    ReleaseSRWLockExclusive(&g_lock);

    if (stand) stand->Release();

    return hr;
}

// Points the factory's two swap chain makers here. Returns 1 when armed (or already so).
int Arm()
{
    if (g_armed)
        return 1;

    typedef HRESULT (WINAPI* MakeFactoryFn)(REFIID, void**);
    HMODULE dxgi = LoadLibraryW(L"dxgi.dll");
    const MakeFactoryFn make = dxgi != nullptr ? (MakeFactoryFn) GetProcAddress(dxgi, "CreateDXGIFactory1") : nullptr;
    IDXGIFactory1* factory = nullptr;

    if (make == nullptr || FAILED(make(__uuidof(IDXGIFactory1), (void**) &factory)) || factory == nullptr)
    {
        Log("flip: no DXGI factory -- not armed");
        return 0;
    }

    IDXGIFactory5* five = nullptr;
    BOOL allow = FALSE;

    if (SUCCEEDED(factory->QueryInterface(__uuidof(IDXGIFactory5), (void**) &five)))
    {
        g_tearing = SUCCEEDED(five->CheckFeatureSupport(DXGI_FEATURE_PRESENT_ALLOW_TEARING, &allow, sizeof(allow))) && allow != FALSE;
        five->Release();
    }

    IDXGIFactory2* two = nullptr;
    const bool hasTwo = SUCCEEDED(factory->QueryInterface(__uuidof(IDXGIFactory2), (void**) &two)) && (void*) two == (void*) factory;

    if (two) two->Release();

    void** methods = *(void***) factory;
    const bool ok = Swap(&methods[10], (void*) &OnCreate, (void**) &g_create) &&
                    (!hasTwo || Swap(&methods[15], (void*) &OnCreateHwnd, (void**) &g_createHwnd));
    factory->Release();

    if (!ok)
    {
        Log("flip: the factory's method table could not be changed -- not armed");
        return 0;
    }

    g_armed = true;
    InterlockedExchange(&g_state, kArmed);
    Log("flip: armed before the game's swap chain%s", g_tearing ? "; the screen takes tearing presents" : "");
    return 1;
}
} // namespace vwsflip

// From the preloader patcher, before Unity has a swap chain.
VWS_EXPORT int vws_flip_arm()
{
    return vwsflip::Arm();
}

// Pacing on or off, from the plugin: every present is given the refreshes that spread the frames
// evenly (see the top of vws_flip.h). `multiplier`: the frames shown for each rendered one.
VWS_EXPORT void vws_flip_pace(int32_t on, uint32_t multiplier)
{
    InterlockedExchange(&vwsflip::g_pace.multiplier, (LONG) (multiplier < 1 ? 1 : multiplier > 4 ? 4 : multiplier));
    InterlockedExchange(&vwsflip::g_pace.on, on != 0 ? 1 : 0);
}

// The frames arriving a second, the screen's refreshes a second, the refreshes one frame is given,
// the frames waiting in the queue (-1: the system does not say), and the frames left out so far.
VWS_EXPORT void vws_flip_pace_status(float* rate, float* refresh, float* each, float* queue, uint32_t* dropped)
{
    if (rate != nullptr)
        *rate = (float) vwsflip::g_pace.rate / 1000.0f;

    if (refresh != nullptr)
        *refresh = (float) vwsflip::g_pace.refresh / 1000.0f;

    if (each != nullptr)
        *each = (float) vwsflip::g_pace.each / 1000.0f;

    if (queue != nullptr)
        *queue = vwsflip::g_pace.queue < 0 ? -1.0f : (float) vwsflip::g_pace.queue / 1000.0f;

    if (dropped != nullptr)
        *dropped = (uint32_t) vwsflip::g_pace.dropped;
}

// For the check suite: does what Present does before presenting -- the stand-in copied onto the
// real back buffer -- and hands that back buffer out (to be released by the caller).
VWS_EXPORT int vws_flip_deliver(void* chain, void** real)
{
    vwsflip::Chain held {};
    vwsflip::Table* t = chain != nullptr ? vwsflip::TableOf(chain) : nullptr;

    if (t == nullptr || real == nullptr || !vwsflip::Hold(chain, &held))
        return 0;

    vwsflip::Deliver((IDXGISwapChain*) chain, t, held);
    vwsflip::Let(held);
    return SUCCEEDED(t->getBuffer((IDXGISwapChain*) chain, 0, __uuidof(ID3D11Texture2D), real)) ? 1 : 0;
}

// `state`: 0 not asked for, 1 armed and no swap chain taken (if it stays so, the game's swap chain
// was made before the arming), 2 at work, 3 left alone because the game's anti-aliasing is on,
// 4 failed (see the log). The stand-in's size, the presents so far, whether tearing is allowed.
VWS_EXPORT void vws_flip_status(int32_t* state, uint32_t* width, uint32_t* height, uint32_t* presents, uint32_t* tearing)
{
    if (state != nullptr)
        *state = (int32_t) vwsflip::g_state;

    if (width != nullptr)
        *width = (uint32_t) vwsflip::g_width;

    if (height != nullptr)
        *height = (uint32_t) vwsflip::g_height;

    if (presents != nullptr)
        *presents = (uint32_t) vwsflip::g_presents;

    if (tearing != nullptr)
        *tearing = vwsflip::g_tearing ? 1u : 0u;
}
