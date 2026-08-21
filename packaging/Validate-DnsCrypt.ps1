[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot

function Read-RepoText {
    param([Parameter(Mandatory)][string]$RelativePath)
    $path = Join-Path $repoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "DNSCrypt release validation is missing required file: $RelativePath"
    }
    return Get-Content -LiteralPath $path -Raw
}

$componentSource = Read-RepoText 'Shadowsocks.Windows\Controller\Service\DnsCryptComponentManager.cs'
$componentPackageSource = Read-RepoText 'Shadowsocks.Windows\Controller\Service\DnsCryptComponentManager.Package.cs'
$runtimeSource = Read-RepoText 'Shadowsocks.Windows\Controller\Service\DnsCryptRuntimeManager.cs'
$maintenancePolicySource = Read-RepoText 'Shadowsocks.Windows\Controller\Service\DnsCryptMaintenancePolicy.cs'
$maintenanceSource = Read-RepoText 'Shadowsocks.Windows\Controller\ShadowsocksController.DnsCrypt.Maintenance.cs'
$bootstrapSource = Read-RepoText 'Shadowsocks.Windows\Controller\Service\DnsCryptBootstrapPolicy.cs'
$controllerDnsSource = Read-RepoText 'Shadowsocks.Windows\Controller\ShadowsocksController.DnsCrypt.cs'
$controllerSource = Read-RepoText 'Shadowsocks.Windows\Controller\ShadowsocksController.cs'
$storageSource = Read-RepoText 'Shadowsocks.Core\Storage\AppStoragePaths.cs'
$trafficModelsSource = Read-RepoText 'Shadowsocks.Core\Controller\Traffic\TrafficModels.cs'
$notices = Read-RepoText 'docs\THIRD-PARTY-NOTICES.md'
$coreProjectText = Read-RepoText 'Shadowsocks.Core\Shadowsocks.Core.csproj'
$windowsProjectText = Read-RepoText 'Shadowsocks.Windows\Shadowsocks.Windows.csproj'
$testsProjectText = Read-RepoText 'Shadowsocks.UnitTests\Shadowsocks.UnitTests.csproj'
$coordinatorSource = Read-RepoText 'Shadowsocks.Windows\Controller\Service\DnsCryptCoordinator.cs'
$winDivertInstallerSource = Read-RepoText 'Shadowsocks.Windows\Controller\Traffic\WinDivertInstaller.cs'
$fragmentTrackerSource = Read-RepoText 'Shadowsocks.NetworkService\Routing\IpFragmentTracker.cs'
$routerSource = Read-RepoText 'Shadowsocks.NetworkService\Routing\WinDivertTransparentRouter.cs'
$flowObserverSource = Read-RepoText 'Shadowsocks.NetworkService\Routing\WinDivertFlowObserver.cs'
$processAttributionSource = Read-RepoText 'Shadowsocks.NetworkService\Routing\ProcessAttributionCache.cs'
$processResolverSource = Read-RepoText 'Shadowsocks.NetworkService\Routing\ProcessResolver.cs'
$cleanSessionSource = Read-RepoText 'Shadowsocks.Core\Storage\CleanStorageSession.cs'
$dnsPolicyConfigSource = Read-RepoText 'Shadowsocks.Core\Controller\Traffic\DnsPolicyConfig.cs'
$dnsPageSource = Read-RepoText 'Shadowsocks.WinUI\Pages\DnsPage.cs'
$dohRelaySource = Read-RepoText 'Shadowsocks.NetworkService\Routing\TransparentDohRelay.cs'
$dnsRelaySource = Read-RepoText 'Shadowsocks.NetworkService\Routing\TransparentDnsRelay.cs'
$resolverLatencySource = Read-RepoText 'Shadowsocks.Windows\Controller\Service\DnsCryptResolverLatencyProbe.cs'
$dnsCryptCountrySelectorSource = Read-RepoText 'Shadowsocks.Windows\Controller\Service\DnsCryptCountrySelector.cs'

