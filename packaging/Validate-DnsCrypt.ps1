[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

function Test-Condition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-ProjectPackageVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][string]$PackageName
    )

    [xml]$project = Get-Content -LiteralPath $ProjectPath -Raw
    $node = $project.SelectSingleNode("/Project/ItemGroup/PackageReference[@Include='$PackageName']")
    if ($null -eq $node) {
        return $null
    }

    return [string]$node.Version
}

$componentPath = Join-Path $repoRoot 'Shadowsocks.Windows\Controller\Service\DnsCryptComponentManager.cs'
$controllerPath = Join-Path $repoRoot 'Shadowsocks.Windows\Controller\ShadowsocksController.DnsCrypt.cs'
$resolverCatalogBootstrapperPath = Join-Path $repoRoot 'Shadowsocks.Windows\Controller\Service\DnsCryptResolverCatalogBootstrapper.cs'
$winDivertPath = Join-Path $repoRoot 'Shadowsocks.Windows\Controller\Traffic\WinDivertInstaller.cs'
$windowsProjectPath = Join-Path $repoRoot 'Shadowsocks.Windows\Shadowsocks.Windows.csproj'
$testsProjectPath = Join-Path $repoRoot 'Shadowsocks.UnitTests\Shadowsocks.UnitTests.csproj'
$noticesPath = Join-Path $repoRoot 'THIRD-PARTY-NOTICES.txt'

foreach ($path in @($componentPath, $controllerPath, $resolverCatalogBootstrapperPath, $winDivertPath, $windowsProjectPath, $testsProjectPath, $noticesPath)) {
    Test-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required DNS/security file is missing: $path"
}

$notices = Get-Content -LiteralPath $noticesPath -Raw

# Concrete security pins are asserted by typed regression tests. This validator intentionally
# checks repository structure, dependencies, notices, and absence of bundled native payloads
# instead of parsing C# implementation text.

$windowsBouncyCastle = Get-ProjectPackageVersion $windowsProjectPath 'BouncyCastle.Cryptography'
$testsBouncyCastle = Get-ProjectPackageVersion $testsProjectPath 'BouncyCastle.Cryptography'
Test-Condition (-not [string]::IsNullOrWhiteSpace($windowsBouncyCastle)) 'Shadowsocks.Windows must reference BouncyCastle.Cryptography for managed signature verification.'
Test-Condition ($windowsBouncyCastle -eq $testsBouncyCastle) 'BouncyCastle.Cryptography version must match between product and tests.'

foreach ($notice in @('Bouncy Castle Cryptography for .NET', 'dnscrypt-proxy', 'License: ISC', 'WinDivert', 'Windows App SDK')) {
    Test-Condition ($notices.IndexOf($notice, [StringComparison]::OrdinalIgnoreCase) -ge 0) "THIRD-PARTY-NOTICES.txt is missing '$notice'."
}

$requiredTests = @(
    'DnsCryptBootstrapPolicyTests.cs',
    'DnsCryptCleanModeIntegrationTests.cs',
    'DnsCryptComponentManagerTests.cs',
    'DnsCryptCoordinatorTests.cs',
    'DnsCryptCountrySelectorTests.cs',
    'DnsCryptMaintenancePolicyTests.cs',
    'DnsCryptRuntimeManagerTests.cs',
    'DnsCryptResolverCatalogBootstrapperTests.cs',
    'DnsCryptTomlGeneratorTests.cs',
    'DnsProcessResolverTests.cs',
    'DnsCaptureStage4Tests.cs',
    'WinDivertInstallerTests.cs',
    'IpFragmentTrackerTests.cs',
    'ProcessAttributionCacheTests.cs'
)
foreach ($testFile in $requiredTests) {
    $path = Join-Path $repoRoot "Shadowsocks.UnitTests\$testFile"
    Test-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required DNS/WinDivert regression test is missing: $testFile"
}

$forbiddenNativeNames = @(
    'dnscrypt-proxy.exe',
    'minisign.exe',
    'libsodium.dll',
    'libsodium-23.dll',
    'WinDivert.dll',
    'WinDivert64.sys'
)
$unexpectedNativeFiles = @(
    Get-ChildItem -LiteralPath $repoRoot -Recurse -File |
        Where-Object {
            $_.FullName -notmatch '[\\/](?:\.git|\.github|bin|obj|artifacts)[\\/]' -and
            $forbiddenNativeNames -contains $_.Name
        }
)
Test-Condition ($unexpectedNativeFiles.Count -eq 0) "Downloaded runtime binaries must not be committed to the source tree: $($unexpectedNativeFiles.FullName -join ', ')"

Write-Information 'DNSCrypt/WinDivert release invariants validated.' -InformationAction Continue
