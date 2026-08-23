#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shadowsocks.Util.ProcessManagement;

namespace Shadowsocks.Controller.Service
{
    internal sealed class SystemDnsCryptRuntimePlatform : IDnsCryptRuntimePlatform
    {
        public async Task<DnsCryptCommandResult> ExecuteAsync(
            string executablePath,
            string workingDirectory,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using Process process = CreateProcess(executablePath, workingDirectory, arguments);
            if (!process.Start())
                throw new InvalidOperationException($"Unable to start '{executablePath}'.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw new TimeoutException(
                    $"'{Path.GetFileName(executablePath)} {string.Join(" ", arguments)}' timed out " +
                    $"after {timeout.TotalSeconds:0} seconds.");
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
            string outputText = await stdout.ConfigureAwait(false);
            string errorText = await stderr.ConfigureAwait(false);
            return new DnsCryptCommandResult(process.ExitCode, outputText, errorText);
        }

        public IDnsCryptRunningProcess Start(
            string executablePath,
            string workingDirectory,
            IReadOnlyList<string> arguments,
            Action<string> standardOutput,
            Action<string> standardError)
        {
            Process process = CreateProcess(executablePath, workingDirectory, arguments);
            process.EnableRaisingEvents = true;
            if (standardOutput is not null)
                process.OutputDataReceived += (_, args) => { if (args.Data is not null) standardOutput(args.Data); };
            if (standardError is not null)
                process.ErrorDataReceived += (_, args) => { if (args.Data is not null) standardError(args.Data); };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException($"Unable to start '{executablePath}'.");
            }
            try
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                return new SystemDnsCryptRunningProcess(process);
            }
            catch
            {
                TryKill(process);
                process.Dispose();
                throw;
            }
        }

        private static Process CreateProcess(string executablePath, string workingDirectory, IReadOnlyList<string> arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);
            return new Process { StartInfo = startInfo };
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
    }

    internal sealed class SystemDnsCryptRunningProcess : IDnsCryptRunningProcess
    {
        private readonly Process process;
        private readonly Job killOnCloseJob;

        public SystemDnsCryptRunningProcess(Process process)
        {
            ArgumentNullException.ThrowIfNull(process);
            this.process = process;
            killOnCloseJob = new Job();
            try
            {
                killOnCloseJob.AddProcessOrThrow(process, "DNSCrypt Proxy");
            }
            catch
            {
                killOnCloseJob.Dispose();
                throw;
            }
            process.Exited += ForwardExited;
        }

        public int Id => process.Id;
        public bool HasExited
        {
            get
            {
                try { return process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        public event EventHandler? Exited;

        public void Kill()
        {
            if (!HasExited)
                process.Kill(entireProcessTree: true);
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => process.WaitForExitAsync(cancellationToken);

        public void Dispose()
        {
            process.Exited -= ForwardExited;
            killOnCloseJob.Dispose();
            process.Dispose();
        }

        private void ForwardExited(object? sender, EventArgs e) => Exited?.Invoke(this, EventArgs.Empty);
    }
}