foreach ($token in @(
    'public const string Repository = "DNSCrypt/dnscrypt-proxy";',
    'dnscrypt-proxy-win64-',
    'ReleaseSigningPublicKey',
    'PrepareLatestAsync',
    'PrepareReleaseAsync',
    'ActivatePrepared',
    'CommitActivation',
    'RollbackActivation')) {
    if ($componentSource -notmatch [regex]::Escape($token)) {
        throw "DNSCrypt component invariant is missing: $token"
    }
}
if ($componentSource -match '\bInstallLatestAsync\b') {
    throw 'DNSCrypt must not expose the old download-and-activate InstallLatestAsync shortcut.'
}
if ($componentPackageSource -notmatch 'Shadowsocks-Reborn/\{ApplicationInfo\.Version\}') {
    throw 'DNSCrypt HTTP User-Agent must derive from ApplicationInfo.Version instead of a stale hard-coded release number.'
}

if ($maintenancePolicySource -notmatch 'MinimumAutomaticCheckInterval\s*=\s*TimeSpan\.FromHours\(24\)') {
    throw 'DNSCrypt automatic update checks must keep a persisted minimum 24-hour interval.'
}
if ($maintenancePolicySource -notmatch 'ClockSkewTolerance\s*=\s*TimeSpan\.FromMinutes\(10\)' -or
    $maintenancePolicySource -notmatch 'normalizedLast > normalizedNow \+ ClockSkewTolerance') {
    throw 'DNSCrypt automatic maintenance must repair implausible future timestamps instead of suppressing updates indefinitely.'
}
$recordIndex = $maintenanceSource.IndexOf('_dnsCryptComponentManager.RecordUpdateCheck(nowUtc)')
$requestIndex = $maintenanceSource.IndexOf('.GetLatestReleaseAsync(cancellationToken)')
if ($recordIndex -lt 0 -or $requestIndex -lt 0 -or $recordIndex -gt $requestIndex) {
    throw 'Automatic DNSCrypt maintenance must persist the update-check attempt before the GitHub request to prevent retry storms.'
}
if ($maintenanceSource -notmatch 'UpdateDnsCryptReleaseCoreAsync' -or
    $controllerDnsSource -notmatch 'PrepareReleaseAsync\(release') {
    throw 'Automatic/manual DNSCrypt updates must consume the exact release selected by the already-completed check.'
}

if ($bootstrapSource -notmatch 'IPAddress\.TryParse' -or
    $controllerDnsSource -notmatch 'DnsCryptBootstrapPolicy\.Validate' -or
    $controllerSource -notmatch 'DNS bootstrap recursion loop') {
    throw 'DNSCrypt-over-Shadowsocks must enforce bootstrap-recursion protection.'
}

foreach ($token in @(
    'DnsCryptComponentDirectory',
    'DnsCryptRuntimeDirectory',
    'DnsCryptUpdateDirectory')) {
    if ($storageSource -notmatch [regex]::Escape($token)) {
        throw "DNSCrypt storage policy is missing: $token"
    }
}
if ($storageSource -match 'DNSCryptProxy.*AppContext\.BaseDirectory') {
    throw 'DNSCrypt writable state must never be rooted beside the executable.'
}

if ($trafficModelsSource -notmatch 'DnsCrypt\s*=\s*4') {
    throw 'DnsPolicyMode.DnsCrypt must keep its backward-compatible numeric value 4.'
}

foreach ($token in @(
    'directDnsServer',
    'customDohRouteThroughShadowsocks')) {
    if ($dnsPolicyConfigSource -notmatch [regex]::Escape($token)) {
        throw "DNS policy persistence is missing: $token"
    }
}
foreach ($token in @(
    'DNS server address',
    'Route Custom DoH through Shadowsocks',
    'Resolver selection',
    'Protocol',
    'Country',
    'Address family',
    'StartResolverLatencyRefresh')) {
    if ($dnsPageSource -notmatch [regex]::Escape($token)) {
        throw "DNS page resolver/routing UI invariant is missing: $token"
    }
}
if ($dohRelaySource -notmatch 'socks5://' -or $dohRelaySource -notmatch 'UseProxy = routeThroughShadowsocks') {
    throw 'Custom DoH must preserve optional routing through the local Shadowsocks SOCKS5 endpoint.'
}
if ($dnsRelaySource -notmatch 'SelectUdpBindAddress' -or $dnsRelaySource -notmatch 'IPAddress.IPv6Any') {
    throw 'Direct custom DNS must support remote IPv4/IPv6 UDP upstreams without loopback source binding.'
}
if ($resolverLatencySource -notmatch 'ProbeAsync' -or $resolverLatencySource -notmatch 'ConnectAsync') {
    throw 'Manual DNSCrypt resolver latency must remain an asynchronous non-blocking probe.'
}
if ($runtimeSource -notmatch 'GetActiveResolverNames' -or $runtimeSource -notmatch 'GetResolverLatencies') {
    throw 'DNSCrypt runtime must expose active resolver names and measured RTT to the UI.'
}

