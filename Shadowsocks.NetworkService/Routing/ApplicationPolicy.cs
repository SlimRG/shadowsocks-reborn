using Shadowsocks.NetworkService.Ipc;

namespace Shadowsocks.NetworkService.Routing;

internal sealed class ApplicationPolicy
{
    private readonly IReadOnlyList<ApplicationRuleDto> _rules;
    private readonly int _mainProcessId;
    private readonly HashSet<int> _excludedProcessIds;
    private readonly RouteAction _defaultRoute;

    public ApplicationPolicy(StartRequest request)
    {
        _rules = request.ApplicationRules ?? [];
        _mainProcessId = request.MainProcessId;
        _excludedProcessIds = request.ExcludedProcessIds is null ? [] : [.. request.ExcludedProcessIds];
        _defaultRoute = request.DefaultRoute is RouteAction.Direct or RouteAction.Block
            ? request.DefaultRoute
            : RouteAction.Proxy;
    }

    public RouteAction Evaluate(int processId, string? processPath, string? processName)
    {
        if (processId == Environment.ProcessId || processId == _mainProcessId || _excludedProcessIds.Contains(processId))
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
                return rule.Action == RouteAction.Default ? _defaultRoute : rule.Action;
            }
        }

        return _defaultRoute;
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
