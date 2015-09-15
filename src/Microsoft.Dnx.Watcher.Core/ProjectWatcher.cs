// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading;
using Microsoft.AspNet.FileProviders;
using Microsoft.Dnx.Runtime;
using Microsoft.Framework.Caching;

namespace Microsoft.Dnx.Watcher.Core
{
    public class ProjectWatcher
    {
        private readonly bool _isWindows;
        private readonly Func<string, IFileProvider> _fileProviderFactory;

        private string _watchedProjectFile;
        private IFileProvider _rootFileProvider;

        public ProjectWatcher(
            
            Func<string, IFileProvider> fileProviderFactory, 
            IRuntimeEnvironment runtimeEnviornment)
        {
            _fileProviderFactory = fileProviderFactory;
            _isWindows = string.Equals(runtimeEnviornment.OperatingSystem, "windows", StringComparison.OrdinalIgnoreCase);
        }

        public void Initialize(string projectOrDirectory)
        {
            _watchedProjectFile = ResolveProjectFileToWatch(projectOrDirectory);
            _rootFileProvider = _fileProviderFactory(Path.GetDirectoryName(_watchedProjectFile));

            // TODO: check initialized
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

        public bool Watch(CancellationToken cancellationToken)
        {
            var project = WaitForValidProjectJson(cancellationToken);

            //var dnxWatcher = new ProcessWatcher(
            //    ResolveProcessHostName(),
            //    ResolveProcessArguments("web"));

            //int dnxProcessId = dnxWatcher.Start();
            //Console.WriteLine(dnxProcessId);
            //int dnxExitCode = dnxWatcher.WaitForExit(CancellationToken.None);

            return true;
        }

        private Project WaitForValidProjectJson(CancellationToken cancellationToken)
        {
            Project project;
            try
            {
                Project.TryGetProject(_watchedProjectFile, out project);
            }
            catch
            {
                // Do nothing but that TryGetProject actually throws...
                project = null;
            }

            if (project == null)
            {
                var expiration = _rootFileProvider.Watch(Project.ProjectFileName);

                using (var changed = new ManualResetEvent(false))
                {
                    expiration.RegisterExpirationCallback(_ =>
                    {
                        changed.Set();
                    },
                    state: null);

                    changed.WaitOne();
                }
            }

            return project;
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