$dnsCryptControllerSource = Read-RepoText 'Shadowsocks.Windows\Controller\ShadowsocksController.DnsCrypt.cs'
$dnsCryptTomlSource = Read-RepoText 'Shadowsocks.Windows\Controller\Service\DnsCryptTomlGenerator.cs'
$ipCountrySource = Read-RepoText 'Shadowsocks.Windows\Controller\Service\IpCountryService.cs'
foreach ($automaticSelectionToken in @(
    'DnsCryptCountrySelector.SelectAsync',
    'DnsCryptCountrySelector.SelectFallback',
    'snapshot.serverNames = selected.ToList()')) {
    if ($dnsCryptControllerSource -notmatch [regex]::Escape($automaticSelectionToken)) {
        throw "DNSCrypt Automatic mode must resolve a filtered server set from the signed catalog: $automaticSelectionToken"
    }
}
foreach ($automaticFilterToken in @(
    'config.requireDnssec',
    'config.requireNoLog',
    'config.requireNoFilter',
    'config.ipv4Servers',
    'config.ipv6Servers')) {
    if ($dnsCryptCountrySelectorSource -notmatch [regex]::Escape($automaticFilterToken)) {
        throw "DNSCrypt Automatic resolver selection is missing a configured filter: $automaticFilterToken"
    }
}
if ($dnsCryptControllerSource -match 'pinned to Cloudflare' -or $dnsCryptControllerSource -match 'Cloudflare-only') {
    throw 'DNSCrypt Automatic mode must not bypass configured resolver filters by pinning the runtime to Cloudflare.'
}
if ($dnsCryptControllerSource -match [regex]::Escape('StartDnsCryptWithAutomaticFallbackAsync')) {
    throw 'Legacy DNSCrypt startup method reference remains after the resolved-resolver startup refactor.'
}
if ($dnsCryptControllerSource -notmatch 'LookupAddressesAsync') {
    throw 'DNSCrypt resolver country metadata must be resolved from endpoint IPs through GeoIP.'
}
foreach ($forbiddenCountryInferenceToken in @(
    'InferCountryFromText',
    'KnownLocationCountries',
    'HasCountryHint')) {
    if ($dnsCryptControllerSource -match [regex]::Escape($forbiddenCountryInferenceToken)
        -or $dnsCryptCountrySelectorSource -match [regex]::Escape($forbiddenCountryInferenceToken)
        -or $ipCountrySource -match [regex]::Escape($forbiddenCountryInferenceToken)) {
        throw "DNSCrypt resolver country selection must not infer geography from resolver text: $forbiddenCountryInferenceToken"
    }
}
if ($dnsCryptControllerSource -match 'CountryName\s*=\s*"Anycast"'
    -or $dnsPageSource -match 'CountryName\s*==\s*"Anycast"') {
    throw 'Anycast must not be represented as a pseudo-country in DNSCrypt resolver metadata.'
}
if ($dnsCryptTomlSource -notmatch 'doh_servers = true' -or $dnsCryptTomlSource -notmatch 'odoh_servers = false') {
    throw 'DNSCrypt Proxy must allow signed DoH resolvers while keeping ODoH disabled.'
}
if ($dnsPageSource -notmatch 'ResolverProtocolFilter' -or $dnsPageSource -notmatch 'All protocols') {
    throw 'Manual DNSCrypt resolver selection must keep the DNSCrypt/DoH protocol dropdown filter.'
}
foreach ($privacyToken in @(
    'bootstrap_resolvers = []',
    'RuntimeUsesPlaintextBootstrap',
    'A DNSCrypt runtime requires at least one resolver selected from the signed catalog.')) {
    if ($dnsCryptTomlSource -notmatch [regex]::Escape($privacyToken)) {
        throw "DNSCrypt active-runtime privacy invariant is missing: $privacyToken"
    }
}
foreach ($forbiddenFallbackToken in @('CloudflareIpv4Stamp', 'CloudflareIpv6Stamp', '[static.cloudflare]', 'GetAutomaticCloudflareServerNames')) {
    if ($dnsCryptTomlSource -match [regex]::Escape($forbiddenFallbackToken)) {
        throw "DNSCrypt Automatic mode must not retain a provider-specific static fallback: $forbiddenFallbackToken"
    }
}
foreach ($preparedValidationToken in @(
    'ResolvePreparedValidationOptionsAsync',
    'DnsCryptTomlPurpose.ResolverCatalog',
    '["-list-all", "-json", "-config", catalogConfigPath]',
    'DnsCryptCountrySelector.SelectFallback')) {
    if ($runtimeSource -notmatch [regex]::Escape($preparedValidationToken)) {
        throw "Prepared Automatic DNSCrypt validation must resolve a filtered resolver from the signed catalog: $preparedValidationToken"
    }
}
if ($dnsCryptTomlSource -notmatch 'DnsCryptTomlPurpose\.ResolverCatalog' -or
    $dnsCryptTomlSource -notmatch "bootstrap_resolvers = \['9\.9\.9\.11:53', '8\.8\.8\.8:53'\]") {
    throw 'Only the explicit resolver-catalog profile may retain one-shot plaintext bootstrap DNS.'
}
if ($runtimeSource -notmatch 'PersistResolverSourceCache' -or
    $dnsCryptControllerSource -notmatch 'TestDnsPrivacyAsync' -or
    $dnsPageSource -notmatch 'Run privacy self-test') {
    throw 'DNS privacy self-test and signed resolver-cache reuse must remain wired end to end.'
}

