using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Shadowsocks.Core
{
    /// <summary>
    /// Process/runtime information required by the core. The UI initializes this once
    /// during startup so core code never needs to depend on Program, WinForms or WPF.
    /// </summary>
    public static class RuntimeEnvironment
    {
        private static string[] _arguments = Array.Empty<string>();

        public static string ExecutablePath { get; private set; } =
            Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? AppContext.BaseDirectory;

        public static string WorkingDirectory { get; private set; } =
            Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty) ?? AppContext.BaseDirectory;

        public static IReadOnlyList<string> Arguments => _arguments;

        public static void Initialize(string executablePath, string workingDirectory, IEnumerable<string> arguments)
        {
            if (!string.IsNullOrWhiteSpace(executablePath))
                ExecutablePath = Path.GetFullPath(executablePath);
            if (!string.IsNullOrWhiteSpace(workingDirectory))
                WorkingDirectory = Path.GetFullPath(workingDirectory);
            _arguments = arguments?.ToArray() ?? Array.Empty<string>();
        }
    }
}
