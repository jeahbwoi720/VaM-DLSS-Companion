// VaM DLSS - Model Resolution: log files of VaM DLSS's native half and of NVIDIA's DLSS, kept off
// the disk on request.
//
// VaM DLSS's native DLL writes ngx\vdn.log and ngx\vdn_fg.log, and NVIDIA's libraries write
// ngx\nvngx.log and ngx\nvngx_dlss_*.log beside them: a few hundred to a few thousand lines a
// session, with no setting anywhere that stops them. All five libraries (VamDlssNr.dll, _nvngx.dll
// and the three nvngx_dlss*.dll) write through WriteFile as they import it from kernel32. Here that
// import is led through Write below, in memory and for these libraries alone: a write to a file of
// one of those names is answered as done and not made, every other write goes on to Windows as it
// came. Nothing on disk is changed, and nothing at all is touched until a log is switched off.
//
// The libraries load one another (VamDlssNr.dll loads _nvngx.dll, which loads the features), and
// NGX opens its log as soon as it is started: so their LoadLibraryW and LoadLibraryExW imports are
// led through here as well, and whatever has just been loaded is given the same treatment before
// it can have written a line.

#pragma once

#include <windows.h>
#include <stdint.h>
#include <string.h>
#include <wchar.h>

namespace vwslogs
{

volatile LONG g_off = 0;      // bit 0: VaM DLSS's own files; bit 1: NVIDIA's
volatile LONG g_writes = 0;   // writes answered and not made
volatile LONG64 g_bytes = 0;  // ...and what they would have written
volatile LONG g_slots = 0;    // imports led through here so far

const LONG kOwnFiles = 1, kNgxFiles = 2;

// Whether the file at `path` is one of the logs that `off` has switched off: by its name alone,
// in whatever case.
inline bool Named(const wchar_t* path, size_t length, LONG off)
{
    size_t start = length;

    while (start > 0 && path[start - 1] != L'\\' && path[start - 1] != L'/')
        --start;

    const size_t n = length - start;
    wchar_t name[64];

    if (n == 0 || n >= 64)
        return false;

    for (size_t i = 0; i < n; ++i)
        name[i] = (wchar_t) towlower(path[start + i]);

    name[n] = 0;

    if ((off & kOwnFiles) != 0 && (wcscmp(name, L"vdn.log") == 0 || wcscmp(name, L"vdn_fg.log") == 0))
        return true;

    return (off & kNgxFiles) != 0 && n >= 9 && wcsncmp(name, L"nvngx", 5) == 0 && wcscmp(name + n - 4, L".log") == 0;
}

inline bool Kept(HANDLE file, LONG off)
{
    if (GetFileType(file) != FILE_TYPE_DISK)
        return false;

    wchar_t path[600];
    const DWORD n = GetFinalPathNameByHandleW(file, path, 600, FILE_NAME_OPENED | VOLUME_NAME_DOS);
    return n != 0 && n < 600 && Named(path, n, off);
}

inline BOOL WINAPI Write(HANDLE file, LPCVOID data, DWORD bytes, LPDWORD written, LPOVERLAPPED overlapped)
{
    const LONG off = g_off;

    if (off != 0 && overlapped == nullptr && Kept(file, off))
    {
        if (written != nullptr)
            *written = bytes;

        InterlockedIncrement(&g_writes);
        InterlockedExchangeAdd64(&g_bytes, (LONG64) bytes);
        return TRUE;
    }

    return WriteFile(file, data, bytes, written, overlapped);
}

inline void HookKnown();

inline HMODULE WINAPI LoadW(LPCWSTR name)
{
    const HMODULE module = LoadLibraryW(name);
    const DWORD error = GetLastError();

    if (module != nullptr && g_off != 0)
        HookKnown();

    SetLastError(error);
    return module;
}

inline HMODULE WINAPI LoadExW(LPCWSTR name, HANDLE file, DWORD flags)
{
    const HMODULE module = LoadLibraryExW(name, file, flags);
    const DWORD error = GetLastError();

    if (module != nullptr && g_off != 0)
        HookKnown();

    SetLastError(error);
    return module;
}

// Leads a module's imports of WriteFile, LoadLibraryW and LoadLibraryExW through the three above.
// Returns how many it changed: none the second time.
inline uint32_t Hook(HMODULE module)
{
    if (module == nullptr)
        return 0;

    uint8_t* base = (uint8_t*) module;
    const IMAGE_DOS_HEADER* dos = (const IMAGE_DOS_HEADER*) base;

    if (dos->e_magic != IMAGE_DOS_SIGNATURE)
        return 0;

    const IMAGE_NT_HEADERS* nt = (const IMAGE_NT_HEADERS*) (base + dos->e_lfanew);

    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.NumberOfRvaAndSizes <= IMAGE_DIRECTORY_ENTRY_IMPORT)
        return 0;

    const IMAGE_DATA_DIRECTORY& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];

    if (dir.VirtualAddress == 0)
        return 0;

    uint32_t changed = 0;

    for (const IMAGE_IMPORT_DESCRIPTOR* d = (const IMAGE_IMPORT_DESCRIPTOR*) (base + dir.VirtualAddress); d->Name != 0; ++d)
    {
        if (d->OriginalFirstThunk == 0 || d->FirstThunk == 0)
            continue;

        const IMAGE_THUNK_DATA* names = (const IMAGE_THUNK_DATA*) (base + d->OriginalFirstThunk);
        IMAGE_THUNK_DATA* slots = (IMAGE_THUNK_DATA*) (base + d->FirstThunk);

        for (; names->u1.AddressOfData != 0; ++names, ++slots)
        {
            if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal))
                continue;

            const char* name = ((const IMAGE_IMPORT_BY_NAME*) (base + names->u1.AddressOfData))->Name;
            void* mine = strcmp(name, "WriteFile") == 0 ? (void*) &Write
                : strcmp(name, "LoadLibraryW") == 0 ? (void*) &LoadW
                : strcmp(name, "LoadLibraryExW") == 0 ? (void*) &LoadExW : nullptr;

            if (mine == nullptr || (void*) slots->u1.Function == mine)
                continue;

            DWORD old = 0;

            if (!VirtualProtect(&slots->u1.Function, sizeof(void*), PAGE_READWRITE, &old))
                continue;

            slots->u1.Function = (ULONG_PTR) mine;
            VirtualProtect(&slots->u1.Function, sizeof(void*), old, &old);
            ++changed;
        }
    }

    if (changed != 0)
        InterlockedExchangeAdd(&g_slots, (LONG) changed);

    return changed;
}

// The five libraries that write those files, whichever of them are loaded now. (Held while they
// are gone through: NGX lets go of its features now and then, on another thread.)
inline void HookKnown()
{
    static const wchar_t* const kNames[] = { L"VamDlssNr.dll", L"_nvngx.dll", L"nvngx_dlss.dll", L"nvngx_dlssnr.dll", L"nvngx_dlssg.dll" };

    for (const wchar_t* name : kNames)
    {
        HMODULE module = nullptr;

        if (GetModuleHandleExW(0, name, &module) && module != nullptr)
        {
            Hook(module);
            FreeLibrary(module);
        }
    }
}

} // namespace vwslogs
