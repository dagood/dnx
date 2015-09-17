// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNet.FileProviders;
using NuGet;

namespace Microsoft.Dnx.Watcher.Core
{
    internal class FileSetWatcher : IDisposable
    {
        private readonly IFileProvider _rootProvider;
        private readonly string _rootPath;

        private readonly List<string> _filesToWatch = new List<string>();
        private readonly Dictionary<string, IDisposable> _triggers = new Dictionary<string, IDisposable>();

        private ManualResetEvent _changedEvent = new ManualResetEvent(false);
        private string _lastChangedFile;

        public FileSetWatcher(IFileProvider rootFileProvider, string rootPath)
        {
            _rootProvider = rootFileProvider;
            _rootPath = rootPath;
        }

        public void AddFilesToWatch(IEnumerable<string> files)
        {
            _filesToWatch.AddRange(files);
        }

        public async Task<string> WatchAsync(CancellationToken cancellationToken)
        {
            DisposeTriggers();
            _changedEvent.Reset();

            foreach (var file in _filesToWatch)
            {
                if (_triggers.ContainsKey(file))
                {
                    // We are already watching this file
                    continue;
                }

                var relativePathToWatch = PathUtility.GetRelativePath(_rootPath, file);

                var triggerCallback = _rootProvider.Watch(relativePathToWatch)
                    .RegisterExpirationCallback(f =>
                    {
                        _lastChangedFile = f as string;
                        _changedEvent.Set();
                    },
                    state: file);

                _triggers.Add(file, triggerCallback);
            }

            await Task.Run(
                () =>
                {
                    while (!cancellationToken.IsCancellationRequested && 
                           !_changedEvent.WaitOne(500))
                    {
                        // File changed or task cancelled
                    }
                },
                cancellationToken);

            return _lastChangedFile;
        }

        private void DisposeTriggers()
        {
            foreach (var trigger in _triggers.Values)
            {
                trigger.Dispose();
            }
            _triggers.Clear();
        }

        public void Dispose()
        {
            DisposeTriggers();

            var ev = _changedEvent;
            _changedEvent = null;
            ev.Dispose();
        }
    }
}
