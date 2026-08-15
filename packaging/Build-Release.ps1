[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$Version = 'v5.0.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$publishDir = Join-Path $artifactsRoot 'publish'
$releaseDir = Join-Path $artifactsRoot 'release'
$solution = Join-Path $repoRoot 'shadowsocks-reborn.sln'
$project = Join-Path $repoRoot 'Shadowsocks.UI\Shadowsocks.UI.csproj'
$testProject = Join-Path $repoRoot 'Shadowsocks.UnitTests\Shadowsocks.UnitTests.csproj'

$normalizedVersion = $Version.Trim() -replace '^[vV]', ''
if ($normalizedVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Invalid release version '$Version'. Use a stable tag such as v5.0.0."
}
$baseVersion = $normalizedVersion
try {
    $requestedVersion = [Version]$baseVersion
}
catch {
    throw "Invalid release version '$Version'. Expected a stable tag such as v5.0.0."
}

$projectXml = [xml](Get-Content -LiteralPath $project -Raw)
$declaredProjectVersion = @($projectXml.Project.PropertyGroup.Version | Where-Object { $_ })[0]
if ([string]::IsNullOrWhiteSpace($declaredProjectVersion)) {
    throw "The main project does not declare <Version>."
}

$declaredVersion = [Version]$declaredProjectVersion
if ($requestedVersion.Major -ne $declaredVersion.Major -or
    $requestedVersion.Minor -ne $declaredVersion.Minor -or
    $requestedVersion.Build -ne $declaredVersion.Build) {
    throw "Release label '$Version' does not match project version '$declaredProjectVersion'. Update version metadata before tagging."
}

$updateCheckerPath = Join-Path $repoRoot 'Shadowsocks.Engine\Controller\Service\UpdateChecker.cs'
$updateCheckerSource = Get-Content -LiteralPath $updateCheckerPath -Raw
if ($updateCheckerSource -notmatch 'public const string Version = "(?<version>[0-9]+(?:\.[0-9]+){1,3})";') {
    throw 'Unable to read UpdateChecker.Version.'
}
$updateCheckerVersion = [Version]$Matches['version']
if ($updateCheckerVersion.Major -ne $declaredVersion.Major -or
    $updateCheckerVersion.Minor -ne $declaredVersion.Minor -or
    $updateCheckerVersion.Build -ne $declaredVersion.Build) {
    throw "UpdateChecker.Version '$updateCheckerVersion' does not match project version '$declaredProjectVersion'."
}

$assemblyInfoPath = Join-Path $repoRoot 'Shadowsocks.UI\Properties\AssemblyInfo.cs'
$assemblyInfoSource = Get-Content -LiteralPath $assemblyInfoPath -Raw
if ($assemblyInfoSource -notmatch 'AssemblyInformationalVersion\("(?<version>[^"]+)"\)') {
    throw 'Unable to read AssemblyInformationalVersion.'
}
if ($Matches['version'] -ne $baseVersion) {
    throw "AssemblyInformationalVersion '$($Matches['version'])' does not match release version '$baseVersion'."
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Assert-Exists {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required release artifact is missing: $Path"
    }
}

Push-Location $repoRoot
try {
    Remove-Item $publishDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $releaseDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item $publishDir -ItemType Directory -Force | Out-Null
    New-Item $releaseDir -ItemType Directory -Force | Out-Null

    & (Join-Path $PSScriptRoot 'Validate-Repository.ps1')

    Invoke-DotNet -Arguments @('restore', $solution, '-p:Platform=x64', '-r', 'win-x64')
    Invoke-DotNet -Arguments @('build', $solution, '-c', 'Release', '-p:Platform=x64', '-m:1', '--no-restore')
    Invoke-DotNet -Arguments @('test', $testProject, '-c', 'Release', '-p:Platform=x64', '--no-build')
    Invoke-DotNet -Arguments @(
        'restore', $project,
        '-p:Platform=x64',
        '-p:PublishProfile=FolderProfile',
        '-r', 'win-x64'
    )
    Invoke-DotNet -Arguments @(
        'publish', $project,
        '-c', 'Release',
        '-p:Platform=x64',
        '-p:PublishProfile=FolderProfile',
        '-r', 'win-x64',
        '--no-self-contained',
        '-o', $publishDir,
        '--no-restore'
    )

    Assert-Exists (Join-Path $publishDir 'shadowsocks-reborn.exe')
    Assert-Exists (Join-Path $publishDir 'Shadowsocks.NetworkService.exe')

    $forbiddenNames = @(
        'Shadowsocks.NetworkService.dll',
        'Shadowsocks.NetworkService.deps.json',
        'Shadowsocks.NetworkService.runtimeconfig.json',
        'shadowsocks-reborn.pdb',
        'privoxy.exe',
        'privoxy.exe.gz',
        'sysproxy.exe',
        'sysproxy64.exe',
        'libsscrypto.dll'
    )

    foreach ($name in $forbiddenNames) {
        $unexpected = Get-ChildItem -Path $publishDir -Recurse -File -Filter $name -ErrorAction SilentlyContinue
        if ($unexpected) {
            throw "Retired or invalid artifact found in publish output: $($unexpected.FullName -join ', ')"
        }
    }

    Copy-Item (Join-Path $repoRoot 'LICENSE.txt') $publishDir -Force
    Copy-Item (Join-Path $repoRoot 'README.md') $publishDir -Force
    Copy-Item (Join-Path $repoRoot 'README.ru.md') $publishDir -Force
    Copy-Item (Join-Path $repoRoot 'CHANGELOG.md') $publishDir -Force

    $safeVersion = $Version -replace '[^0-9A-Za-z._-]', '-'
    $zipName = "shadowsocks-reborn-$safeVersion-win-x64.zip"
    $zipPath = Join-Path $releaseDir $zipName
    $hashPath = "$zipPath.sha256"

    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath -CompressionLevel Optimal -Force

    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $hashPath -Value "$hash  $zipName" -Encoding ascii -NoNewline

    Write-Host ''
    Write-Host 'Release package created:'
    Write-Host "  $zipPath"
    Write-Host "  $hashPath"
}
finally {
    Pop-Location
}
