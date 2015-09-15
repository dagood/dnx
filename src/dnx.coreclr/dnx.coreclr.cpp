// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

#include "stdafx.h"

#include "dnx.coreclr.h"
#include "tpa.h"
#include "utils.h"
#include "trace_writer.h"
#include "app_main.h"
#include <thread>

typedef int (STDMETHODCALLTYPE *HostMain)(const int argc, const wchar_t** argv, bootstrapper_context ctx);

std::wstring GetModuleDirectory(HMODULE module)
{
    wchar_t buffer[MAX_PATH];
    GetModuleFileName(module, buffer, MAX_PATH);
    return dnx::utils::remove_file_from_path(buffer);
}

// Generate a list of trusted platform assemblies.
bool GetTrustedPlatformAssembliesList(const std::wstring& runtime_directory, bool bNative, std::wstring& tpa_paths)
{
    // Build the list of the tpa assemblies
    auto tpas = CreateTpaBase(bNative);

    // Scan the directory to see if all the files in TPA list exist
    for (auto assembly_name : tpas)
    {
        if (!dnx::utils::file_exists(dnx::utils::path_combine(runtime_directory, assembly_name)))
        {
            return false;
        }
    }

    for (auto assembly_name : tpas)
    {
        tpa_paths.append(dnx::utils::path_combine(runtime_directory, assembly_name)).append(L";");
    }

    return true;
}

bool GetTrustedPlatformAssembliesList(const std::wstring& runtime_directory, std::wstring& trusted_platform_assemblies)
{
    // Try native images first
    if (!GetTrustedPlatformAssembliesList(runtime_directory, true, trusted_platform_assemblies))
    {
        if (!GetTrustedPlatformAssembliesList(runtime_directory, false, trusted_platform_assemblies))
        {
             return false;
        }
    }

    return true;
}

bool PinModule(HMODULE module, dnx::trace_writer& trace_writer)
{
    wchar_t module_path_buffer[MAX_PATH];
    GetModuleFileName(module, module_path_buffer, MAX_PATH);

    HMODULE ignoreModule;
    // Pin the module - CoreCLR.dll does not support being unloaded.
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN, module_path_buffer, &ignoreModule))
    {
        trace_writer.write(L"Failed to pin coreclr.dll", false);
        return false;
    }

    return true;
}

HMODULE LoadCoreClr(const std::wstring& runtime_directory, dnx::trace_writer& trace_writer)
{
    HMODULE coreclr_module = LoadLibraryExW(dnx::utils::path_combine(runtime_directory, L"coreclr.dll").c_str(), NULL, 0);

    if (coreclr_module)
    {
        if (PinModule(coreclr_module, trace_writer))
        {
            return coreclr_module;
        }

        FreeLibrary(coreclr_module);
    }

    return nullptr;
}

