// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Threading;
using Microsoft.AspNet.FileProviders;
using Microsoft.Dnx.Compilation;
using Microsoft.Dnx.Runtime.Common.CommandLine;
using Microsoft.Dnx.Watcher.Core;
using Microsoft.Framework.Logging;

namespace Microsoft.Dnx.Watcher
{
    public class Program
    {
        private readonly ILoggerFactory _loggerFactory;

        public Program()
        {
            _loggerFactory = new LoggerFactory()
                .AddConsole(LogLevel.Debug);
        }

        public int Main(string[] args)
        {
            var app = new CommandLineApplication();
            app.Name = "dnx-watch";
            app.FullName = "Microsoft .NET DNX File Watcher";

            var optionVerbose = app.Option("-v|--verbose", "Show verbose output", CommandOptionType.NoValue);
            app.HelpOption("-?|-h|--help");
            //app.VersionOption("--version", () => _runtimeEnvironment.GetShortVersion(), () => _runtimeEnvironment.GetFullVersion());

            // Show help information if no subcommand/option was specified
            app.OnExecute(() =>
            {
                app.ShowHelp();
                return 2;
            });

            var projectArg = app.Option(
                "--project <PATH>",
                "Path to the project.json file or the application folder. Defaults to the current folder if not provided.",
                CommandOptionType.SingleValue);

            app.OnExecute(async () =>
            {
                var watcher = new ProjectWatcher(
                    root => { return new PhysicalFileProvider(root); },
                    new ProjectGraphProvider(),
                    _loggerFactory);
                watcher.Initialize(projectArg.Value());

                var success = await watcher.WatchAsync(CancellationToken.None);

                return success ? 0 : 1;
            });

            return app.Execute(args);
        }
    }
}
