using System;
using System.IO;

namespace Shadowsocks.Controller.Traffic
{
    internal static class ApplicationPatternMatcher
    {
        public static bool Matches(string pattern, string processPath, string processName)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                return false;
            }

            pattern = pattern.Trim();
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
    }
}
