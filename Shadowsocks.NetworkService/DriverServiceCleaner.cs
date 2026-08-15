using System.Diagnostics;

namespace Shadowsocks.NetworkService;

/// <summary>
/// Removes the WinDivert kernel service after every capture process has exited.
/// This is used by Game Mode so anti-cheat-sensitive applications do not see an
/// active WinDivert driver merely because Shadowsocks was previously in Admin Mode.
/// </summary>
internal static class DriverServiceCleaner
{
    private static readonly string[] ServiceNames = ["WinDivert", "WinDivert64"];

    public static bool TryRemove()
    {
        bool clean = true;
        foreach (string serviceName in ServiceNames)
        {
            RunSc("stop", serviceName);
            int deleteExitCode = RunSc("delete", serviceName);
            if (deleteExitCode is not (0 or 1060))
            {
                // sc.exe normally returns a generic non-zero process code rather than
                // the SCM error code, so failure here is advisory. The next WinDivertOpen
                // can still recover an already stopped/marked-for-delete service.
                clean = false;
            }
        }

        return clean;
    }

    private static int RunSc(string verb, string serviceName)
    {
        try
        {
            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
                Arguments = $"{verb} {serviceName}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            if (process.WaitForExit(5000))
            {
                return process.ExitCode;
            }

            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
            catch
            {
            }

            return -1;
        }
        catch
        {
            return -1;
        }
    }
}
