#nullable enable
using System;
using Shadowsocks.Controller.Traffic;

namespace Shadowsocks.Controller.Service
{
    public enum DnsCryptComponentStage
    {
        CheckingRelease,
        DownloadingArchive,
        DownloadingSignature,
        Verifying,
        Installing,
        Prepared,
        ValidatingRuntime,
        Activating,
        Starting,
        Removing,
        Ready,
    }

    public sealed record DnsCryptComponentProgress(
        DnsCryptComponentStage Stage,
        long BytesTransferred = 0,
        long? TotalBytes = null);

    public sealed record DnsCryptReleaseInfo(
        Version Version,
        string TagName,
        string ArchiveName,
        Uri ArchiveDownloadUri,
        string SignatureName,
        Uri SignatureDownloadUri,
        long ArchiveSize,
        string Sha256Digest);

    public sealed record DnsCryptPreparedComponent(
        Version Version,
        string DirectoryPath,
        string ExecutablePath);

    public sealed record DnsCryptComponentStatus(
        bool IsInstalled,
        Version? ActiveVersion,
        Version? PreviousVersion,
        string? ExecutablePath,
        bool AutoUpdate,
        DateTimeOffset? LastUpdateCheckUtc);

    public sealed record DnsPrivacySelfTestResult(
        bool Passed,
        bool RuntimeHealthy,
        bool AdminInterceptionActive,
        bool PlaintextFallbackBlocked,
        bool PlaintextBootstrapDisabled,
        bool SystemDnsIgnored,
        bool AutomaticResolverPinned,
        bool RouteThroughShadowsocks,
        string Upstream,
        string Transport,
        System.Collections.Generic.IReadOnlyList<string> Failures);

    public sealed record DnsCryptManagementStatus(
        DnsPolicyMode Mode,
        DnsCryptComponentStatus Component,
        DnsCryptRuntimeStatus Runtime,
        Version? LatestVersion,
        string? LastMaintenanceError = null,
        bool AutomaticResolvers = true,
        System.Collections.Generic.IReadOnlyList<DnsCryptResolverInfo>? ActiveResolvers = null)
    {
        public bool UpdateAvailable => Component?.ActiveVersion is not null
            && LatestVersion is not null
            && LatestVersion > Component.ActiveVersion;
    }
}
