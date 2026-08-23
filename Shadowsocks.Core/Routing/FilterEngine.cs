#nullable enable

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

#if SHADOWSOCKS_NETWORKSERVICE_FILTER_ENGINE
namespace Shadowsocks.NetworkService.ManagedRouting
#else
namespace Shadowsocks.Routing
#endif
{
    /// <summary>
    /// Network-routing result produced by the managed ABP/EasyList-compatible matcher.
    /// The engine intentionally models only the DIRECT/PROXY subset used by Shadowsocks
    /// PAC routing; browser cosmetic filtering is outside this contract.
    /// </summary>
    public enum FilterRoutingAction
    {
        Direct = 0,
        Proxy = 1,
    }

    public enum FilterRuleSource
    {
        Fallback = 0,
        PrivateNetwork = 1,
        User = 2,
        Default = 3,
    }

    public readonly record struct FilterRoutingDecision(
        FilterRoutingAction Action,
        FilterRuleSource Source,
        string? MatchedRule)
    {
        public bool IsMatch => Source is FilterRuleSource.User or FilterRuleSource.Default;

        public static FilterRoutingDecision Direct(FilterRuleSource source, string? matchedRule = null)
            => new(FilterRoutingAction.Direct, source, matchedRule);

        public static FilterRoutingDecision Proxy(FilterRuleSource source, string matchedRule)
            => new(FilterRoutingAction.Proxy, source, matchedRule);
    }

    public sealed record InvalidFilterRule(string Text, string Reason);

    public sealed class FilterCompilationReport
    {
        internal FilterCompilationReport(
            int defaultRuleCount,
            int userRuleCount,
            int optimizedDomainRuleCount,
            int unindexedRuleCount,
            IReadOnlyList<InvalidFilterRule> invalidRules)
        {
            DefaultRuleCount = defaultRuleCount;
            UserRuleCount = userRuleCount;
            OptimizedDomainRuleCount = optimizedDomainRuleCount;
            UnindexedRuleCount = unindexedRuleCount;
            InvalidRules = invalidRules;
        }

        public int DefaultRuleCount { get; }
        public int UserRuleCount { get; }
        public int OptimizedDomainRuleCount { get; }
        public int UnindexedRuleCount { get; }
        public IReadOnlyList<InvalidFilterRule> InvalidRules { get; }
        public int InvalidRuleCount => InvalidRules.Count;
    }

    /// <summary>
    /// Immutable, thread-safe managed matcher for the network-filter subset previously
    /// provided by the historical ABP-compatible routing path. User rules are evaluated before generated/default rules;
    /// within each rule set, exception rules (@@) win over blocking rules. A blocking rule
    /// routes through Shadowsocks, an exception routes DIRECT, and no match falls back DIRECT.
    /// </summary>
    public sealed class FilterEngine
    {
        private const int MaxCacheEntries = 1024;
        private static readonly TimeSpan s_ruleRegexTimeout = TimeSpan.FromMilliseconds(100);

        private static readonly Regex s_optionsRegex = new(
            @"\$(?<options>~?[\w-]+(?:=[^,\s]+)?(?:,~?[\w-]+(?:=[^,\s]+)?)*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly SearchValues<char> s_invalidSimpleDomainCharacters = SearchValues.Create("/*^|:");

        private static readonly FrozenSet<string> s_acceptedCompatibilityOptions = new[]
        {
            "other", "script", "image", "stylesheet", "object", "subdocument", "document",
            "xbl", "ping", "xmlhttprequest", "object-subrequest", "dtd", "media", "font",
            "background", "popup", "elemhide", "third-party", "collapse",
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        private readonly RuleMatcher _userMatcher;
        private readonly RuleMatcher _defaultMatcher;
        private readonly ConcurrentDictionary<CacheKey, FilterRoutingDecision> _resultCache = new();

        private FilterEngine(RuleMatcher userMatcher, RuleMatcher defaultMatcher)
        {
            _userMatcher = userMatcher;
            _defaultMatcher = defaultMatcher;
        }

        public static FilterEngine Compile(
            IEnumerable<string> defaultRules,
            IEnumerable<string> userRules,
            out FilterCompilationReport report)
        {
            List<InvalidFilterRule> invalidRules = [];
            RuleMatcher defaultMatcher = RuleMatcher.Compile(
                defaultRules, "default", invalidRules, out int defaultCount, out int defaultOptimized, out int defaultUnindexed);
            RuleMatcher userMatcher = RuleMatcher.Compile(
                userRules, "user", invalidRules, out int userCount, out int userOptimized, out int userUnindexed);
            report = new FilterCompilationReport(
                defaultCount,
                userCount,
                defaultOptimized + userOptimized,
                defaultUnindexed + userUnindexed,
                invalidRules.AsReadOnly());
            return new FilterEngine(userMatcher, defaultMatcher);
        }

        public static FilterEngine Compile(IEnumerable<string> defaultRules, IEnumerable<string> userRules)
            => Compile(defaultRules, userRules, out _);

        /// <summary>
        /// Reads an EasyList/ABP-style text file for the Shadowsocks user-rule contract:
        /// blank lines, comments (!) and section headers ([...]) are ignored, and surrounding
        /// whitespace is normalized before a rule is compiled.
        /// </summary>
        public static IReadOnlyList<string> ParseRuleLines(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return Array.Empty<string>();
            }

            List<string> rules = [];
            using System.IO.StringReader reader = new(content);
            for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('!') || trimmed.StartsWith('['))
                {
                    continue;
                }

                rules.Add(trimmed);
            }

            return rules;
        }

        public FilterRoutingDecision Evaluate(string url, string? host = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(url);

            string normalizedHost = NormalizeHost(url, host);
            if (IsLocalBypassIpv4(normalizedHost))
            {
                return FilterRoutingDecision.Direct(FilterRuleSource.PrivateNetwork);
            }

            CacheKey cacheKey = new(url, normalizedHost);
            if (_resultCache.TryGetValue(cacheKey, out FilterRoutingDecision cached))
            {
                return cached;
            }

            FilterRoutingDecision decision = EvaluateUncached(url, normalizedHost);
            if (_resultCache.Count >= MaxCacheEntries)
            {
                _resultCache.Clear();
            }
            _resultCache.TryAdd(cacheKey, decision);
            return decision;
        }

        private FilterRoutingDecision EvaluateUncached(string url, string host)
        {
            if (_userMatcher.TryMatch(url, host, out CompiledFilterRule? userRule))
            {
                return userRule.IsException
                    ? FilterRoutingDecision.Direct(FilterRuleSource.User, userRule.Text)
                    : FilterRoutingDecision.Proxy(FilterRuleSource.User, userRule.Text);
            }

            if (_defaultMatcher.TryMatch(url, host, out CompiledFilterRule? defaultRule))
            {
                return defaultRule.IsException
                    ? FilterRoutingDecision.Direct(FilterRuleSource.Default, defaultRule.Text)
                    : FilterRoutingDecision.Proxy(FilterRuleSource.Default, defaultRule.Text);
            }

            return FilterRoutingDecision.Direct(FilterRuleSource.Fallback);
        }

        private static string NormalizeHost(string url, string? host)
        {
            string normalized = host?.Trim().TrimEnd('.') ?? string.Empty;
            if (normalized.Length > 0)
            {
                return normalized;
            }

            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            {
                return uri.Host.TrimEnd('.');
            }

            return string.Empty;
        }

        private static bool IsLocalBypassIpv4(string host)
        {
            if (!IPAddress.TryParse(host, out IPAddress? address)
                || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return false;
            }

            Span<byte> bytes = stackalloc byte[4];
            if (!address.TryWriteBytes(bytes, out int bytesWritten) || bytesWritten != bytes.Length)
            {
                return false;
            }

            return bytes[0] == 10
                || bytes[0] == 127
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 168);
        }

        private static bool IsKeywordCharacter(char character)
            => character is >= 'a' and <= 'z'
                or >= 'A' and <= 'Z'
                or >= '0' and <= '9'
                or '%';

        private static string CreateLowerAsciiKeyword(string text, int start, int length)
            => string.Create(length, (text, start), static (destination, state) =>
            {
                for (int i = 0; i < destination.Length; i++)
                {
                    char character = state.text[state.start + i];
                    destination[i] = character is >= 'A' and <= 'Z'
                        ? (char)(character + ('a' - 'A'))
                        : character;
                }
            });

        private readonly record struct CacheKey(string Url, string Host);

        private sealed class RuleMatcher
        {
            private readonly FrozenDictionary<string, CompiledFilterRule[]> _whitelistDomains;
            private readonly FrozenDictionary<string, CompiledFilterRule[]> _blockingDomains;
            private readonly FrozenDictionary<string, CompiledFilterRule[]> _whitelistByKeyword;
            private readonly FrozenDictionary<string, CompiledFilterRule[]> _blockingByKeyword;

            private RuleMatcher(
                FrozenDictionary<string, CompiledFilterRule[]> whitelistDomains,
                FrozenDictionary<string, CompiledFilterRule[]> blockingDomains,
                FrozenDictionary<string, CompiledFilterRule[]> whitelistByKeyword,
                FrozenDictionary<string, CompiledFilterRule[]> blockingByKeyword)
            {
                _whitelistDomains = whitelistDomains;
                _blockingDomains = blockingDomains;
                _whitelistByKeyword = whitelistByKeyword;
                _blockingByKeyword = blockingByKeyword;
            }

            public static RuleMatcher Compile(
                IEnumerable<string>? sourceRules,
                string sourceName,
                List<InvalidFilterRule> invalidRules,
                out int validCount,
                out int optimizedDomainCount,
                out int unindexedCount)
            {
                Dictionary<string, List<CompiledFilterRule>> whitelistDomains = new(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, List<CompiledFilterRule>> blockingDomains = new(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, List<CompiledFilterRule>> whitelist = new(StringComparer.Ordinal);
                Dictionary<string, List<CompiledFilterRule>> blocking = new(StringComparer.Ordinal);
                HashSet<string> seen = new(StringComparer.Ordinal);
                validCount = 0;
                optimizedDomainCount = 0;
                unindexedCount = 0;

                foreach (string? rawRule in sourceRules ?? Array.Empty<string>())
                {
                    string? text = rawRule?.Trim();
                    if (string.IsNullOrEmpty(text) || text.StartsWith('!') || text.StartsWith('['))
                    {
                        continue;
                    }
                    if (!seen.Add(text))
                    {
                        continue;
                    }

                    if (!CompiledFilterRule.TryCreate(text, out CompiledFilterRule? rule, out string? reason))
                    {
                        invalidRules.Add(new InvalidFilterRule(text, $"{sourceName}: {reason ?? "invalid rule"}"));
                        continue;
                    }

                    if (rule.HostSuffix is string hostSuffix)
                    {
                        Dictionary<string, List<CompiledFilterRule>> domainIndex =
                            rule.IsException ? whitelistDomains : blockingDomains;
                        if (!domainIndex.TryGetValue(hostSuffix, out List<CompiledFilterRule>? domainBucket) || domainBucket is null)
                        {
                            domainBucket = [];
                            domainIndex.Add(hostSuffix, domainBucket);
                        }
                        domainBucket.Add(rule);
                        optimizedDomainCount++;
                    }
                    else
                    {
                        Dictionary<string, List<CompiledFilterRule>> index = rule.IsException ? whitelist : blocking;
                        if (!index.TryGetValue(rule.Keyword, out List<CompiledFilterRule>? bucket) || bucket is null)
                        {
                            bucket = [];
                            index.Add(rule.Keyword, bucket);
                        }
                        bucket.Add(rule);
                        if (rule.Keyword.Length == 0)
                        {
                            unindexedCount++;
                        }
                    }
                    validCount++;
                }

                return new RuleMatcher(
                    FreezeIndex(whitelistDomains, StringComparer.OrdinalIgnoreCase),
                    FreezeIndex(blockingDomains, StringComparer.OrdinalIgnoreCase),
                    FreezeIndex(whitelist, StringComparer.Ordinal),
                    FreezeIndex(blocking, StringComparer.Ordinal));
            }

            public bool TryMatch(string url, string host, [NotNullWhen(true)] out CompiledFilterRule? matched)
            {
                // Match CombinedMatcher semantics: any matching exception wins over any
                // blocking rule in the same rule set. Simple ||domain^ filters use a
                // dedicated suffix index; complex filters use the ABP keyword index.
                if (TryMatchDomainIndex(_whitelistDomains, url, host, out matched)
                    || TryMatchKeywordIndex(_whitelistByKeyword, url, host, out matched))
                {
                    return true;
                }

                return TryMatchDomainIndex(_blockingDomains, url, host, out matched)
                    || TryMatchKeywordIndex(_blockingByKeyword, url, host, out matched);
            }

            private static FrozenDictionary<string, CompiledFilterRule[]> FreezeIndex(
                Dictionary<string, List<CompiledFilterRule>> source,
                StringComparer comparer)
                => source.ToFrozenDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToArray(),
                    comparer);

            private static bool TryMatchDomainIndex(
                FrozenDictionary<string, CompiledFilterRule[]> index,
                string url,
                string host,
                [NotNullWhen(true)] out CompiledFilterRule? matched)
            {
                string candidate = host.Trim().TrimEnd('.');
                while (candidate.Length > 0)
                {
                    if (index.TryGetValue(candidate, out CompiledFilterRule[]? bucket) && bucket is not null)
                    {
                        foreach (CompiledFilterRule rule in bucket)
                        {
                            if (rule.Matches(url, host))
                            {
                                matched = rule;
                                return true;
                            }
                        }
                    }

                    int dot = candidate.IndexOf('.');
                    if (dot < 0)
                    {
                        break;
                    }
                    candidate = candidate[(dot + 1)..];
                }

                matched = null;
                return false;
            }

            private static bool TryMatchKeywordIndex(
                FrozenDictionary<string, CompiledFilterRule[]> index,
                string url,
                string host,
                [NotNullWhen(true)] out CompiledFilterRule? matched)
            {
                return TryMatchKeywordsInText(index, url, url, host, out matched)
                    || TryMatchKeywordsInText(index, host, url, host, out matched)
                    || TryMatchKeyword(index, string.Empty, url, host, out matched);
            }

            private static bool TryMatchKeywordsInText(
                FrozenDictionary<string, CompiledFilterRule[]> index,
                string text,
                string url,
                string host,
                [NotNullWhen(true)] out CompiledFilterRule? matched)
            {
                if (string.IsNullOrEmpty(text))
                {
                    matched = null;
                    return false;
                }

                int tokenStart = -1;
                for (int indexInText = 0; indexInText <= text.Length; indexInText++)
                {
                    bool isKeywordCharacter = indexInText < text.Length && IsKeywordCharacter(text[indexInText]);
                    if (isKeywordCharacter)
                    {
                        tokenStart = tokenStart < 0 ? indexInText : tokenStart;
                        continue;
                    }

                    if (tokenStart < 0)
                    {
                        continue;
                    }

                    int tokenLength = indexInText - tokenStart;
                    if (tokenLength >= 3)
                    {
                        string keyword = CreateLowerAsciiKeyword(text, tokenStart, tokenLength);
                        if (TryMatchKeyword(index, keyword, url, host, out matched))
                        {
                            return true;
                        }
                    }

                    tokenStart = -1;
                }

                matched = null;
                return false;
            }

            private static bool TryMatchKeyword(
                FrozenDictionary<string, CompiledFilterRule[]> index,
                string keyword,
                string url,
                string host,
                [NotNullWhen(true)] out CompiledFilterRule? matched)
            {
                if (index.TryGetValue(keyword, out CompiledFilterRule[]? bucket) && bucket is not null)
                {
                    foreach (CompiledFilterRule rule in bucket)
                    {
                        if (rule.Matches(url, host))
                        {
                            matched = rule;
                            return true;
                        }
                    }
                }

                matched = null;
                return false;
            }

        }

        private sealed class CompiledFilterRule
        {
            private static readonly string s_separatorPattern =
                @"(?:[\x00-\x24\x26-\x2C\x2F\x3A-\x40\x5B-\x5E\x60\x7B-\x7F]|$)";

            private readonly Regex? _regex;
            private readonly string? _hostSuffix;
            private readonly DomainPolicy? _domains;
            private readonly bool _requiresSiteKey;

            private CompiledFilterRule(
                string text,
                bool isException,
                Regex? regex,
                string? hostSuffix,
                DomainPolicy? domains,
                string keyword,
                bool requiresSiteKey)
            {
                Text = text;
                IsException = isException;
                _regex = regex;
                _hostSuffix = hostSuffix;
                _domains = domains;
                Keyword = keyword;
                _requiresSiteKey = requiresSiteKey;
            }

            public string Text { get; }
            public bool IsException { get; }
            public string Keyword { get; }
            public string? HostSuffix => _hostSuffix;
            public bool IsOptimizedDomain => _hostSuffix is not null;

            public bool Matches(string url, string host)
            {
                if (_requiresSiteKey || !(_domains?.IsActiveOn(host) ?? true))
                {
                    return false;
                }

                if (_hostSuffix is not null)
                {
                    if (host.Equals(_hostSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    int suffixStart = host.Length - _hostSuffix.Length;
                    return suffixStart > 0
                        && host[suffixStart - 1] == '.'
                        && host.AsSpan(suffixStart).Equals(_hostSuffix, StringComparison.OrdinalIgnoreCase);
                }

                try
                {
                    return _regex?.IsMatch(url) == true;
                }
                catch (RegexMatchTimeoutException)
                {
                    // One pathological user rule must never stall or break routing.
                    return false;
                }
            }

            public static bool TryCreate(string originalText, [NotNullWhen(true)] out CompiledFilterRule? rule, out string? reason)
            {
                rule = null;
                reason = null;

                bool isException = originalText.StartsWith("@@", StringComparison.Ordinal);
                string body = isException ? originalText[2..] : originalText;
                body = NormalizeShadowsocksDomainRule(body);

                if (!TryParseOptions(
                    body,
                    out string patternText,
                    out string? domainSource,
                    out bool matchCase,
                    out bool requiresSiteKey,
                    out reason))
                {
                    return false;
                }

                if (patternText.Length == 0)
                {
                    reason = "empty filter pattern";
                    return false;
                }

                try
                {
                    RegexOptions regexOptions = RegexOptions.CultureInvariant;
                    if (!matchCase)
                    {
                        regexOptions |= RegexOptions.IgnoreCase;
                    }

                    string? hostSuffix = null;
                    Regex? regex = null;
                    if (!TryGetSimpleDomainAnchor(patternText, matchCase, out hostSuffix))
                    {
                        string regexPattern = IsExplicitRegex(patternText)
                            ? patternText[1..^1]
                            : ConvertAbpPatternToRegex(patternText);

                        // Complex rules are interpreted rather than RegexOptions.Compiled.
                        // GeoSite can contain tens of thousands of entries; generating native
                        // regex code per rule would create unnecessary startup/JIT pressure.
                        regex = new Regex(regexPattern, regexOptions, s_ruleRegexTimeout);
                    }

                    DomainPolicy? domains = DomainPolicy.Parse(domainSource);
                    string keyword = FindKeyword(originalText, IsExplicitRegex(patternText));
                    rule = new CompiledFilterRule(
                        originalText, isException, regex, hostSuffix, domains, keyword, requiresSiteKey);
                    return true;
                }
                catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
                {
                    reason = $"invalid regular expression: {exception.Message}";
                    return false;
                }
            }

            private static bool TryParseOptions(
                string body,
                out string patternText,
                out string? domainSource,
                out bool matchCase,
                out bool requiresSiteKey,
                out string? reason)
            {
                patternText = body;
                domainSource = null;
                matchCase = false;
                requiresSiteKey = false;
                reason = null;

                Match optionsMatch = s_optionsRegex.Match(body);
                if (!optionsMatch.Success)
                {
                    return true;
                }

                patternText = body[..optionsMatch.Index];
                foreach (string rawOption in optionsMatch.Groups["options"].Value.Split(','))
                {
                    string option = rawOption;
                    string? value = null;
                    int equals = option.IndexOf('=');
                    if (equals >= 0)
                    {
                        value = option[(equals + 1)..];
                        option = option[..equals];
                    }

                    if (option.Equals("match-case", StringComparison.OrdinalIgnoreCase))
                    {
                        matchCase = true;
                        continue;
                    }

                    if (option.Equals("~match-case", StringComparison.OrdinalIgnoreCase))
                    {
                        matchCase = false;
                        continue;
                    }

                    if (option.Equals("domain", StringComparison.OrdinalIgnoreCase) && value is not null)
                    {
                        domainSource = value;
                        continue;
                    }

                    if (option.Equals("sitekey", StringComparison.OrdinalIgnoreCase) && value is not null)
                    {
                        // Managed routing never supplies an ABP sitekey. Keep the rule valid
                        // but inactive, matching the network-routing compatibility contract.
                        requiresSiteKey = true;
                        continue;
                    }

                    string normalized = option.StartsWith('~') ? option[1..] : option;
                    if (s_acceptedCompatibilityOptions.Contains(normalized))
                    {
                        // Browser resource/cosmetic options do not alter this network-only
                        // DIRECT/PROXY decision, but accepting them keeps EasyList compatibility.
                        continue;
                    }

                    reason = $"unsupported option '{rawOption}'";
                    return false;
                }

                return true;
            }

            private static bool IsExplicitRegex(string pattern)
                => pattern.Length >= 2 && pattern[0] == '/' && pattern[^1] == '/';

            private static string NormalizeShadowsocksDomainRule(string body)
            {
                Match options = s_optionsRegex.Match(body);
                int patternLength = options.Success ? options.Index : body.Length;
                string pattern = body[..patternLength];
                if (!pattern.StartsWith("||", StringComparison.Ordinal)
                    || pattern.EndsWith('^')
                    || pattern.EndsWith('|'))
                {
                    return body;
                }

                // The historical Shadowsocks rule normalization appends a separator to ||domain rules
                // before giving them to the ABP matcher. Do it before options here so a
                // valid rule such as ||example.com$domain=site.test stays valid.
                return pattern + "^" + body[patternLength..];
            }

            private static bool TryGetSimpleDomainAnchor(string pattern, bool matchCase, [NotNullWhen(true)] out string? hostSuffix)
            {
                hostSuffix = null;
                if (matchCase
                    || !pattern.StartsWith("||", StringComparison.Ordinal)
                    || !pattern.EndsWith('^'))
                {
                    return false;
                }

                string candidate = pattern[2..^1];
                if (candidate.Length == 0
                    || candidate.AsSpan().IndexOfAny(s_invalidSimpleDomainCharacters) >= 0
                    || ContainsWhitespace(candidate))
                {
                    return false;
                }

                hostSuffix = candidate.TrimEnd('.');
                return hostSuffix.Length > 0;
            }

            private static string ConvertAbpPatternToRegex(string pattern)
            {
                // Preserve the ABP network-filter transformation while avoiding a temporary
                // normalized pattern allocation during rule compilation.
                if (pattern.EndsWith("^|", StringComparison.Ordinal))
                {
                    pattern = pattern[..^1];
                }

                bool domainAnchor = pattern.StartsWith("||", StringComparison.Ordinal);
                bool startAnchor = !domainAnchor && pattern.StartsWith('|');
                bool endAnchor = pattern.EndsWith('|');

                int start = domainAnchor ? 2 : startAnchor ? 1 : 0;
                int length = pattern.Length - start - (endAnchor ? 1 : 0);
                ReadOnlySpan<char> core = length > 0
                    ? pattern.AsSpan(start, length)
                    : ReadOnlySpan<char>.Empty;

                StringBuilder builder = new();
                if (domainAnchor)
                {
                    builder.Append(@"^[\w\-]+://+(?!/)(?:[^/]+\.)?");
                }
                else if (startAnchor)
                {
                    builder.Append('^');
                }

                bool previousWildcard = false;
                foreach (char character in core)
                {
                    switch (character)
                    {
                        case '*':
                            if (!previousWildcard)
                            {
                                builder.Append(".*");
                            }
                            previousWildcard = true;
                            break;
                        case '^':
                            builder.Append(s_separatorPattern);
                            previousWildcard = false;
                            break;
                        default:
                            builder.Append(Regex.Escape(character.ToString()));
                            previousWildcard = false;
                            break;
                    }
                }

                if (endAnchor)
                {
                    builder.Append('$');
                }

                string result = builder.ToString();
                if (!domainAnchor && !startAnchor && result.StartsWith(".*", StringComparison.Ordinal))
                {
                    result = result[2..];
                }
                if (!endAnchor && result.EndsWith(".*", StringComparison.Ordinal))
                {
                    result = result[..^2];
                }
                return result;
            }

            private static string FindKeyword(string originalText, bool explicitRegex)
            {
                if (explicitRegex)
                {
                    return string.Empty;
                }

                ReadOnlySpan<char> text = originalText.AsSpan();
                Match options = s_optionsRegex.Match(originalText);
                if (options.Success)
                {
                    text = text[..options.Index];
                }
                if (text.StartsWith("@@", StringComparison.Ordinal))
                {
                    text = text[2..];
                }

                int bestStart = -1;
                int bestLength = 0;
                int tokenStart = -1;
                for (int i = 0; i <= text.Length; i++)
                {
                    bool isKeywordCharacter = i < text.Length && IsKeywordCharacter(text[i]);
                    if (isKeywordCharacter)
                    {
                        tokenStart = tokenStart < 0 ? i : tokenStart;
                        continue;
                    }

                    if (tokenStart < 0)
                    {
                        continue;
                    }

                    int tokenLength = i - tokenStart;
                    bool hasValidLeftBoundary = tokenStart > 0
                        && !IsKeywordCharacter(text[tokenStart - 1])
                        && text[tokenStart - 1] != '*';
                    bool hasValidRightBoundary = i < text.Length
                        && !IsKeywordCharacter(text[i])
                        && text[i] != '*';
                    if (tokenLength >= 3
                        && hasValidLeftBoundary
                        && hasValidRightBoundary
                        && tokenLength > bestLength)
                    {
                        bestStart = tokenStart;
                        bestLength = tokenLength;
                    }

                    tokenStart = -1;
                }

                return bestStart >= 0
                    ? CreateLowerAsciiKeyword(originalText, bestStart + (originalText.StartsWith("@@", StringComparison.Ordinal) ? 2 : 0), bestLength)
                    : string.Empty;
            }

            private static bool ContainsWhitespace(string value)
            {
                foreach (char character in value)
                {
                    if (char.IsWhiteSpace(character))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private sealed class DomainPolicy
        {
            private readonly FrozenDictionary<string, bool> _domains;
            private readonly bool _defaultInclude;

            private DomainPolicy(FrozenDictionary<string, bool> domains, bool defaultInclude)
            {
                _domains = domains;
                _defaultInclude = defaultInclude;
            }

            public static DomainPolicy? Parse(string? source)
            {
                if (string.IsNullOrWhiteSpace(source))
                {
                    return null;
                }

                Dictionary<string, bool> domains = new(StringComparer.OrdinalIgnoreCase);
                bool hasIncludes = false;
                foreach (string raw in source.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    bool include = !raw.StartsWith('~');
                    string domain = include ? raw : raw[1..];
                    domain = domain.Trim().TrimEnd('.');
                    if (domain.Length == 0)
                    {
                        continue;
                    }
                    hasIncludes |= include;
                    domains[domain] = include;
                }
                return new DomainPolicy(
                    domains.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
                    !hasIncludes);
            }

            public bool IsActiveOn(string host)
            {
                if (string.IsNullOrWhiteSpace(host))
                {
                    return _defaultInclude;
                }

                string candidate = host.Trim().TrimEnd('.');
                while (candidate.Length > 0)
                {
                    if (_domains.TryGetValue(candidate, out bool included))
                    {
                        return included;
                    }

                    int dot = candidate.IndexOf('.');
                    if (dot < 0)
                    {
                        break;
                    }
                    candidate = candidate[(dot + 1)..];
                }

                return _defaultInclude;
            }
        }
    }
}
