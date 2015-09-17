// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Framework.Logging;

namespace Microsoft.Dnx.Watcher.Core
{
    public class ProcessWatcher
    {
        private readonly ILoggerFactory _loggerFactory;

        private Process _runningProcess;
        private ILogger _logger;

        public ProcessWatcher(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;
        }

        public int Start(string executable, string arguments)
        {
            // TODO: check that it is running 
            _runningProcess = new Process();
            _runningProcess.StartInfo = new ProcessStartInfo()
            {
                FileName = executable,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
#if !DNXCORE50
            _runningProcess.StartInfo.EnvironmentVariables["DNX_TRACE"] = "1";
#endif
            _runningProcess.Start();

            _logger = _loggerFactory.CreateLogger($"{executable}:{_runningProcess.Id}");

            _runningProcess.EnableRaisingEvents = true;

            _runningProcess.ErrorDataReceived += OnErrorDataReceived;
            _runningProcess.OutputDataReceived += OnOutputDataReceived;

            _runningProcess.BeginOutputReadLine();
            _runningProcess.BeginErrorReadLine();

            return _runningProcess.Id;
        }

        private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogInformation(e.Data);
            }
        }

        private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                _logger.LogError(e.Data);
            }
        }

        public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
        {
            // TODO: Check that it is running

            try
            {
                await Task.Run(() =>
                {
                    while (true)
                    {
                        if (_runningProcess.WaitForExit(500))
                        {
                            break;
                        }

                        if (cancellationToken.IsCancellationRequested)
                        {
                            if (!_runningProcess.HasExited)
                            {
                                _runningProcess.Kill();
                            }

                            break;
                        }
                    }
                });

                return _runningProcess.ExitCode;
            }
            finally
            {
                _runningProcess = null;
            }
        }
    }
}
