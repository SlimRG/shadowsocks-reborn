[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$minimumSdk = [Version]'10.0.303'
$expectedLocales = @('en', 'ru-RU', 'zh-CN', 'zh-TW', 'ja', 'ko', 'fr')

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

function Test-RequiredFile {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RelativePath)

    $path = Join-Path $repoRoot $RelativePath
    Test-Condition (Test-Path -LiteralPath $path -PathType Leaf) "Required file is missing: $RelativePath"
}

function Get-XmlDocument {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RelativePath)

    $path = Join-Path $repoRoot $RelativePath
    Test-RequiredFile $RelativePath
    return [xml](Get-Content -LiteralPath $path -Raw)
}

function Get-ProjectProperty {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][xml]$Project,
        [Parameter(Mandatory)][string]$Name
    )

    $node = $Project.SelectSingleNode("/Project/PropertyGroup/$Name")
    return if ($null -eq $node) { $null } else { [string]$node.InnerText }
}

function Get-PackageVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][xml]$Project,
        [Parameter(Mandatory)][string]$PackageName
    )

    $node = $Project.SelectSingleNode("/Project/ItemGroup/PackageReference[@Include='$PackageName']")
    return if ($null -eq $node) { $null } else { [string]$node.Version }
}

function Get-RepositoryFiles {
    [CmdletBinding()]
    param()

    return @(
        Get-ChildItem -LiteralPath $repoRoot -Recurse -File |
            Where-Object { $_.FullName -notmatch '[\\/](?:\.git|\.github|bin|obj|artifacts)[\\/]' }
    )
}

function Test-PowerShellSyntax {
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.IO.FileInfo[]]$Files)

    foreach ($file in $Files) {
        $tokens = $null
        $errors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
        if ($errors.Count -ne 0) {
            $messages = $errors | ForEach-Object { "$($_.Extent.StartLineNumber):$($_.Extent.StartColumnNumber) $($_.Message)" }
            throw "PowerShell syntax error in '$($file.FullName)': $($messages -join '; ')"
        }
    }
}

function Test-TextEncodingAndLineEndings {
    [CmdletBinding()]
    param([Parameter(Mandatory)][System.IO.FileInfo[]]$Files)

    $extensions = @('.cs', '.csproj', '.props', '.targets', '.pubxml', '.xml', '.xaml', '.resx', '.json', '.md', '.txt', '.csv', '.ps1', '.sln', '.manifest', '.config')
    foreach ($file in $Files) {
        if ($extensions -notcontains $file.Extension.ToLowerInvariant() -and $file.Name -notin @('CHANGES', 'NuGet.Config')) {
            continue
        }

        $bytes = [System.IO.File]::ReadAllBytes($file.FullName)
        Test-Condition ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) "Text file must be UTF-8 with BOM: $($file.FullName)"
        $text = [System.Text.Encoding]::UTF8.GetString($bytes, 3, $bytes.Length - 3)
        Test-Condition ($text -notmatch '(?<!\r)\n') "Text file must use CRLF line endings: $($file.FullName)"
    }
}

function Test-WinUiIsEnabledAssignments {
    [CmdletBinding()]
    param()

    $winUiRoot = Join-Path $repoRoot 'Shadowsocks.WinUI'
    $nonControlTypes = @('Border', 'Grid', 'StackPanel', 'Panel', 'TextBlock', 'Image', 'FontIcon', 'Canvas')
    $sourceFiles = @(Get-ChildItem -LiteralPath $winUiRoot -Recurse -File -Filter '*.cs')

    foreach ($file in $sourceFiles) {
        $text = Get-Content -LiteralPath $file.FullName -Raw
        foreach ($typeName in $nonControlTypes) {
            $declarations = [regex]::Matches($text, "\b$([regex]::Escape($typeName))\s+(?<name>_?[A-Za-z][A-Za-z0-9_]*)\b")
            foreach ($declaration in $declarations) {
                $variableName = $declaration.Groups['name'].Value
                $assignmentPattern = "\b$([regex]::Escape($variableName))\.IsEnabled\s*="
                Test-Condition ($text -notmatch $assignmentPattern) "WinUI IsEnabled cannot target non-Control $typeName '$variableName' in $($file.FullName). Disable the interactive child controls instead."
            }
        }
    }
}

