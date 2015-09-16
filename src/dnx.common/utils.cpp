// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

#include "stdafx.h"

#include "xplat.h"
#include <string>

// <codecvt> not supported in libstdc++ (gcc, Clang) but conversions from wstring are only
// meant to be used on Windows
#if defined(_WIN32)
#include <locale>
#include <codecvt>
#else
#include <strings.h>
#endif

namespace dnx
{
    namespace utils
    {
        std::string to_string(const std::string& s)
        {
            return s;
        }

        // std::string <--> std::wstring conversion functions are not general purpose and should be
        // used only to convert strings containing ASCII characters
        dnx::xstring_t to_xstring_t(const std::string& s)
        {
#if defined(_WIN32)
            return std::wstring_convert<std::codecvt_utf8_utf16<wchar_t>>{}.from_bytes(s);
#else
            return s;
#endif
        }

        // conversions from wstring are only meant to be use on Windows
#if defined(_WIN32)
        std::string to_string(const std::wstring& s)
        {
            return std::wstring_convert<std::codecvt_utf8_utf16<wchar_t>>{}.to_bytes(s);
        }

        dnx::xstring_t to_xstring_t(const std::wstring& s)
        {
            return s;
        }

        std::wstring to_wstring(const std::string& s)
        {
            return to_xstring_t(s);
        }
#endif

        bool strings_equal_ignore_case(const dnx::char_t* s1, const dnx::char_t* s2)
        {
#if defined(_WIN32)
            return _wcsicmp(s1, s2) == 0;
#else
            return strcasecmp(s1, s2) == 0;
#endif
        }


        bool ends_with_slash(const xstring_t& path)
        {
            if (path.length() > 0)
            {
                auto last = path.back();

                return last == _X('\\') || last == _X('/');
            }

            return false;
        }

        xstring_t path_combine(const xstring_t& path1, const xstring_t& path2)
        {
            if (path1.length() == 0)
            {
                return path2;
            }

            if (path2.length() == 0)
            {
                return path1;
            }

            xstring_t path{ path1 };

            if (ends_with_slash(path))
            {
                path.resize(path.length() - 1);
            }

            return path + PATH_SEPARATOR + (path2[0] == _X('\\') || path2[0] == _X('/') ? path2.substr(1) : path2);
        }

#if defined (_WIN32)
        bool file_exists(const xstring_t& path)
        {
            auto attributes = GetFileAttributes(path.c_str());

            return attributes != INVALID_FILE_ATTRIBUTES && ((attributes & FILE_ATTRIBUTE_DIRECTORY) == 0);
        }

        bool directory_exists(const xstring_t& path)
        {
            auto attributes = GetFileAttributes(path.c_str());

            return attributes != INVALID_FILE_ATTRIBUTES && ((attributes & FILE_ATTRIBUTE_DIRECTORY) != 0);
        }
#endif

        xstring_t remove_file_from_path(const xstring_t& path)
        {
            if (ends_with_slash(path))
            {
                return path;
            }

            auto last_separator = path.find_last_of(_X("/\\"));

            return last_separator != xstring_t::npos
                ? path.substr(0, last_separator)
                : path;
        }

        int get_bootstrapper_option_arg_count(const dnx::char_t* option_name)
        {
            if (strings_equal_ignore_case(option_name, _X("--appbase")) ||
                strings_equal_ignore_case(option_name, _X("--lib")) ||
                strings_equal_ignore_case(option_name, _X("--packages")) ||
                strings_equal_ignore_case(option_name, _X("--configuration")) ||
                strings_equal_ignore_case(option_name, _X("--framework")) ||
                strings_equal_ignore_case(option_name, _X("--port")) ||
                strings_equal_ignore_case(option_name, _X("--project")) ||
                strings_equal_ignore_case(option_name, _X("-p")))
            {
                return 1;
            }

            if (strings_equal_ignore_case(option_name, _X("--watch")) ||
                strings_equal_ignore_case(option_name, _X("--debug")) ||
                strings_equal_ignore_case(option_name, _X("--bootstrapper-debug")) ||
                strings_equal_ignore_case(option_name, _X("--help")) ||
                strings_equal_ignore_case(option_name, _X("-h")) ||
                strings_equal_ignore_case(option_name, _X("-?")) ||
                strings_equal_ignore_case(option_name, _X("--version")))
            {
                return 0;
            }

            // It isn't a bootstrapper option
            return -1;
        }
    }
}
