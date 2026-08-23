using Shadowsocks.NetworkService.Ipc;

namespace Shadowsocks.NetworkService.Routing;

internal sealed class ApplicationPolicy
{
    private const string DnsCryptExecutableName = "dnscrypt-proxy.exe";
    private readonly IReadOnlyList<ApplicationRuleDto> _rules;
    private readonly int _mainProcessId;
    private readonly HashSet<int> _excludedProcessIds;
    private readonly RouteAction _defaultRoute;
    private readonly string? _dnsCryptComponentRoot;
    private readonly int _dnsCryptProcessId;
    private readonly DnsPolicyMode _dnsPolicyMode;
    private readonly bool _managedRoutingEnabled;

    public ApplicationPolicy(StartRequest request)
    {
        _rules = request.ApplicationRules ?? [];
        _mainProcessId = request.MainProcessId;
        _excludedProcessIds = request.ExcludedProcessIds is null ? [] : [.. request.ExcludedProcessIds];
        _defaultRoute = request.DefaultRoute is RouteAction.Direct or RouteAction.Block
            ? request.DefaultRoute
            : RouteAction.Proxy;
        _dnsCryptComponentRoot = NormalizeDirectory(request.DnsPolicy?.DnsCryptComponentRoot);
        _dnsCryptProcessId = request.DnsPolicy?.DnsCryptProcessId ?? 0;
        _dnsPolicyMode = request.DnsPolicy?.Mode ?? DnsPolicyMode.System;
        _managedRoutingEnabled = request.ManagedRouting?.Enabled == true;
    }

    public bool IsExcludedProcess(int processId, string? processPath = null)
        => processId == Environment.ProcessId
           || processId == _mainProcessId
           || _excludedProcessIds.Contains(processId)
           || IsManagedDnsCryptExecutable(processPath);

    /// <summary>
    /// Determines whether a process may bypass transparent port-53 policy. In DNSCrypt mode,
    /// Shadowsocks and SIP003 remain excluded from generic traffic capture but their DNS/53 is
    /// still intercepted; only NetworkService and the managed dnscrypt-proxy runtime are exempt
    /// to prevent the local DNS bridge from recursively capturing itself.
    /// </summary>
    public bool IsDnsInterceptionExempt(int processId, string? processPath = null)
    {
        // In DNSCrypt mode, generic capture exclusions (Shadowsocks itself and SIP003
        // plugins) must NOT become DNS exclusions. Otherwise their own UDP/TCP 53
        // traffic can escape in plaintext while the rest of the system is protected.
        // The NetworkService process and the managed dnscrypt-proxy process remain
        // exempt so the local transparent bridge cannot recursively capture itself.
        if (_dnsPolicyMode == DnsPolicyMode.DnsCrypt)
        {
            return processId == Environment.ProcessId
                || (_dnsCryptProcessId > 0 && processId == _dnsCryptProcessId)
                || IsManagedDnsCryptExecutable(processPath);
        }

        return processId == Environment.ProcessId
            || processId == _mainProcessId
            || _excludedProcessIds.Contains(processId)
            || (_dnsCryptProcessId > 0 && processId == _dnsCryptProcessId)
            || IsManagedDnsCryptExecutable(processPath);
    }

    public RouteAction Evaluate(
        byte protocol,
        ushort remotePort,
        int processId,
        string? processPath,
        string? processName)
    {
        if (IsExcludedProcess(processId, processPath))
        {
            return RouteAction.Direct;
        }

        foreach (ApplicationRuleDto rule in _rules)
        {
            if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Application))
            {
                continue;
            }

            if (Matches(rule.Application.Trim(), processPath, processName))
            {
                if (rule.Action != RouteAction.Default)
                {
                    return rule.Action;
                }

                // Default means "continue through the shared routing policy", matching
                // TrafficPolicyEngine in the main process rather than bypassing ABP rules.
                break;
            }
        }

        // Only ordinary TCP is deferred for Host/SNI inspection. DNS has its own policy,
        // UDP has no reliable hostname at this layer, and explicit application rules above
        // remain authoritative without any payload inspection.
        if (_managedRoutingEnabled && protocol == 6 && remotePort != 53)
        {
            return RouteAction.Deferred;
        }

        return _defaultRoute;
    }

    public RouteAction DefaultRoute => _defaultRoute;

    private bool IsManagedDnsCryptExecutable(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(_dnsCryptComponentRoot) || string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(processPath);
            if (!string.Equals(Path.GetFileName(fullPath), DnsCryptExecutableName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string directory = Path.GetDirectoryName(fullPath) ?? string.Empty;
            string normalizedDirectory = NormalizeDirectory(directory) ?? string.Empty;
            return normalizedDirectory.StartsWith(_dnsCryptComponentRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string full = Path.GetFullPath(path.Trim());
            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
        catch
        {
            return null;
        }
    }

    private static bool Matches(string pattern, string? processPath, string? processName)
    {
        string path = processPath ?? string.Empty;
        string name = processName ?? (string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileName(path));
        string fileName = string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileName(path);
        string nameWithExe = string.IsNullOrEmpty(name) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name
            : name + ".exe";
        if (!pattern.Contains('*') && !pattern.Contains('?'))
        {
            string patternFileName = Path.GetFileName(pattern);
            return string.Equals(pattern, path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pattern, fileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pattern, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pattern, nameWithExe, StringComparison.OrdinalIgnoreCase)
                || string.Equals(patternFileName, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(patternFileName, nameWithExe, StringComparison.OrdinalIgnoreCase);
        }

        return WildcardMatch(path, pattern)
            || WildcardMatch(fileName, pattern)
            || WildcardMatch(name, pattern)
            || WildcardMatch(nameWithExe, pattern);
    }

    private static bool WildcardMatch(string value, string pattern)
    {
        int valueIndex = 0;
        int patternIndex = 0;
        int starIndex = -1;
        int checkpoint = 0;
        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length
                && (pattern[patternIndex] == '?'
                    || char.ToUpperInvariant(pattern[patternIndex]) == char.ToUpperInvariant(value[valueIndex])))
            {
                valueIndex++;
                patternIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                checkpoint = valueIndex;
            }
            else if (starIndex >= 0)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++checkpoint;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }
}
