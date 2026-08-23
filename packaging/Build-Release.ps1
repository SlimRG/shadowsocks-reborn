[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^[vV]?\d+\.\d+\.\d+$')]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $repoRoot 'artifacts'
$publishDir = Join-Path $artifactsRoot 'publish'
$releaseDir = Join-Path $artifactsRoot 'release'
$solutionPath = Join-Path $repoRoot 'shadowsocks-reborn.sln'
$mainProjectPath = Join-Path $repoRoot 'Shadowsocks.WinUI\Shadowsocks.WinUI.csproj'
$coreProjectPath = Join-Path $repoRoot 'Shadowsocks.Core\Shadowsocks.Core.csproj'
$testProjectPath = Join-Path $repoRoot 'Shadowsocks.UnitTests\Shadowsocks.UnitTests.csproj'
$minimumReleaseSdk = [Version]'10.0.303'

function Get-CanonicalVersion {
    [CmdletBinding()]
    param()

    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    $value = [string](@($props.Project.PropertyGroup.Version | Where-Object { $_ })[0])
    if ([string]::IsNullOrWhiteSpace($value) -or $value -notmatch '^\d+\.\d+\.\d+$') {
        throw 'Directory.Build.props must contain one stable three-part <Version> value.'
    }

    return $value.Trim()
}

function Test-RequiredFile {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required release file is missing: $Path"
    }
}

function Invoke-DotNet {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Test-VersionMetadata {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ReleaseVersion,
        [Parameter(Mandatory)][Version]$ExpectedFourPartVersion
    )

    $applicationInfoPath = Join-Path $repoRoot 'Shadowsocks.Core\ApplicationInfo.cs'
    $applicationInfoSource = Get-Content -LiteralPath $applicationInfoPath -Raw
    if ($applicationInfoSource -notmatch 'public const string Version = "(?<version>\d+(?:\.\d+){3})";') {
        throw 'Unable to read ApplicationInfo.Version.'
    }
    if ([Version]$Matches.version -ne $ExpectedFourPartVersion) {
        throw "ApplicationInfo.Version '$($Matches.version)' must match '$ExpectedFourPartVersion'."
    }

    foreach ($relativePath in @('Shadowsocks.WinUI\app.manifest', 'Shadowsocks.NetworkService\app.manifest')) {
        $manifestPath = Join-Path $repoRoot $relativePath
        [xml]$manifest = Get-Content -LiteralPath $manifestPath -Raw
        $identity = $manifest.SelectSingleNode("/*[local-name()='assembly']/*[local-name()='assemblyIdentity']")
        if ($null -eq $identity) {
            throw "Manifest '$relativePath' does not contain assemblyIdentity."
        }

        $manifestVersion = [Version]$identity.GetAttribute('version')
        if ($manifestVersion -ne $ExpectedFourPartVersion) {
            throw "Manifest '$relativePath' version '$manifestVersion' must match '$ExpectedFourPartVersion'."
        }
    }

    $changesText = Get-Content -LiteralPath (Join-Path $repoRoot 'CHANGES') -Raw
    $escapedVersion = [regex]::Escape($ExpectedFourPartVersion.ToString(4))
    if ($changesText -notmatch "(?m)^$escapedVersion \d{4}-\d{2}-\d{2}\s*$") {
        throw "CHANGES must contain a dated heading for $ExpectedFourPartVersion."
    }

    if ($ReleaseVersion -ne $ExpectedFourPartVersion.ToString(3)) {
        throw "Release version '$ReleaseVersion' is inconsistent with '$ExpectedFourPartVersion'."
    }
}

$canonicalVersion = Get-CanonicalVersion
$requestedVersion = if ([string]::IsNullOrWhiteSpace($Version)) {
    $canonicalVersion
}
else {
    $Version.Trim() -replace '^[vV]', ''
}

if ($requestedVersion -ne $canonicalVersion) {
    throw "Requested release '$requestedVersion' does not match Directory.Build.props version '$canonicalVersion'."
}

$expectedFourPartVersion = [Version]"$canonicalVersion.0"
Test-VersionMetadata -ReleaseVersion $canonicalVersion -ExpectedFourPartVersion $expectedFourPartVersion

$dotnetSdkText = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $dotnetSdkText -notmatch '^\d+\.\d+\.\d+$') {
    throw "Unable to resolve a stable .NET SDK version. Found '$dotnetSdkText'."
}

