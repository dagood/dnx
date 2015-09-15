// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Threading;

namespace Microsoft.Dnx.Watcher.Core
{
    public class ProcessWatcher
    {
        private readonly ProcessStartInfo _processStartInfo;

        private Process _runningProcess;

        public ProcessWatcher(string executable, string arguments)
        {
            _processStartInfo = new ProcessStartInfo()
            {
                FileName = executable,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
        }

        public int Start()
        {
            // TODO: check that it is running
            _runningProcess = new Process();
            _runningProcess.StartInfo = _processStartInfo;
            _runningProcess.Start();

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
                Console.Error.WriteLine(e.Data);
            }
        }

        private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                Console.WriteLine(e.Data);
            }
        }

        public int WaitForExit(CancellationToken cancellationToken)
        {
            // TODO: Check that it is running

            try
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
                return _runningProcess.ExitCode;
            }
            finally
            {
                _runningProcess = null;
            }
        }
    }
}
