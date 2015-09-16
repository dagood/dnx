// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.AspNet.FileProviders;
using Microsoft.Dnx.Compilation;
using Microsoft.Dnx.Runtime;
using Microsoft.Dnx.Runtime.Common.Impl;
using NuGet;

namespace Microsoft.Dnx.Watcher.Core
{
    public class ProjectWatcher
    {
        private readonly Func<string, IFileProvider> _fileProviderFactory;
        private readonly IProjectGraphProvider _projectGraphProvider;
        private readonly bool _isWindows;

        private string _watchedProjectFile;
        private string _rootFolder;

        private IFileProvider _rootFileProvider;

        public ProjectWatcher(
            Func<string, IFileProvider> fileProviderFactory,
            IProjectGraphProvider projectGraphProvider,
            IRuntimeEnvironment runtimeEnviornment)
        {
            _fileProviderFactory = fileProviderFactory;
            _projectGraphProvider = projectGraphProvider;
            _isWindows = string.Equals(runtimeEnviornment.OperatingSystem, "windows", StringComparison.OrdinalIgnoreCase);
        }

        public void Initialize(string projectOrDirectory)
        {
            var projectFullPath = ResolveProjectFileToWatch(projectOrDirectory);

            _rootFolder = ProjectRootResolver.ResolveRootDirectory(projectFullPath);
            _rootFileProvider = _fileProviderFactory(_rootFolder);

            _watchedProjectFile = PathUtility.GetRelativePath(projectFullPath, _rootFolder);
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

            var libManager = _projectGraphProvider.GetProjectGraph(
                project,
                new FrameworkName(FrameworkNames.LongNames.Dnx, new Version(4, 5, 1)));

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
            Project project = null;

            if (TryGetProject(_watchedProjectFile, out project))
            {
                return project;
            }

            // Invalid project so wait for a valid one
            using (var projectFileChanged = new ManualResetEvent(false))
            using (_rootFileProvider.Watch(Project.ProjectFileName)
                    .RegisterExpirationCallback(
                        _ => { projectFileChanged.Set(); },
                        state: null))
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (projectFileChanged.WaitOne(500))
                    {
                        if (TryGetProject(_watchedProjectFile, out project))
                        {
                            return project;
                        }
                    }
                }
            }

            return null;
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

        // Same as TryGetProject but it doesn't throw
        public bool TryGetProject(string projectFile, out Project project)
        {
            //TODO: Consider printing the errors
            try
            {
                return Project.TryGetProject(_watchedProjectFile, out project);
            }
            catch
            {
                project = null;
                return false;
            }
        }
    }
}