$requiredFiles = @(
    'Directory.Build.props',
    'global.json',
    'NuGet.Config',
    'appsettings.json',
    'LICENSE.txt',
    'THIRD-PARTY-NOTICES.txt',
    'README.md',
    'README.ru.md',
    'README.zh-CN.md',
    'CHANGES',
    'shadowsocks-reborn.sln',
    'Shadowsocks.Core\Shadowsocks.Core.csproj',
    'Shadowsocks.Windows\Shadowsocks.Windows.csproj',
    'Shadowsocks.Windows.WinUI\Shadowsocks.Windows.WinUI.csproj',
    'Shadowsocks.NetworkService\Shadowsocks.NetworkService.csproj',
    'Shadowsocks.WinUI\Shadowsocks.WinUI.csproj',
    'Shadowsocks.UnitTests\Shadowsocks.UnitTests.csproj',
    'Shadowsocks.WinUI\Properties\PublishProfiles\FolderProfile.pubxml',
    'Shadowsocks.Core\Routing\FilterEngine.cs',
    'Shadowsocks.UnitTests\FilterEngineTests.cs',
    'Shadowsocks.UnitTests\ManagedRoutingArchitectureTests.cs',
    'Shadowsocks.UnitTests\ManagedRoutingIntegrationTests.cs',
    'Shadowsocks.Windows\Controller\Service\PluginManager.cs',
    'Shadowsocks.Windows\Controller\ShadowsocksController.Plugins.Maintenance.cs',
    'Shadowsocks.WinUI\Pages\PluginsPage.cs',
    'Shadowsocks.UnitTests\PluginManagerTests.cs',
    'Shadowsocks.UnitTests\LocalizationServiceTests.cs',
    'TODO.txt'
)
foreach ($file in $requiredFiles) {
    Test-RequiredFile $file
}

$allFiles = Get-RepositoryFiles
Test-PowerShellSyntax @($allFiles | Where-Object Extension -EQ '.ps1')
Test-TextEncodingAndLineEndings $allFiles
Test-WinUiIsEnabledAssignments

$unexpectedBuildTrees = @(Get-ChildItem -LiteralPath $repoRoot -Recurse -Directory | Where-Object { $_.Name -in @('bin', 'obj', 'artifacts') -and $_.FullName -notmatch '[\\/](?:\.git|\.github)[\\/]' })
Test-Condition ($unexpectedBuildTrees.Count -eq 0) "Source archive must not contain build output directories: $($unexpectedBuildTrees.FullName -join ', ')"

$javascriptFiles = @($allFiles | Where-Object { $_.Extension.ToLowerInvariant() -in @('.js', '.mjs', '.cjs') })
Test-Condition ($javascriptFiles.Count -eq 0) "Bundled JavaScript is not allowed in the source tree: $($javascriptFiles.FullName -join ', ')"

$forbiddenProjectDirectories = @('Shadowsocks.Engine', 'Shadowsocks.UI')
foreach ($directory in $forbiddenProjectDirectories) {
    Test-Condition (-not (Test-Path -LiteralPath (Join-Path $repoRoot $directory))) "Retired project directory remains: $directory"
}

$projectFiles = @($allFiles | Where-Object Extension -EQ '.csproj')
Test-Condition ($projectFiles.Count -eq 6) "Expected exactly six project files; found $($projectFiles.Count)."

$props = Get-XmlDocument 'Directory.Build.props'
$canonicalVersion = Get-ProjectProperty $props 'Version'
Test-Condition ($canonicalVersion -match '^\d+\.\d+\.\d+$') 'Directory.Build.props must define one stable three-part Version.'
Test-Condition ((Get-ProjectProperty $props 'AnalysisLevel') -eq '10.0-recommended') 'AnalysisLevel must be 10.0-recommended.'
Test-Condition ((Get-ProjectProperty $props 'EnforceCodeStyleInBuild') -eq 'true') 'EnforceCodeStyleInBuild must be true.'
$nugetAuditNode = $props.SelectSingleNode("/Project/PropertyGroup/NuGetAudit")
Test-Condition ($null -ne $nugetAuditNode -and [string]$nugetAuditNode.InnerText -eq 'false') 'NuGetAudit must default to false for deterministic local builds.'
$warningsAsErrors = [string]($props.SelectSingleNode('/Project/PropertyGroup/WarningsAsErrors').InnerText)
foreach ($code in @('NU1900', 'NU1901', 'NU1902', 'NU1903', 'NU1904')) {
    Test-Condition ($warningsAsErrors -match "(?:^|;)$code(?:;|$)") "NuGet audit warning $code must be release-blocking when audit is enabled."
}

$projectVersions = @($projectFiles | ForEach-Object { ([xml](Get-Content -LiteralPath $_.FullName -Raw)).SelectNodes('/Project/PropertyGroup/Version') } | Where-Object { $null -ne $_ })
Test-Condition ($projectVersions.Count -eq 0) 'Project files must inherit the canonical Version from Directory.Build.props instead of duplicating it.'