$dotnetSdkVersion = [Version]$dotnetSdkText
if ($dotnetSdkVersion.Major -ne 10 -or $dotnetSdkVersion -lt $minimumReleaseSdk) {
    throw "Release build requires .NET 10 SDK $minimumReleaseSdk or newer. Found $dotnetSdkVersion."
}

Push-Location $repoRoot
try {
    Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $releaseDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -Path $publishDir -ItemType Directory -Force | Out-Null
    New-Item -Path $releaseDir -ItemType Directory -Force | Out-Null

    & (Join-Path $PSScriptRoot 'Validate-Repository.ps1')

    Invoke-DotNet -Arguments @('restore', $coreProjectPath, '-p:NuGetAudit=true', '-p:NuGetAuditMode=all')
    Invoke-DotNet -Arguments @('build', $coreProjectPath, '-c', 'Release', '--no-restore', '-p:TreatWarningsAsErrors=true')
    Invoke-DotNet -Arguments @('restore', $solutionPath, '-p:Platform=x64', '-r', 'win-x64', '-p:NuGetAudit=true', '-p:NuGetAuditMode=all')
    Invoke-DotNet -Arguments @('build', $solutionPath, '-c', 'Release', '-p:Platform=x64', '-m:1', '--no-restore', '-p:TreatWarningsAsErrors=true')
    Invoke-DotNet -Arguments @('test', $testProjectPath, '-c', 'Release', '-p:Platform=x64', '--no-build')
    Invoke-DotNet -Arguments @(
        'restore', $mainProjectPath,
        '-p:Platform=x64',
        '-p:PublishProfile=FolderProfile',
        '-r', 'win-x64',
        '-p:NuGetAudit=true',
        '-p:NuGetAuditMode=all'
    )
    Invoke-DotNet -Arguments @(
        'publish', $mainProjectPath,
        '-c', 'Release',
        '-p:Platform=x64',
        '-p:PublishProfile=FolderProfile',
        '-r', 'win-x64',
        '--self-contained', 'true',
        '-o', $publishDir,
        '--no-restore',
        '-p:TreatWarningsAsErrors=true'
    )

    $publishedExecutable = Join-Path $publishDir 'Shadowsocks.exe'
    Test-RequiredFile $publishedExecutable

    $publishEntries = @(Get-ChildItem -LiteralPath $publishDir -Force)
    if ($publishEntries.Count -ne 1 -or $publishEntries[0].PSIsContainer -or $publishEntries[0].Name -ne 'Shadowsocks.exe') {
        $publishEntryNames = @($publishEntries | ForEach-Object { $_.Name }) -join ', '
        throw "Final publish must contain exactly Shadowsocks.exe. Found: $publishEntryNames"
    }

    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($publishedExecutable)
    $publishedFileVersion = [Version]::new(
        [Math]::Max(0, $versionInfo.FileMajorPart),
        [Math]::Max(0, $versionInfo.FileMinorPart),
        [Math]::Max(0, $versionInfo.FileBuildPart),
        [Math]::Max(0, $versionInfo.FilePrivatePart))
    if ($publishedFileVersion -ne $expectedFourPartVersion) {
        throw "Published Shadowsocks.exe FileVersion '$publishedFileVersion' does not match '$expectedFourPartVersion'."
    }

    $zipName = 'Shadowsocks-win-x64.zip'
    $zipPath = Join-Path $releaseDir $zipName
    $hashPath = "$zipPath.sha256"
    Compress-Archive -LiteralPath $publishedExecutable -DestinationPath $zipPath -CompressionLevel Optimal -Force

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $entries = @($archive.Entries | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Name) })
        if ($entries.Count -ne 1 -or $entries[0].FullName -ne 'Shadowsocks.exe') {
            $entryNames = @($entries | ForEach-Object { $_.FullName }) -join ', '
            throw "Release ZIP must contain exactly Shadowsocks.exe. Found: $entryNames"
        }
    }
    finally {
        $archive.Dispose()
    }

    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath $hashPath -Value "$hash  $zipName" -Encoding ascii -NoNewline

    Write-Information "Release package created:`n  $zipPath`n  $hashPath" -InformationAction Continue
}
finally {
    Pop-Location
}