foreach ($projectText in @($windowsProjectText, $testsProjectText)) {
    if ($projectText -notmatch 'BouncyCastle\.Cryptography"\s+Version="2\.7\.0"') {
        throw 'BouncyCastle.Cryptography 2.7.0 must remain pinned for managed Minisign verification/tests.'
    }
}
foreach ($noticeToken in @('Bouncy Castle Cryptography for .NET', 'dnscrypt-proxy', 'License: ISC', 'WinDivert', 'Windows App SDK')) {
    if ($notices -notmatch [regex]::Escape($noticeToken)) {
        throw "THIRD-PARTY-NOTICES.md is missing: $noticeToken"
    }
}
if ($coreProjectText -notmatch 'Shadowsocks\.Core\.THIRD-PARTY-NOTICES\.md') {
    throw 'The one-file product must embed THIRD-PARTY-NOTICES.md so the bundled Bouncy Castle notice ships inside Shadowsocks.exe.'
}
if ($coreProjectText -notmatch 'Shadowsocks\.Core\.LICENSE\.txt') {
    throw 'The one-file product must embed LICENSE.txt so the product license remains accessible from Shadowsocks.exe.'
}
if ($notices -notmatch 'modified Bzip2 library' -or $notices -notmatch 'Apache License 2\.0') {
    throw 'THIRD-PARTY-NOTICES.md must preserve the Bouncy Castle modified-Bzip2 Apache-2.0 notice.'
}

if ($coordinatorSource -notmatch 'SuspendAndExecuteAsync' -or
    $coordinatorSource -notmatch '_lifecycleCancellation' -or
    $controllerSource -notmatch '_dnsCryptCoordinator\.Resume\(\)' -or
    $controllerSource -notmatch 'SuspendAndExecuteAsync') {
    throw 'DNSCrypt shutdown must cancel active/queued management transactions and resume the coordinator on the next Start lifecycle.'
}

