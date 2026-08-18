[CmdletBinding()]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$Version = 'v5.1.0'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$publishDir = Join-Path $artifactsRoot 'publish'
$releaseDir = Join-Path $artifactsRoot 'release'
$solution = Join-Path $repoRoot 'shadowsocks-reborn.sln'
$project = Join-Path $repoRoot 'Shadowsocks.WinUI\Shadowsocks.WinUI.csproj'
$coreProject = Join-Path $repoRoot 'Shadowsocks.Core\Shadowsocks.Core.csproj'
$testProject = Join-Path $repoRoot 'Shadowsocks.UnitTests\Shadowsocks.UnitTests.csproj'

$normalizedVersion = $Version.Trim() -replace '^[vV]', ''
if ($normalizedVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Invalid release version '$Version'. Use a stable tag such as v5.1.0."
}
$baseVersion = $normalizedVersion
try {
    $requestedVersion = [Version]$baseVersion
}
catch {
    throw "Invalid release version '$Version'. Expected a stable tag such as v5.1.0."
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

$applicationInfoPath = Join-Path $repoRoot 'Shadowsocks.Core\ApplicationInfo.cs'
$applicationInfoSource = Get-Content -LiteralPath $applicationInfoPath -Raw
if ($applicationInfoSource -notmatch 'public const string Version = "(?<version>[0-9]+(?:\.[0-9]+){1,3})";') {
    throw 'Unable to read ApplicationInfo.Version.'
}
$applicationVersion = [Version]$Matches['version']
if ($applicationVersion.Major -ne $declaredVersion.Major -or
    $applicationVersion.Minor -ne $declaredVersion.Minor -or
    $applicationVersion.Build -ne $declaredVersion.Build) {
    throw "ApplicationInfo.Version '$applicationVersion' does not match project version '$declaredProjectVersion'."
}

$versionedProjects = @(
    'Shadowsocks.Core\Shadowsocks.Core.csproj',
    'Shadowsocks.Windows\Shadowsocks.Windows.csproj',
    'Shadowsocks.NetworkService\Shadowsocks.NetworkService.csproj'
)
foreach ($relativeProjectPath in $versionedProjects) {
    $versionedProjectPath = Join-Path $repoRoot $relativeProjectPath
    $versionedProjectXml = [xml](Get-Content -LiteralPath $versionedProjectPath -Raw)
    $versionedProjectVersionText = @($versionedProjectXml.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    if ([string]::IsNullOrWhiteSpace($versionedProjectVersionText)) {
        throw "Release project '$relativeProjectPath' does not declare <Version>."
    }

    $versionedProjectVersion = [Version]$versionedProjectVersionText
    if ($versionedProjectVersion.Major -ne $declaredVersion.Major -or
        $versionedProjectVersion.Minor -ne $declaredVersion.Minor -or
        $versionedProjectVersion.Build -ne $declaredVersion.Build) {
        throw "Release project '$relativeProjectPath' version '$versionedProjectVersionText' does not match '$declaredProjectVersion'."
    }
}

$versionedManifests = @(
    'Shadowsocks.WinUI\app.manifest',
    'Shadowsocks.NetworkService\app.manifest'
)
foreach ($relativeManifestPath in $versionedManifests) {
    $manifestPath = Join-Path $repoRoot $relativeManifestPath
    $manifestXml = [xml](Get-Content -LiteralPath $manifestPath -Raw)
    $manifestIdentity = $manifestXml.SelectSingleNode("/*[local-name()='assembly']/*[local-name()='assemblyIdentity']")
    if ($null -eq $manifestIdentity) {
        throw "Release manifest '$relativeManifestPath' does not contain assemblyIdentity."
    }

    $manifestVersionText = [string]$manifestIdentity.GetAttribute('version')
    if ([string]::IsNullOrWhiteSpace($manifestVersionText)) {
        throw "Release manifest '$relativeManifestPath' does not declare assemblyIdentity version."
    }

    $manifestVersion = [Version]$manifestVersionText
    if ($manifestVersion.Major -ne $declaredVersion.Major -or
        $manifestVersion.Minor -ne $declaredVersion.Minor -or
        $manifestVersion.Build -ne $declaredVersion.Build) {
        throw "Release manifest '$relativeManifestPath' version '$manifestVersionText' does not match '$declaredProjectVersion'."
    }
}

$changelogPath = Join-Path $repoRoot 'CHANGELOG.md'
$changelogText = Get-Content -LiteralPath $changelogPath -Raw
$escapedReleaseVersion = [regex]::Escape($normalizedVersion)
if ($changelogText -notmatch "(?m)^## \[$escapedReleaseVersion\] - \d{4}-\d{2}-\d{2}\s*$") {
    throw "CHANGELOG.md does not contain a dated release heading for $normalizedVersion."
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

    Invoke-DotNet -Arguments @('restore', $coreProject)
    Invoke-DotNet -Arguments @('build', $coreProject, '-c', 'Release', '--no-restore')
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
        '--self-contained', 'true',
        '-o', $publishDir,
        '--no-restore'
    )

    Assert-Exists (Join-Path $publishDir 'Shadowsocks.exe')

    $publishEntries = @(Get-ChildItem -LiteralPath $publishDir -Force)
    if ($publishEntries.Count -ne 1 -or $publishEntries[0].PSIsContainer -or $publishEntries[0].Name -ne 'Shadowsocks.exe') {
        throw "Product publish must contain exactly Shadowsocks.exe. Found: $($publishEntries.Name -join ', ')"
    }

    $forbiddenNames = @(
        'Shadowsocks.NetworkService.dll',
        'Shadowsocks.NetworkService.deps.json',
        'Shadowsocks.NetworkService.runtimeconfig.json',
        'Shadowsocks.pdb',
        'Shadowsocks.NetworkService.exe',
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


    $safeVersion = $Version -replace '[^0-9A-Za-z._-]', '-'
    $zipName = "shadowsocks-reborn-$safeVersion-win-x64.zip"
    $zipPath = Join-Path $releaseDir $zipName
    $hashPath = "$zipPath.sha256"

    Compress-Archive -Path (Join-Path $publishDir 'Shadowsocks.exe') -DestinationPath $zipPath -CompressionLevel Optimal -Force

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entries = @($zip.Entries | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name) })
        if ($entries.Count -ne 1 -or $entries[0].Name -ne 'Shadowsocks.exe') {
            throw "Release ZIP must contain exactly Shadowsocks.exe. Found: $($entries.FullName -join ', ')"
        }
    }
    finally {
        $zip.Dispose()
    }

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
