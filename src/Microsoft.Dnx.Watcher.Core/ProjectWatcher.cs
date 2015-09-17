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
        
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;

        private string _fullProjectFilePath;
        private string _relativeProjectFilePath;
        private string _rootFolder;

        private IFileProvider _rootFileProvider;

        public ProjectWatcher(
            Func<string, IFileProvider> fileProviderFactory,
            IProjectGraphProvider projectGraphProvider,
            ILoggerFactory loggerFactory)
        {
            _fileProviderFactory = fileProviderFactory;
            _projectGraphProvider = projectGraphProvider;
            _loggerFactory = loggerFactory;
            _logger = _loggerFactory.CreateLogger<ProjectWatcher>();
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
            while (!cancellationToken.IsCancellationRequested)
            {
                var project = await WaitForValidProjectJsonAsync(cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return true;
                }

                CancellationTokenSource currentRunCancellationSource = new CancellationTokenSource();
                CancellationTokenSource combinedCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    currentRunCancellationSource.Token);

                var fileWatchers = CreateFileWatchers(project);
                var fileWatchingTasks = fileWatchers.Select(watcher => watcher.WatchAsync(combinedCancellationSource.Token));

                var dnxWatcher = new ProcessWatcher(_loggerFactory);

                int dnxProcessId = dnxWatcher.Start("dnx", ResolveProcessArguments("web"));
                _logger.LogVerbose($"dnx process id: {dnxProcessId}");

                var dnxTask = dnxWatcher.WaitForExitAsync(combinedCancellationSource.Token);

                var tasksToWait = new Task[] { dnxTask }.Concat(fileWatchingTasks).ToArray();

                int finishedTaskIndex = Task.WaitAny(tasksToWait, cancellationToken);
                
                // Regardless of the outcome, make sure everything is cancelled
                // and wait for dnx to exit. We don't want orphan processes
                currentRunCancellationSource.Cancel();
                await dnxTask;

                if (cancellationToken.IsCancellationRequested)
                {
                    return true;
                }

                if (finishedTaskIndex == 0)
                {
                    // This is the dnx task
                    var dnxExitCode = dnxTask.Result;
                    _logger.LogInformation($"dnx exit code: {dnxExitCode}");
                }
                else
                {
                    // This is a file watcher task
                    var fileChangedTask = tasksToWait[finishedTaskIndex] as Task<string>;
                    _logger.LogInformation($"File changed: {fileChangedTask.Result}");
                }

                // Wait for all tasks to finish before starting a new iteration
                // otherwise, there might some files locked
                Task.WaitAll(tasksToWait);

                Console.WriteLine();
            }


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

                    _logger.LogInformation($"Error(s) reading project file '{_fullProjectFilePath}': ");
                    _logger.LogError(errors);
                    _logger.LogInformation("Fix the error to continue.");

                    await projectFileWatcher.WatchAsync(cancellationToken);

                    if (!cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogInformation($"File changed: {_fullProjectFilePath}");
                    }
                }

                return null;
            }
        }

        private string ResolveProcessArguments(string userArguments)
        {
            // TODO: check windows
            // TODO: Fix this appbase hack once Pawel fixes the env var
            return $"--appbase {Path.GetDirectoryName(_fullProjectFilePath)} --project {_fullProjectFilePath} Microsoft.Dnx.ApplicationHost {userArguments}";
        }

        private IEnumerable<FileSetWatcher> CreateFileWatchers(Project project)
        {
            // TODO: we need the framework to run on
            var libManager = _projectGraphProvider.GetProjectGraph(
                project,
                new FrameworkName(FrameworkNames.LongNames.Dnx, new Version(4, 5, 1)));

            return libManager.GetLibraryDescriptions().OfType<ProjectDescription>()
                .Select(lib => lib.Project)
                .GroupBy(proj => ProjectRootResolver.ResolveRootDirectory(proj.ProjectFilePath) + Path.DirectorySeparatorChar)
                .Select(group =>
                {
                    var watcher = new FileSetWatcher(_fileProviderFactory(group.Key), group.Key);

                    watcher.AddFilesToWatch(group.SelectMany(proj =>
                        proj.Files.SourceFiles.Concat(
                        proj.Files.PreprocessSourceFiles).Concat(
                        proj.Files.SharedFiles).Concat(
                        new string[] { proj.ProjectFilePath })));

                    return watcher;
                })
                .ToList();
        }

        // Same as TryGetProject but it doesn't throw
        private bool TryGetProject(string projectFile, out Project project, out string errorMessage)
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
            catch (Exception ex)
            {
                errorMessage = ex.Message;
            }

            project = null;
            return false;
        }
    }
}