$global = Get-Content -LiteralPath (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json
$sdkVersion = [Version]$global.sdk.version
Test-Condition ($sdkVersion.Major -eq 10 -and $sdkVersion -ge $minimumSdk) "global.json must pin .NET 10 SDK $minimumSdk or newer."
Test-Condition ($global.sdk.rollForward -eq 'latestFeature') 'global.json rollForward must be latestFeature.'
Test-Condition ($global.sdk.allowPrerelease -eq $false) 'global.json must disable prerelease SDKs.'

[xml]$nugetConfig = Get-Content -LiteralPath (Join-Path $repoRoot 'NuGet.Config') -Raw
$packageSources = @($nugetConfig.configuration.packageSources.add)
Test-Condition ($packageSources.Count -eq 1 -and $packageSources[0].key -eq 'nuget.org' -and $packageSources[0].value -eq 'https://api.nuget.org/v3/index.json' -and $packageSources[0].protocolVersion -eq '3') 'NuGet.Config must use only the official nuget.org v3 package source.'
$restoreSettings = @($nugetConfig.configuration.packageRestore.add)
$restoreEnabled = @($restoreSettings | Where-Object { $_.key -eq 'enabled' -and $_.value -eq 'True' }).Count -eq 1
$automaticRestore = @($restoreSettings | Where-Object { $_.key -eq 'automatic' -and $_.value -eq 'True' }).Count -eq 1
Test-Condition ($restoreSettings.Count -eq 2 -and $restoreEnabled -and $automaticRestore) 'NuGet.Config must keep package restore enabled and automatic.'

$core = Get-XmlDocument 'Shadowsocks.Core\Shadowsocks.Core.csproj'
$windows = Get-XmlDocument 'Shadowsocks.Windows\Shadowsocks.Windows.csproj'
$windowsWinUI = Get-XmlDocument 'Shadowsocks.Windows.WinUI\Shadowsocks.Windows.WinUI.csproj'
$networkService = Get-XmlDocument 'Shadowsocks.NetworkService\Shadowsocks.NetworkService.csproj'
$winUI = Get-XmlDocument 'Shadowsocks.WinUI\Shadowsocks.WinUI.csproj'
$tests = Get-XmlDocument 'Shadowsocks.UnitTests\Shadowsocks.UnitTests.csproj'
$publishProfile = Get-XmlDocument 'Shadowsocks.WinUI\Properties\PublishProfiles\FolderProfile.pubxml'

Test-Condition ((Get-ProjectProperty $core 'TargetFramework') -eq 'net10.0') 'Shadowsocks.Core must target net10.0.'
Test-Condition ((Get-ProjectProperty $windows 'TargetFramework') -eq 'net10.0-windows10.0.19041.0') 'Shadowsocks.Windows target framework changed unexpectedly.'
Test-Condition ((Get-ProjectProperty $windowsWinUI 'TargetFramework') -eq 'net10.0-windows10.0.26100.0') 'Shadowsocks.Windows.WinUI target framework changed unexpectedly.'
Test-Condition ((Get-ProjectProperty $networkService 'TargetFramework') -eq 'net10.0-windows10.0.19041.0') 'NetworkService target framework changed unexpectedly.'
Test-Condition ((Get-ProjectProperty $winUI 'TargetFramework') -eq 'net10.0-windows10.0.26100.0') 'Shadowsocks.WinUI target framework changed unexpectedly.'
Test-Condition ((Get-ProjectProperty $tests 'TargetFramework') -eq 'net10.0-windows10.0.19041.0') 'Unit test target framework changed unexpectedly.'
foreach ($project in @($windows, $windowsWinUI, $networkService, $winUI, $tests)) {
    Test-Condition ((Get-ProjectProperty $project 'PlatformTarget') -eq 'x64') 'Windows/runtime projects and tests must target x64.'
}

$coreReferences = @($core.SelectNodes('/Project/ItemGroup/ProjectReference'))
Test-Condition ($coreReferences.Count -eq 0) 'Shadowsocks.Core must not depend on another project.'
$windowsReferences = @($windows.SelectNodes('/Project/ItemGroup/ProjectReference') | ForEach-Object { [string]$_.Include })
Test-Condition ($windowsReferences.Count -eq 1 -and $windowsReferences[0] -eq '..\Shadowsocks.Core\Shadowsocks.Core.csproj') 'Shadowsocks.Windows must reference only Shadowsocks.Core.'
$winUIReferences = @($winUI.SelectNodes('/Project/ItemGroup/ProjectReference') | ForEach-Object { [string]$_.Include })
foreach ($expectedReference in @('..\Shadowsocks.Core\Shadowsocks.Core.csproj', '..\Shadowsocks.Windows\Shadowsocks.Windows.csproj', '..\Shadowsocks.Windows.WinUI\Shadowsocks.Windows.WinUI.csproj')) {
    Test-Condition ($winUIReferences -contains $expectedReference) "Shadowsocks.WinUI is missing project reference '$expectedReference'."
}
Test-Condition ($winUIReferences.Count -eq 3) 'Shadowsocks.WinUI must have exactly the expected three project references.'

$embeddedResources = @($core.SelectNodes('/Project/ItemGroup/EmbeddedResource') | ForEach-Object { [string]$_.Include })
foreach ($resource in @('Data\i18n.csv', '..\appsettings.json', '..\LICENSE.txt', '..\THIRD-PARTY-NOTICES.txt', 'Data\user-rule.txt')) {
    Test-Condition ($embeddedResources -contains $resource) "Core embedded resource is missing: $resource"
}
Test-Condition (-not ($embeddedResources | Where-Object { $_ -match 'abp\.js$' })) 'Historical abp.js must not be embedded.'

$linkedFilter = $networkService.SelectSingleNode("/Project/ItemGroup/Compile[@Include='..\Shadowsocks.Core\Routing\FilterEngine.cs']")
Test-Condition ($null -ne $linkedFilter -and [string]$linkedFilter.Link -eq 'Routing\Shared\FilterEngine.cs') 'NetworkService must compile the shared managed FilterEngine source.'

$winUIComponentVersion = Get-PackageVersion $winUI 'Microsoft.WindowsAppSDK.WinUI'
$windowsWinUIComponentVersion = Get-PackageVersion $windowsWinUI 'Microsoft.WindowsAppSDK.WinUI'
Test-Condition (-not [string]::IsNullOrWhiteSpace($winUIComponentVersion) -and $winUIComponentVersion -eq $windowsWinUIComponentVersion) 'Microsoft.WindowsAppSDK.WinUI package version must match across WinUI projects.'

$publishInvariants = @{
    Configuration = 'Release'
    Platform = 'x64'
    RuntimeIdentifier = 'win-x64'
    WindowsPackageType = 'None'
    WindowsAppSDKSelfContained = 'true'
    SelfContained = 'true'
    PublishSingleFile = 'true'
    IncludeAllContentForSelfExtract = 'true'
    IncludeNativeLibrariesForSelfExtract = 'true'
    EnableCompressionInSingleFile = 'true'
    PublishTrimmed = 'false'
    PublishReadyToRun = 'false'
    DebugSymbols = 'false'
}
foreach ($property in $publishInvariants.GetEnumerator()) {
    Test-Condition ((Get-ProjectProperty $publishProfile $property.Key) -eq $property.Value) "Publish profile property '$($property.Key)' must be '$($property.Value)'."
}

foreach ($targetName in @('PrepareEmbeddedNetworkService', 'ValidateFinalSingleFileLayout')) {
    Test-Condition ($null -ne $winUI.SelectSingleNode("/Project/Target[@Name='$targetName']")) "Shadowsocks.WinUI is missing MSBuild target '$targetName'."
}
Test-Condition ($null -ne $networkService.SelectSingleNode("/Project/Target[@Name='ValidateNetworkServicePublishMode']")) 'NetworkService publish validation target is missing.'

$appSettings = Get-Content -LiteralPath (Join-Path $repoRoot 'appsettings.json') -Raw | ConvertFrom-Json
Test-Condition ($null -ne $appSettings.Logging) 'appsettings.json must contain Logging settings.'
foreach ($name in @('FilePath', 'FallbackFilePath', 'MinimumLevel', 'VerboseMinimumLevel', 'ArchiveAboveSizeBytes', 'MaxArchiveFiles', 'MaxArchiveDays', 'Layout')) {
    Test-Condition ($null -ne $appSettings.Logging.$name) "Logging setting '$name' is missing."
}

$i18nPath = Join-Path $repoRoot 'Shadowsocks.Core\Data\i18n.csv'
Test-RequiredFile 'Shadowsocks.Core\Data\i18n.csv'
$i18nRows = @(Import-Csv -LiteralPath $i18nPath)
Test-Condition ($i18nRows.Count -gt 0) 'i18n.csv must contain translations.'
$actualLocales = @($i18nRows[0].PSObject.Properties.Name)
Test-Condition (($actualLocales -join ',') -eq ($expectedLocales -join ',')) "i18n.csv locale columns must be: $($expectedLocales -join ', ')."
$duplicateKeys = @($i18nRows | Group-Object en | Where-Object Count -GT 1)
Test-Condition ($duplicateKeys.Count -eq 0) "i18n.csv contains duplicate English keys: $($duplicateKeys.Name -join ', ')"
foreach ($row in $i18nRows) {
    if ([string]$row.en -match '^#') {
        continue
    }

    foreach ($locale in $expectedLocales) {
        Test-Condition (-not [string]::IsNullOrWhiteSpace([string]$row.$locale)) "i18n.csv has an empty '$locale' translation for key '$($row.en)'."
    }

    Test-Condition ([string]$row.'ru-RU' -notmatch '[\u3040-\u30ff\u3400-\u9fff\uac00-\ud7af]') "i18n.csv has CJK text in ru-RU for key '$($row.en)'."
}

$xsdFiles = @($allFiles | Where-Object Extension -EQ '.xsd')
Test-Condition ($xsdFiles.Count -eq 0) "Generated XSD files must not be stored in the source tree: $($xsdFiles.FullName -join ', ')"
foreach ($weaverPath in @('Shadowsocks.Core\FodyWeavers.xml', 'Shadowsocks.Windows\FodyWeavers.xml')) {
    Test-RequiredFile $weaverPath
    $weaverText = Get-Content -LiteralPath (Join-Path $repoRoot $weaverPath) -Raw
    Test-Condition ($weaverText -notmatch 'schemaLocation') "$weaverPath must not reference generated XSD schemas."
}

$rootMarkdown = @(Get-ChildItem -LiteralPath $repoRoot -File -Filter '*.md' | ForEach-Object Name | Sort-Object)
$expectedRootMarkdown = @('README.md', 'README.ru.md', 'README.zh-CN.md')
Test-Condition (($rootMarkdown -join ',') -eq ($expectedRootMarkdown -join ',')) "Root Markdown files must be only localized READMEs. Found: $($rootMarkdown -join ', ')"

$readmeFiles = @('README.md', 'README.ru.md', 'README.zh-CN.md')
foreach ($readme in $readmeFiles) {
    $text = Get-Content -LiteralPath (Join-Path $repoRoot $readme) -Raw
    Test-Condition ($text.IndexOf($canonicalVersion, [StringComparison]::Ordinal) -ge 0) "$readme must mention release $canonicalVersion."
    Test-Condition ($text.IndexOf('.NET 10', [StringComparison]::OrdinalIgnoreCase) -ge 0) "$readme must document .NET 10."
    Test-Condition ($text.IndexOf('WinUI 3', [StringComparison]::OrdinalIgnoreCase) -ge 0) "$readme must document WinUI 3."
    Test-Condition ($text.IndexOf('DNSCrypt', [StringComparison]::OrdinalIgnoreCase) -ge 0) "$readme must document DNSCrypt."
    Test-Condition ($text.IndexOf('Managed', [StringComparison]::OrdinalIgnoreCase) -ge 0) "$readme must document managed routing."
}

$applicationInfo = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Core\ApplicationInfo.cs') -Raw
Test-Condition ($applicationInfo -match 'public const string Version = "(?<version>\d+\.\d+\.\d+\.\d+)";') 'ApplicationInfo.Version is missing.'
Test-Condition ([Version]$Matches.version -eq [Version]"$canonicalVersion.0") 'ApplicationInfo.Version does not match Directory.Build.props.'
foreach ($manifestPath in @('Shadowsocks.WinUI\app.manifest', 'Shadowsocks.NetworkService\app.manifest')) {
    $manifest = Get-XmlDocument $manifestPath
    $identity = $manifest.SelectSingleNode("/*[local-name()='assembly']/*[local-name()='assemblyIdentity']")
    Test-Condition ($null -ne $identity -and [Version]$identity.GetAttribute('version') -eq [Version]"$canonicalVersion.0") "$manifestPath version does not match Directory.Build.props."
}
$changes = Get-Content -LiteralPath (Join-Path $repoRoot 'CHANGES') -Raw
$escapedFourPartVersion = [regex]::Escape("$canonicalVersion.0")
Test-Condition ($changes -match "(?m)^$escapedFourPartVersion \d{4}-\d{2}-\d{2}\s*$") "CHANGES is missing the current release heading for $canonicalVersion.0."

& (Join-Path $PSScriptRoot 'Validate-DnsCrypt.ps1')

Write-Information "Repository structure validated for Shadowsocks Reborn $canonicalVersion." -InformationAction Continue
