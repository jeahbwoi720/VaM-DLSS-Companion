// Runs a managed executable on VaM's own embedded Mono (Unity 2018.1's legacy runtime), with no
// Unity around it. It exists to answer questions that only that runtime can: does HarmonyX hook
// VamDlssNr's real methods there, does the transpiled IL compile, do the P/Invokes marshal.
//
//   monohost.exe <VaM dir> <assembly search path, ';' separated> <test.exe> [args...]
#include <windows.h>
#include <stdio.h>
#include <string.h>

typedef void* (*jit_init_version_fn)(const char*, const char*);
typedef void (*set_dirs_fn)(const char*, const char*);
typedef void (*set_assemblies_path_fn)(const char*);
typedef void* (*domain_assembly_open_fn)(void*, const char*);
typedef int (*jit_exec_fn)(void*, void*, int, char**);
typedef void (*add_internal_call_fn)(const char*, const void*);
typedef void (*config_parse_fn)(const char*);

// The engine calls VamDlssNr's static initialisers make. Compiling one of its methods runs them,
// and outside Unity there is no engine to answer; none of what they return is used here.
static int __cdecl Shader_PropertyToID(void* name)
{
    static int next = 1000;
    (void) name;
    return next++;
}

static void __cdecl Noop(void)
{
}

static const char* const kNoopCalls[] = {
    "UnityEngine.Rendering.CommandBuffer::InitBuffer",
    "UnityEngine.Rendering.CommandBuffer::ReleaseBuffer",
    "UnityEngine.Rendering.CommandBuffer::set_name",
    "UnityEngine.Rendering.CommandBuffer::INTERNAL_CALL_SetGlobalVector",
};

int main(int argc, char** argv)
{
    char path[1024];
    HMODULE mono;
    void* domain;
    void* assembly;

    if (argc < 4)
    {
        printf("usage: monohost <VaM dir> <assembly path> <test.exe> [args]\n");
        return 2;
    }

    snprintf(path, sizeof(path), "%s\\Mono\\EmbedRuntime\\mono.dll", argv[1]);
    mono = LoadLibraryA(path);

    if (mono == NULL)
    {
        printf("could not load %s (error %lu)\n", path, GetLastError());
        return 2;
    }

#define SYM(type, name) type name = (type) GetProcAddress(mono, #name)
    {
        SYM(set_dirs_fn, mono_set_dirs);
        SYM(set_assemblies_path_fn, mono_set_assemblies_path);
        SYM(jit_init_version_fn, mono_jit_init_version);
        SYM(domain_assembly_open_fn, mono_domain_assembly_open);
        SYM(jit_exec_fn, mono_jit_exec);
        SYM(add_internal_call_fn, mono_add_internal_call);
        SYM(config_parse_fn, mono_config_parse);
        char etc[1024];

        if (!mono_set_dirs || !mono_set_assemblies_path || !mono_jit_init_version || !mono_domain_assembly_open ||
            !mono_jit_exec || !mono_add_internal_call)
        {
            printf("mono.dll is missing an embedding export\n");
            return 2;
        }

        snprintf(path, sizeof(path), "%s\\VaM_Data\\Managed", argv[1]);
        snprintf(etc, sizeof(etc), "%s\\Mono\\etc", argv[1]);
        mono_set_dirs(path, etc);
        mono_set_assemblies_path(argv[2]);

        if (mono_config_parse)
            mono_config_parse(NULL);

        domain = mono_jit_init_version("vws-host", "v2.0.50727");

        if (domain == NULL)
        {
            printf("mono_jit_init_version failed\n");
            return 2;
        }

        mono_add_internal_call("UnityEngine.Shader::PropertyToID", (const void*) Shader_PropertyToID);

        {
            int i;

            for (i = 0; i < (int) (sizeof(kNoopCalls) / sizeof(kNoopCalls[0])); ++i)
                mono_add_internal_call(kNoopCalls[i], (const void*) Noop);
        }

        assembly = mono_domain_assembly_open(domain, argv[3]);

        if (assembly == NULL)
        {
            printf("could not open %s\n", argv[3]);
            return 2;
        }

        fflush(stdout);
        return mono_jit_exec(domain, assembly, argc - 3, argv + 3);
    }
}
