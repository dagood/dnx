// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNet.FileProviders;
using Microsoft.Dnx.Compilation;
using Microsoft.Dnx.Runtime;
using Microsoft.Dnx.Runtime.Common.Impl;
using Microsoft.Framework.Logging;
using NuGet;

namespace Microsoft.Dnx.Watcher.Core
{
    public class ProjectWatcher
    {
        private readonly Func<string, IFileProvider> _fileProviderFactory;
        private readonly IProjectGraphProvider _projectGraphProvider;
        private readonly ILogger _logger;
        private readonly bool _isWindows;

        private IEnumerable<FileSetWatcher> _fileWatchers;

        private string _fullProjectFilePath;
        private string _relativeProjectFilePath;
        private string _rootFolder;

        private IFileProvider _rootFileProvider;

        public ProjectWatcher(
            Func<string, IFileProvider> fileProviderFactory,
            IProjectGraphProvider projectGraphProvider,
            ILogger logger,
            IRuntimeEnvironment runtimeEnviornment)
        {
            _fileProviderFactory = fileProviderFactory;
            _projectGraphProvider = projectGraphProvider;
            _logger = logger;
            _isWindows = string.Equals(runtimeEnviornment.OperatingSystem, "windows", StringComparison.OrdinalIgnoreCase);
        }

        public void Initialize(string projectOrDirectory)
        {
            _fullProjectFilePath = ResolveProjectFileToWatch(projectOrDirectory);

            // Append a / at the end of the path to make it a directory
            _rootFolder = ProjectRootResolver.ResolveRootDirectory(_fullProjectFilePath) + Path.DirectorySeparatorChar;
            _rootFileProvider = _fileProviderFactory(_rootFolder);

            _relativeProjectFilePath = PathUtility.GetRelativePath(_rootFolder, _fullProjectFilePath);
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

        public async Task<bool> WatchAsync(CancellationToken cancellationToken)
        {
            var project = await WaitForValidProjectJsonAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return true;
            }

            CreateFileWatchers(project);

            //var dnxWatcher = new ProcessWatcher(
            //    ResolveProcessHostName(),
            //    ResolveProcessArguments("web"));

            //int dnxProcessId = dnxWatcher.Start();
            //Console.WriteLine(dnxProcessId);

            //int dnxExitCode = dnxWatcher.WaitForExit(CancellationToken.None);

            return true;
        }

        private async Task<Project> WaitForValidProjectJsonAsync(CancellationToken cancellationToken)
        {
            Project project = null;

            using (var projectFileWatcher = new FileSetWatcher(_rootFileProvider, _rootFolder))
            {
                projectFileWatcher.AddFilesToWatch(new string[] { _fullProjectFilePath });

                while (!cancellationToken.IsCancellationRequested)
                {
                    string errors;
                    if (TryGetProject(_fullProjectFilePath, out project, out errors))
                    {
                        return project;
                    }

                    _logger.LogError($"Error(s) reading project file '{_fullProjectFilePath}': ");
                    _logger.LogError(errors);
                    _logger.LogError("Fix the error to continue.");
                    
                    await projectFileWatcher.WatchAsync(cancellationToken);

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation($"File changed: {_fullProjectFilePath}");
                    }
                }

                return null;
            }
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
            return $"/c dnx --debug --appbase {Path.GetDirectoryName(_fullProjectFilePath)} --project {_fullProjectFilePath} Microsoft.Dnx.ApplicationHost {userArguments}";
        }

        private void CreateFileWatchers(Project project)
        {
            // TODO: we need the framework to run on
            var libManager = _projectGraphProvider.GetProjectGraph(
                project,
                new FrameworkName(FrameworkNames.LongNames.Dnx, new Version(4, 5, 1)));

            _fileWatchers = libManager.GetLibraryDescriptions().OfType<ProjectDescription>()
                .Select(lib => lib.Project)
                .GroupBy(proj => ProjectRootResolver.ResolveRootDirectory(proj.ProjectFilePath))
                .Select(group =>
                {
                    var watcher = new FileSetWatcher(_fileProviderFactory(group.Key), group.Key);
                    watcher.AddFilesToWatch(group.SelectMany(proj => proj.Files.SourceFiles));
                    return watcher;
                })
                .ToList();
        }

        // Same as TryGetProject but it doesn't throw
        private bool TryGetProject(string projectFile, out Project project, out string  errorMessage)
        {
            //TODO: Consider printing the errors
            try
            {
                ICollection<DiagnosticMessage> errors = new List<DiagnosticMessage>();
                if (!Project.TryGetProject(_fullProjectFilePath, out project, errors))
                {
                    errorMessage = string.Join(Environment.NewLine, errors.Select(e => e.ToString()));
                }
                else
                {
                    errorMessage = null;
                    return true;
                }
            }
            catch(Exception ex)
            {
                errorMessage = ex.Message;
            }

            project = null;
            return false;
        }
    }
}
