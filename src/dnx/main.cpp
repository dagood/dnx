// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

#include "stdafx.h"
#include "pal.h"
#include "utils.h"

int CallApplicationProcessMain(size_t argc, dnx::char_t* argv[], dnx::trace_writer& trace_writer);
void FreeExpandedCommandLineArguments(size_t argc, dnx::char_t** ppszArgv);
bool ExpandCommandLineArguments(size_t argc, dnx::char_t** ppszArgv, size_t& expanded_argc, dnx::char_t**& ppszExpandedArgv);

#if defined(ARM)
int wmain(int argc, wchar_t* argv[])
#elif defined(PLATFORM_UNIX)
int main(int argc, char* argv[])
#else
extern "C" int __stdcall DnxMain(int argc, wchar_t* argv[])
#endif
{
    // Check for the debug flag before doing anything else
    for (int i = 1; i < argc; ++i)
    {
        auto arg_count = dnx::utils::get_bootstrapper_option_arg_count(argv[i]);
        // not a bootstrapper option
        if (arg_count == -1)
        {
            break;
        }

        if (arg_count > 0)
        {
            //skip path argument
            i += arg_count;
            continue;
        }

        if (dnx::utils::strings_equal_ignore_case(argv[i], _X("--bootstrapper-debug"))
#if !defined(CORECLR_WIN) && !defined(CORECLR_LINUX) && !defined(CORECLR_DARWIN)
            || dnx::utils::strings_equal_ignore_case(argv[i], _X("--debug"))
#endif
            )
        {
            WaitForDebuggerToAttach();
            break;
        }
    }

    size_t nExpandedArgc = 0;
    dnx::char_t** ppszExpandedArgv = nullptr;
    auto expanded = ExpandCommandLineArguments(argc - 1, &(argv[1]), nExpandedArgc, ppszExpandedArgv);

    auto trace_writer = dnx::trace_writer{ IsTracingEnabled() };
    if (!expanded)
    {
        return CallApplicationProcessMain(argc - 1, &argv[1], trace_writer);
    }

    auto exitCode = CallApplicationProcessMain(nExpandedArgc, ppszExpandedArgv, trace_writer);
    FreeExpandedCommandLineArguments(nExpandedArgc, ppszExpandedArgv);
    return exitCode;
}
