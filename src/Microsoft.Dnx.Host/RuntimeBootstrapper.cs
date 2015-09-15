// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Microsoft.Dnx.Runtime;
using Microsoft.Dnx.Runtime.Common.CommandLine;

namespace Microsoft.Dnx.Host
{
    public static class RuntimeBootstrapper
    {
        private static readonly char[] _libPathSeparator = new[] { ';' };

        public static int Execute(string[] args, FrameworkName targetFramework, BootstrapperContext context)
        {
            // If we're a console host then print exceptions to stderr
            var printExceptionsToStdError = Environment.GetEnvironmentVariable(EnvironmentNames.ConsoleHost) == "1";

            try
            {
                return ExecuteAsync(args, targetFramework).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                if (printExceptionsToStdError)
                {
                    PrintErrors(ex);
                    return 1;
                }

                throw;
            }
        }

        private static void PrintErrors(Exception ex)
        {
            while (ex != null)
            {
                if (ex is TargetInvocationException ||
                    ex is AggregateException)
                {
                    // Skip these exception messages as they are
                    // generic
                }
                else if (ex is ReflectionTypeLoadException)
                {
                    var typeLoadException = ex as ReflectionTypeLoadException;

                    foreach (var loaderException in typeLoadException.LoaderExceptions)
                    {
                        Console.Error.WriteLine(loaderException);
                    }
                }
                else
                {
                    Console.Error.WriteLine(ex);
                }

                ex = ex.InnerException;
            }
        }

        public static Task<int> ExecuteAsync(string[] args, FrameworkName targetFramework)
        {
            var app = new CommandLineApplication(throwOnUnexpectedArg: false);
            app.Name = Constants.BootstrapperExeName;
            app.FullName = Constants.BootstrapperFullName;

            // These options were handled in the native code, but got passed through here.
            // We just need to capture them and clean them up.
            var optionProject = app.Option("--project|-p <PATH>", "Path to the project.json file or the application folder. Defaults to the current folder if not provided.",
                CommandOptionType.SingleValue);
            var optionAppbase = app.Option("--appbase <PATH>", "Application base directory path",
                CommandOptionType.SingleValue);
            var optionLib = app.Option("--lib <LIB_PATHS>", "Paths used for library look-up",
                CommandOptionType.MultipleValue);
            var optionDebug = app.Option("--debug", "Waits for the debugger to attach before beginning execution.",
                CommandOptionType.NoValue);
#if DNX451
            var optionFramework = app.Option("--framework <FRAMEWORK_ID>", "Set the framework version to use when running (i.e. dnx451, dnx452, dnx46, ...)", CommandOptionType.SingleValue);
#endif

            var env = new RuntimeEnvironment();

            app.HelpOption("-?|-h|--help");
            app.VersionOption("--version",
                              () => env.GetShortVersion(),
                              () => env.GetFullVersion());

            // Options below are only for help info display
            // They will be forwarded to Microsoft.Dnx.ApplicationHost
            var optionsToForward = new[]
            {
                app.Option("--watch", "Watch file changes", CommandOptionType.NoValue),
                app.Option("--packages <PACKAGE_DIR>", "Directory containing packages", CommandOptionType.SingleValue),
                app.Option("--configuration <CONFIGURATION>", "The configuration to run under", CommandOptionType.SingleValue),
                app.Option("--port <PORT>", "The port to the compilation server", CommandOptionType.SingleValue)
            };

            app.Execute(args);

            // Help information was already shown because help option was specified
            if (app.IsShowingInformation)
            {
                return Task.FromResult(0);
            }

            // Show help information if no subcommand/option was specified
            if (!app.IsShowingInformation && app.RemainingArguments.Count == 0)
            {
                app.ShowHelp();
                return Task.FromResult(2);
            }

            // Some options should be forwarded to Microsoft.Dnx.ApplicationHost
            var appHostName = "Microsoft.Dnx.ApplicationHost";
            var appHostIndex = app.RemainingArguments.FindIndex(s =>
                string.Equals(s, appHostName, StringComparison.OrdinalIgnoreCase));
            foreach (var option in optionsToForward)
            {
                if (option.HasValue())
                {
                    if (appHostIndex < 0)
                    {
                        Console.WriteLine("The option '--{0}' can only be used with {1}", option.LongName, appHostName);
                        return Task.FromResult(1);
                    }

                    if (option.OptionType == CommandOptionType.NoValue)
                    {
                        app.RemainingArguments.Insert(appHostIndex + 1, "--" + option.LongName);
                    }
                    else if (option.OptionType == CommandOptionType.SingleValue)
                    {
                        app.RemainingArguments.Insert(appHostIndex + 1, "--" + option.LongName);
                        app.RemainingArguments.Insert(appHostIndex + 2, option.Value());
                    }
                    else if (option.OptionType == CommandOptionType.MultipleValue)
                    {
                        foreach (var value in option.Values)
                        {
                            app.RemainingArguments.Insert(appHostIndex + 1, "--" + option.LongName);
                            app.RemainingArguments.Insert(appHostIndex + 2, value);
                        }
                    }
                }
            }

            // Resolve the lib paths
            IEnumerable<string> searchPaths = ResolveSearchPaths(optionLib.Values, app.RemainingArguments);

            var bootstrapper = new Bootstrapper(searchPaths);
            return bootstrapper.RunAsync(app.RemainingArguments, env, targetFramework);
        }

        private static IEnumerable<string> ResolveSearchPaths(List<string> libPaths, List<string> remainingArgs)
        {
            var searchPaths = new List<string>();

            var defaultLibPath = Environment.GetEnvironmentVariable(EnvironmentNames.DefaultLib);

            if (!string.IsNullOrEmpty(defaultLibPath))
            {
                // Add the default lib folder if specified
                ExpandSearchPath(defaultLibPath, searchPaths);
            }

            // Add the expanded search libs to the list of paths
            foreach (var libPath in libPaths)
            {
                ExpandSearchPath(libPath, searchPaths);
            }

            // If a .dll or .exe is specified then turn this into
            // --lib {path to dll/exe} [dll/exe name]
            if (remainingArgs.Count > 0)
            {
                var application = remainingArgs[0];
                var extension = Path.GetExtension(application);

                if (!string.IsNullOrEmpty(extension) &&
                    extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    // Add the directory to the list of search paths
                    searchPaths.Add(Path.GetDirectoryName(Path.GetFullPath(application)));

                    // Modify the argument to be the dll/exe name
                    remainingArgs[0] = Path.GetFileNameWithoutExtension(application);
                }
            }

            return searchPaths;
        }

        private static void ExpandSearchPath(string libPath, List<string> searchPaths)
        {
            if (libPath.IndexOf(';') >= 0)
            {
                foreach (var path in libPath.Split(_libPathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    searchPaths.Add(Path.GetFullPath(path));
                }
            }
            else
            {
                searchPaths.Add(libPath);
            }
        }
    }
}