foreach ($token in @(
    'https://github.com/basil00/WinDivert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip',
    'HttpCompletionOption.ResponseHeadersRead',
    'MaxPackageBytes',
    'WinDivert-{Version}-A/x64/',
    'ValidateX64PortableExecutable',
    'DllSha256',
    'DriverSha256',
    'ValidatePinnedRuntimeFile',
    'SHA256.HashData',
    'CryptographicOperations.FixedTimeEquals')) {
    if ($winDivertInstallerSource -notmatch [regex]::Escape($token)) {
        throw "WinDivert runtime hardening invariant is missing: $token"
    }
}
if ($winDivertInstallerSource -notmatch 'c1e060ee19444a259b2162f8af0f3fe8c4428a1c6f694dce20de194ac8d7d9a2' -or
    $winDivertInstallerSource -notmatch '8da085332782708d8767bcace5327a6ec7283c17cfb85e40b03cd2323a90ddc2') {
    throw 'WinDivert 2.2.2 x64 payload SHA-256 pins must remain explicit release invariants.'
}

foreach ($token in @('FragmentDisposition', 'Drop', 'TryParseIpv4', 'TryParseIpv6')) {
    if ($fragmentTrackerSource -notmatch [regex]::Escape($token)) {
        throw "Fragment-routing hardening invariant is missing: $token"
    }
}
foreach ($token in @('IpFragmentTracker', 'EvaluateFirstFragment', 'tcp or udp or fragment')) {
    if ($routerSource -notmatch [regex]::Escape($token)) {
        throw "WinDivert fragment integration invariant is missing: $token"
    }
}
foreach ($token in @('WinDivertLayer.Flow', 'WinDivertFlags.Sniff | WinDivertFlags.ReceiveOnly', 'ProcessAttributionCache.Observe')) {
    if ($flowObserverSource -notmatch [regex]::Escape($token)) {
        throw "WinDivert FLOW ownership observer invariant is missing: $token"
    }
}
if ($processResolverSource -notmatch 'ProcessAttributionCache\.TryGetProcessId' -or
    $processAttributionSource -notmatch 'Ambiguous ports') {
    throw 'DNS process attribution must prefer FLOW-layer PID ownership and safely fall back for ambiguous endpoints.'
}
if ($cleanSessionSource -notmatch 'FileOptions\.DeleteOnClose' -or
    $cleanSessionSource -notmatch 'TryDeleteDirectory\(Root\)') {
    throw 'Clean Mode session lifecycle must own an exclusive lock and delete its session tree on disposal.'
}

foreach ($requiredTest in @(
    'Shadowsocks.UnitTests\DnsCryptMaintenancePolicyTests.cs',
    'Shadowsocks.UnitTests\DnsCryptBootstrapPolicyTests.cs',
    'Shadowsocks.UnitTests\DnsCryptCleanModeIntegrationTests.cs',
    'Shadowsocks.UnitTests\CleanStorageSessionTests.cs',
    'Shadowsocks.UnitTests\WinDivertInstallerTests.cs',
    'Shadowsocks.UnitTests\IpFragmentTrackerTests.cs',
    'Shadowsocks.UnitTests\ProcessAttributionCacheTests.cs')) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $requiredTest) -PathType Leaf)) {
        throw "DNSCrypt stage-5 regression test is missing: $requiredTest"
    }
}

$forbiddenBinaryNames = @(
    'dnscrypt-proxy.exe',
    'minisign.exe',
    'libsodium.dll',
    'libsodium-23.dll'
)
$unexpectedBinaries = @(
    Get-ChildItem -LiteralPath $repoRoot -Recurse -File |
        Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj|artifacts)[\\/]' -and
            $forbiddenBinaryNames -contains $_.Name.ToLowerInvariant()
        }
)
if ($unexpectedBinaries.Count -ne 0) {
    throw "DNSCrypt/minisign native binaries must not be stored in the repository or product source tree: $($unexpectedBinaries.FullName -join ', ')"
}

if ($runtimeSource -notmatch 'JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE' -and
    (Read-RepoText 'Shadowsocks.Windows\Util\ProcessManagement\Job.cs') -notmatch 'JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE') {
    throw 'DNSCrypt runtime must remain attached to the shared kill-on-close Windows Job Object.'
}

Write-Host 'Validated DNSCrypt stages 1-5: signed runtime component, pinned WinDivert payloads, fragment-safe routing, FLOW PID attribution, 24-hour maintenance, Clean Mode lifecycle and embedded notices.'
