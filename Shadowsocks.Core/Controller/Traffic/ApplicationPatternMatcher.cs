using System;
using System.IO;

namespace Shadowsocks.Controller.Traffic
{
    internal static class ApplicationPatternMatcher
    {
        public static bool Matches(string pattern, string processPath, string processName)
        {
            if (!TryCompile(pattern, out CompiledApplicationPattern compiled))
            {
                return false;
            }

            ApplicationMatchTarget target = CreateTarget(processPath, processName);
            return Matches(compiled, target);
        }

        internal static bool TryCompile(string pattern, out CompiledApplicationPattern compiled)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                compiled = default;
                return false;
            }

            string normalized = pattern.Trim();
            compiled = new CompiledApplicationPattern(
                normalized,
                Path.GetFileName(normalized),
                normalized.Contains('*') || normalized.Contains('?'));
            return true;
        }

        internal static ApplicationMatchTarget CreateTarget(string processPath, string processName)
        {
            string path = processPath ?? string.Empty;
            string fileName = path.Length == 0 ? string.Empty : Path.GetFileName(path);
            string name = processName ?? fileName;
            string nameWithExe = name.Length == 0 || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name
                : string.Concat(name, ".exe");
            return new ApplicationMatchTarget(path, fileName, name, nameWithExe);
        }

        internal static bool Matches(CompiledApplicationPattern compiled, ApplicationMatchTarget target)
        {
            if (!compiled.HasWildcards)
            {
                return string.Equals(compiled.Pattern, target.Path, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(compiled.Pattern, target.FileName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(compiled.Pattern, target.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(compiled.Pattern, target.NameWithExe, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(compiled.FileName, target.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(compiled.FileName, target.NameWithExe, StringComparison.OrdinalIgnoreCase);
            }

            return WildcardMatch(target.Path, compiled.Pattern)
                || WildcardMatch(target.FileName, compiled.Pattern)
                || WildcardMatch(target.Name, compiled.Pattern)
                || WildcardMatch(target.NameWithExe, compiled.Pattern);
        }

        public static bool WildcardMatch(string value, string pattern)
        {
            value ??= string.Empty;
            pattern ??= string.Empty;

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

        internal readonly record struct CompiledApplicationPattern(
            string Pattern,
            string FileName,
            bool HasWildcards);

        internal readonly record struct ApplicationMatchTarget(
            string Path,
            string FileName,
            string Name,
            string NameWithExe);
    }
}