/*
    Win2KDisable : DisallowWin32kSystemCalls
    SET DNX_WIN32K_DISABLE=1
*/
void Win32KDisable(dnx::trace_writer& trace_writer)
{
    wchar_t buff[2] = { 0 , 0 };

    if (GetEnvironmentVariable(L"DNX_WIN32K_DISABLE", buff, 2) != 1 || buff[0] != L'1')
    {
        return;
    }

    auto process_threads_module = LoadLibraryExW(L"api-ms-win-core-processthreads-l1-1-1.dll", NULL, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    if (process_threads_module)
    {
        auto SetProcessMitigationPolicy_function = (FnSetProcessMitigationPolicy)GetProcAddress(process_threads_module, "SetProcessMitigationPolicy");
        if (SetProcessMitigationPolicy_function)
        {
            PROCESS_MITIGATION_SYSTEM_CALL_DISABLE_POLICY system_call_disable_policy = {};
            system_call_disable_policy.DisallowWin32kSystemCalls = 1;

            if (SetProcessMitigationPolicy_function(ProcessSystemCallDisablePolicy,
                &system_call_disable_policy, sizeof(system_call_disable_policy)))
            {
                trace_writer.write(L"DNX_WIN32K_DISABLE successful", false);
            }
        }
    }

    FreeLibrary(process_threads_module);
}

HRESULT GetClrRuntimeHost(HMODULE coreclr_module, ICLRRuntimeHost2** ppClrRuntimeHost, dnx::trace_writer& trace_writer)
{
    auto GetCLRRuntimeHost_function = (FnGetCLRRuntimeHost)GetProcAddress(coreclr_module, "GetCLRRuntimeHost");
    if (!GetCLRRuntimeHost_function)
    {
        trace_writer.write(L"Failed to find export GetCLRRuntimeHost", false);
        return E_FAIL;
    }

    return GetCLRRuntimeHost_function(IID_ICLRRuntimeHost2, (IUnknown**)ppClrRuntimeHost);
}

HRESULT StartClrHost(ICLRRuntimeHost2* pCLRRuntimeHost, dnx::trace_writer& trace_writer)
{
    STARTUP_FLAGS startup_flags = (STARTUP_FLAGS)(
        STARTUP_FLAGS::STARTUP_LOADER_OPTIMIZATION_SINGLE_DOMAIN |
        STARTUP_FLAGS::STARTUP_SINGLE_APPDOMAIN
        // STARTUP_SERVER_GC flag is not supported by CoreCLR for ARM
#ifndef ARM
        | STARTUP_FLAGS::STARTUP_SERVER_GC
#endif
        );

    pCLRRuntimeHost->SetStartupFlags(startup_flags);

    // Authenticate with either CORECLR_HOST_AUTHENTICATION_KEY or CORECLR_HOST_AUTHENTICATION_KEY_NONGEN
    HRESULT hr = pCLRRuntimeHost->Authenticate(CORECLR_HOST_AUTHENTICATION_KEY);
    if (FAILED(hr))
    {
        trace_writer.write(L"Failed to Authenticate()", false);
        return hr;
    }

    return pCLRRuntimeHost->Start();
}

HRESULT StopClrHost(ICLRRuntimeHost2* pCLRRuntimeHost)
{
    return pCLRRuntimeHost->Stop();
}

HRESULT ExecuteMain(ICLRRuntimeHost2* pCLRRuntimeHost, PCALL_APPLICATION_MAIN_DATA data, dnx::trace_writer& trace_writer)
{
    const wchar_t* property_keys[] =
    {
        // Allowed property names:
        // APPBASE
        // - The base path of the application from which the exe and other assemblies will be loaded
        L"APPBASE",
        //
        // TRUSTED_PLATFORM_ASSEMBLIES
        // - The list of complete paths to each of the fully trusted assemblies
        L"TRUSTED_PLATFORM_ASSEMBLIES",
        //
        // APP_PATHS
        // - The list of paths which will be probed by the assembly loader
        L"APP_PATHS",
        //
        // APP_NI_PATHS
        // - The list of additional paths that the assembly loader will probe for ngen images
        //
        // NATIVE_DLL_SEARCH_DIRECTORIES
        // - The list of paths that will be probed for native DLLs called by PInvoke
        //
        L"AppDomainCompatSwitch",
    };

    std::wstring trusted_platform_assemblies;
    // Came up with 8192 empirically - the string we build is about 4000 characters on my machine but it contains
    // paths to the user profile folder so it can be bigger.
    trusted_platform_assemblies.reserve(8192);

    if (!GetTrustedPlatformAssembliesList(data->runtimeDirectory, trusted_platform_assemblies))
    {
        trace_writer.write(L"Failed to find TPA files in the coreclr directory", false);
        return E_FAIL;
    }

    // Add the assembly containing the app domain manager to the trusted list
    trusted_platform_assemblies.append(dnx::utils::path_combine(data->runtimeDirectory, L"Microsoft.Dnx.Host.CoreClr.dll"));

    const wchar_t* property_values[] = {
        // APPBASE
        data->applicationBase,
        // TRUSTED_PLATFORM_ASSEMBLIES
        trusted_platform_assemblies.c_str(),
        // APP_PATHS
        data->runtimeDirectory,
        // Use the latest behavior when TFM not specified
        L"UseLatestBehaviorWhenTFMNotSpecified",
    };

    DWORD domainId;

    HRESULT hr = pCLRRuntimeHost->CreateAppDomainWithManager(
        L"Microsoft.Dnx.Host.CoreClr",
        APPDOMAIN_ENABLE_PLATFORM_SPECIFIC_APPS | APPDOMAIN_ENABLE_PINVOKE_AND_CLASSIC_COMINTEROP | APPDOMAIN_DISABLE_TRANSPARENCY_ENFORCEMENT,
        NULL,
        NULL,
        sizeof(property_keys) / sizeof(wchar_t*),
        property_keys,
        property_values,
        &domainId);

    if (FAILED(hr))
    {
        trace_writer.write(L"Failed to create app domain", false);
        trace_writer.write(std::wstring(L"TPA: ").append(trusted_platform_assemblies), false);
        trace_writer.write(std::wstring(L"AppPaths: ").append(data->runtimeDirectory), false);
        return hr;
    }

    HostMain main_function;
    // looks like the Version in the assembly is mandatory but the value does not matter
    hr = pCLRRuntimeHost->CreateDelegate(domainId, L"Microsoft.Dnx.Host.CoreClr, Version=0.0.0.0", L"DomainManager", L"Execute", (INT_PTR*)&main_function);
    if (FAILED(hr))
    {
        trace_writer.write(L"Failed to create main delegate", false);
        return hr;
    }

    auto windows_version = dnx::utils::get_windows_version();
    if (!windows_version)
    {
        trace_writer.write(L"Unexpected windows version", false);
        return -1;
    }

    bootstrapper_context ctx;
    ctx.operating_system = L"Windows";
    ctx.os_version = windows_version;
    ctx.processor_count = std::thread::hardware_concurrency();
    ctx.runtime_directory = data->runtimeDirectory;
    ctx.application_base = data->applicationBase;
#if defined(AMD64)
    ctx.architecture = L"x64";
#elif defined(ARM)
    ctx.architecture = L"arm";
#else
    ctx.architecture = L"x86";
#endif
    // for coreclr we always want to handle exceptions because
    // they cannot be marshalled from managed to native code
    ctx.handle_exceptions = true;


    // Call main
    data->exitcode = main_function(data->argc, data->argv, ctx);

    pCLRRuntimeHost->UnloadAppDomain(domainId, true);

    return S_OK;
}

bool IsTracingEnabled()
{
    wchar_t buff[2];
    return GetEnvironmentVariable(L"DNX_TRACE", buff, 2) == 1 && buff[0] == L'1';
}

extern "C" HRESULT __stdcall CallApplicationMain(PCALL_APPLICATION_MAIN_DATA data)
{
    auto trace_writer = dnx::trace_writer{ IsTracingEnabled() };

    SetEnvironmentVariable(L"DNX_FRAMEWORK", L"dnxcore50");

    Win32KDisable(trace_writer);

    auto coreclr_module = LoadCoreClr(data->runtimeDirectory, trace_writer);
    if (!coreclr_module)
    {
        trace_writer.write(L"Failed to locate or load coreclr.dll", false);
        return E_FAIL;
    }

    ICLRRuntimeHost2* pCLRRuntimeHost = nullptr;

    HRESULT hr = GetClrRuntimeHost(coreclr_module, &pCLRRuntimeHost, trace_writer);
    if (FAILED(hr))
    {
        trace_writer.write(L"Failed to get IID_ICLRRuntimeHost2", false);
        return hr;
    }

    hr = StartClrHost(pCLRRuntimeHost, trace_writer);
    if (FAILED(hr))
    {
        trace_writer.write(L"Failed to start CLR host", false);
        return hr;
    }

    hr = ExecuteMain(pCLRRuntimeHost, data, trace_writer);
    if (FAILED(hr))
    {
        trace_writer.write(L"Failed to execute Main", false);
        return hr;
    }

    hr = StopClrHost(pCLRRuntimeHost);
    if (FAILED(hr))
    {
        trace_writer.write(L"Failed to stop CLR host", false);
        return hr;
    }

    return S_OK;
}