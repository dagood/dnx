// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading;
using Microsoft.Dnx.Runtime;

namespace Microsoft.Dnx.Watcher.Core
{
    public class ProjectWatcher
    {
        private readonly string _watchedProjectFile;
        private readonly bool _isWindows;

        public ProjectWatcher(string projectOrDirectory, IRuntimeEnvironment runtimeEnviornment)
        {
            // TODO: consider doing this in an initialize method so don't throw
            _watchedProjectFile = ResolveProjectFileToWatch(projectOrDirectory);

            _isWindows = string.Equals(runtimeEnviornment.OperatingSystem, "windows", StringComparison.OrdinalIgnoreCase);
        }

        private string ResolveProjectFileToWatch(string projectOrDirectory)
        {
            if (string.IsNullOrEmpty(projectOrDirectory))
            {
                projectOrDirectory = Directory.GetCurrentDirectory();
            }

            if (!string.Equals(Path.GetFileName(projectOrDirectory), Project.ProjectFileName, System.StringComparison.Ordinal))
            {
                projectOrDirectory = Path.Combine(projectOrDirectory, Project.ProjectFileName);
            }

            if (!File.Exists(projectOrDirectory))
            {
                // TODO: better error message
                throw new InvalidOperationException("Project file not found");
            }

            return projectOrDirectory;
        }

        public bool Watch()
        {
            //var dnxWatcher = new ProcessWatcher(
            //    ResolveProcessHostName(),
            //    ResolveProcessArguments("web"));

            //int dnxProcessId = dnxWatcher.Start();
            //Console.WriteLine(dnxProcessId);
            //int dnxExitCode = dnxWatcher.WaitForExit(CancellationToken.None);

            return true;
        }

        private void WaitForProjectJsonFileToChange()
        {

        }

        private string ResolveProcessHostName()
        {
            // TODO: check windows
            return "cmd";
        }

        private string ResolveProcessArguments(string userArguments)
        {
            // TODO: check windows
            // TODO: Fix this appbase hack once Pawel fixes the env var
            return $"/c dnx --debug --appbase {Path.GetDirectoryName(_watchedProjectFile)} --project {_watchedProjectFile} Microsoft.Dnx.ApplicationHost {userArguments}";
        }
    }
}
