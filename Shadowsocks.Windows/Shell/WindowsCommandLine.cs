#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Shadowsocks.Windows.Shell;

public static partial class WindowsCommandLine
{
    public static string[] ParseArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return Array.Empty<string>();
        }

        // CommandLineToArgvW expects a complete command line. Add a synthetic executable
        // so the returned argv semantics match Environment.GetCommandLineArgs().
        string commandLine = $"shadowsocks-reborn.exe {arguments}";
        nint argumentVector = CommandLineToArgv(commandLine, out int argumentCount);
        if (argumentVector == nint.Zero)
        {
            return Array.Empty<string>();
        }

        try
        {
            var result = new List<string>(Math.Max(0, argumentCount - 1));
            for (int index = 1; index < argumentCount; index++)
            {
                nint argumentPointer = Marshal.ReadIntPtr(argumentVector, index * IntPtr.Size);
                result.Add(Marshal.PtrToStringUni(argumentPointer) ?? string.Empty);
            }

            return result.ToArray();
        }
        finally
        {
            _ = LocalFree(argumentVector);
        }
    }

    [LibraryImport("shell32.dll", EntryPoint = "CommandLineToArgvW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CommandLineToArgv(string commandLine, out int argumentCount);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static partial nint LocalFree(nint memory);
}
