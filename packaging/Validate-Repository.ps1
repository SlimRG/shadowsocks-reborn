[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot

function Get-XmlChildText {
    param(
        [Parameter(Mandatory)][System.Xml.XmlNode]$Node,
        [Parameter(Mandatory)][string]$Name
    )

    $child = $Node.SelectSingleNode("./*[local-name()='$Name']")
    if ($null -eq $child) {
        return $null
    }

    return [string]$child.InnerText
}

function Get-XmlMetadataText {
    param(
        [Parameter(Mandatory)][System.Xml.XmlElement]$Node,
        [Parameter(Mandatory)][string]$Name
    )

    # MSBuild item metadata may be written either as an XML attribute
    # (for example Version="1.2.3" or LogicalName="...") or as a
    # child element (<Version>1.2.3</Version>).  Accept both canonical
    # representations so repository validation is independent of formatting.
    if ($Node.HasAttribute($Name)) {
        return [string]$Node.GetAttribute($Name)
    }

    return Get-XmlChildText -Node $Node -Name $Name
}

function Get-XmlAttributeText {
    param(
        [Parameter(Mandatory)][System.Xml.XmlElement]$Node,
        [Parameter(Mandatory)][string]$Name
    )

    if (-not $Node.HasAttribute($Name)) {
        return $null
    }

    return [string]$Node.GetAttribute($Name)
}

function Get-ProjectPropertyValues {
    param(
        [Parameter(Mandatory)][xml]$Project,
        [Parameter(Mandatory)][string]$Name
    )

    return @(
        $Project.SelectNodes("/*[local-name()='Project']/*[local-name()='PropertyGroup']/*[local-name()='$Name']") |
            ForEach-Object { [string]$_.InnerText } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
}

function Get-ProjectPropertyValue {
    param(
        [Parameter(Mandatory)][xml]$Project,
        [Parameter(Mandatory)][string]$Name
    )

    $values = @(Get-ProjectPropertyValues -Project $Project -Name $Name)
    if ($values.Count -eq 0) {
        return $null
    }

    return $values[0]
}

function Get-ProjectItems {
    param(
        [Parameter(Mandatory)][xml]$Project,
        [Parameter(Mandatory)][string]$Name
    )

    return @($Project.SelectNodes("/*[local-name()='Project']/*[local-name()='ItemGroup']/*[local-name()='$Name']"))
}

function Get-ProjectTargets {
    param([Parameter(Mandatory)][xml]$Project)

    return @($Project.SelectNodes("/*[local-name()='Project']/*[local-name()='Target']"))
}

function Get-ProjectReferences {
    param([Parameter(Mandatory)][xml]$Project)

    # PowerShell enumerates arrays returned from functions. Callers that need
    # collection semantics must materialize the result with @(...), especially
    # under Set-StrictMode where $null.Count is an error.
    return @(
        Get-ProjectItems -Project $Project -Name 'ProjectReference' |
            ForEach-Object { Get-XmlAttributeText -Node $_ -Name 'Include' } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
}
$missing = [System.Collections.Generic.List[string]]::new()

$resxFiles = @(
    Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Filter '*.resx' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
)

foreach ($resxFile in $resxFiles) {
    [xml]$document = Get-Content -LiteralPath $resxFile.FullName -Raw

    foreach ($dataNode in $document.SelectNodes('/root/data')) {
        $typeName = $dataNode.GetAttribute('type')
        if ($typeName -notlike '*ResXFileRef*') {
            continue
        }

        $valueNode = $dataNode.SelectSingleNode('value')
        if ($null -eq $valueNode) {
            continue
        }

        $reference = $valueNode.InnerText
        $separatorIndex = $reference.IndexOf(';')
        if ($separatorIndex -ge 0) {
            $reference = $reference.Substring(0, $separatorIndex)
        }

        if ([string]::IsNullOrWhiteSpace($reference)) {
            continue
        }

        $resolvedPath = [System.IO.Path]::GetFullPath(
            (Join-Path $resxFile.DirectoryName $reference))

        if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
            $relativeResx = [System.IO.Path]::GetRelativePath($repoRoot, $resxFile.FullName)
            $relativeTarget = [System.IO.Path]::GetRelativePath($repoRoot, $resolvedPath)
            $missing.Add("$relativeResx -> $relativeTarget")
        }
    }
}

if ($missing.Count -gt 0) {
    throw "Missing ResX linked resources:`n  $($missing -join "`n  ")"
}

Write-Host "Validated $($resxFiles.Count) .resx files: all linked resources exist."

$expectedProjects = @(
    'Shadowsocks.Core\Shadowsocks.Core.csproj',
    'Shadowsocks.Windows\Shadowsocks.Windows.csproj',
    'Shadowsocks.Windows.WinUI\Shadowsocks.Windows.WinUI.csproj',
    'Shadowsocks.WinUI\Shadowsocks.WinUI.csproj',
    'Shadowsocks.NetworkService\Shadowsocks.NetworkService.csproj',
    'Shadowsocks.UnitTests\Shadowsocks.UnitTests.csproj'
)
foreach ($relativeProject in $expectedProjects) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $relativeProject) -PathType Leaf)) {
        throw "Expected project is missing: $relativeProject"
    }
}

if (Test-Path -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Engine')) {
    throw 'Legacy Shadowsocks.Engine directory must not exist after Core/Windows separation.'
}

# Phase 9: the desktop presentation graph is WinUI-only after verified parity.
$legacyUiRoot = Join-Path $repoRoot 'Shadowsocks.UI'
if (Test-Path -LiteralPath $legacyUiRoot) {
    throw 'Phase 9 requires the retired Shadowsocks.UI WinForms/WPF project to stay removed.'
}

$allProjectFiles = @(
    Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Filter '*.csproj' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
)
$legacyDesktopPackages = @(
    'AvalonEdit',
    'MdXaml',
    'Pharmacist.Common',
    'ReactiveUI.Events.WPF',
    'ReactiveUI.WPF',
    'WPFLocalizeExtension',
    'XAMLMarkupExtensions',
    'ZXing.Net.Bindings.Windows.Compatibility'
)
foreach ($projectFile in $allProjectFiles) {
    [xml]$projectXml = Get-Content -LiteralPath $projectFile.FullName -Raw
    $projectSdk = Get-XmlAttributeText -Node $projectXml.DocumentElement -Name 'Sdk'
    if ($projectSdk -eq 'Microsoft.NET.Sdk.WindowsDesktop') {
        throw "Phase 9 forbids Microsoft.NET.Sdk.WindowsDesktop: $($projectFile.FullName)"
    }

    $useWpf = Get-ProjectPropertyValue -Project $projectXml -Name 'UseWPF'
    $useWindowsForms = Get-ProjectPropertyValue -Project $projectXml -Name 'UseWindowsForms'
    if ($useWpf -eq 'true' -or $useWindowsForms -eq 'true') {
        throw "Phase 9 forbids UseWPF/UseWindowsForms: $($projectFile.FullName)"
    }

    $packageNames = @(
        Get-ProjectItems -Project $projectXml -Name 'PackageReference' |
            ForEach-Object { Get-XmlAttributeText -Node $_ -Name 'Include' } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    foreach ($packageName in $legacyDesktopPackages) {
        if ($packageNames -contains $packageName) {
            throw "Retired WinForms/WPF package '$packageName' returned in $($projectFile.FullName)."
        }
    }
}

$desktopSourceFiles = @(
    Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Filter '*.cs' |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
)
$legacyDesktopSourcePatterns = @(
    'System\.Windows\.Forms',
    '^\s*using\s+System\.Windows(?:\.|\s*;)',
    '^\s*using\s+ReactiveUI(?:\.|\s*;)',
    'WPFLocalizeExtension',
    'ICSharpCode\.AvalonEdit',
    '\bMdXaml\b'
)
foreach ($pattern in $legacyDesktopSourcePatterns) {
    $match = $desktopSourceFiles | Select-String -Pattern $pattern | Select-Object -First 1
    if ($null -ne $match) {
        $relative = [System.IO.Path]::GetRelativePath($repoRoot, $match.Path)
        throw "Retired WinForms/WPF source dependency returned: ${relative}:$($match.LineNumber): $($match.Line.Trim())"
    }
}

foreach ($retiredFileName in @('MenuViewController.cs', 'ConfigForm.cs', 'LogForm.cs')) {
    $retiredFile = Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Filter $retiredFileName |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        Select-Object -First 1
    if ($null -ne $retiredFile) {
        throw "Retired desktop UI file returned: $($retiredFile.FullName)"
    }
}

Write-Host 'Validated retired desktop UI checks: WinUI-only project/package/source graph.'


$appSettingsPath = Join-Path $repoRoot 'appsettings.json'
if (-not (Test-Path -LiteralPath $appSettingsPath -PathType Leaf)) {
    throw 'Root appsettings.json is required for build-time application configuration.'
}
try {
    $appSettings = Get-Content -LiteralPath $appSettingsPath -Raw | ConvertFrom-Json
}
catch {
    throw "appsettings.json is not valid JSON: $($_.Exception.Message)"
}
if ([string]::IsNullOrWhiteSpace($appSettings.Logging.FilePath) -or
    [string]::IsNullOrWhiteSpace($appSettings.Logging.FallbackFilePath) -or
    [string]::IsNullOrWhiteSpace($appSettings.Logging.MinimumLevel) -or
    [string]::IsNullOrWhiteSpace($appSettings.Logging.VerboseMinimumLevel)) {
    throw 'appsettings.json must define Logging.FilePath, Logging.FallbackFilePath, Logging.MinimumLevel, and Logging.VerboseMinimumLevel.'
}

$nlogConfigFiles = @(
    Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Filter 'NLog.config' |
        Where-Object { $_.FullName -notmatch '[\/](bin|obj)[\/]' }
)
if ($nlogConfigFiles.Count -ne 0) {
    throw "NLog.config must not exist; logging is configured from appsettings.json. Found: $($nlogConfigFiles.FullName -join ', ')"
}

$nlogConfigSymbols = @(
    Get-ChildItem -LiteralPath $repoRoot -Recurse -File -Filter '*.cs' |
        Where-Object { $_.FullName -notmatch '[\/](bin|obj)[\/]' } |
        Select-String -Pattern '\bNLogConfig\b|TouchAndApplyNLogConfig|LoadConfiguration\(NLOG_CONFIG' -ErrorAction SilentlyContinue
)
if ($nlogConfigSymbols.Count -ne 0) {
    $first = $nlogConfigSymbols[0]
    throw "Legacy NLog.config code remains: $($first.Path):$($first.LineNumber): $($first.Line.Trim())"
}

$solutionPath = Join-Path $repoRoot 'shadowsocks-reborn.sln'
if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw 'Expected solution is missing: shadowsocks-reborn.sln'
}

$nuGetConfigPath = Join-Path $repoRoot 'NuGet.Config'
if (-not (Test-Path -LiteralPath $nuGetConfigPath -PathType Leaf)) {
    throw 'NuGet.Config is required so Visual Studio clean builds automatically restore PackageReference assets.'
}
[xml]$nuGetConfig = Get-Content -LiteralPath $nuGetConfigPath -Raw
$restoreSettings = @{}
$restoreSettingNodes = @($nuGetConfig.SelectNodes("/*[local-name()='configuration']/*[local-name()='packageRestore']/*[local-name()='add']"))
foreach ($addNode in $restoreSettingNodes) {
    $key = Get-XmlAttributeText -Node $addNode -Name 'key'
    $value = Get-XmlAttributeText -Node $addNode -Name 'value'
    if (-not [string]::IsNullOrWhiteSpace($key)) {
        $restoreSettings[$key] = $value
    }
}
if ($restoreSettings['enabled'] -ne 'True' -or $restoreSettings['automatic'] -ne 'True') {
    throw 'NuGet.Config must enable packageRestore/enabled and packageRestore/automatic for Visual Studio clean-build restore.'
}

$solutionText = Get-Content -LiteralPath $solutionPath -Raw
$sdkCSharpProjectType = '{9A19103F-16F7-4668-BE54-9A1E7A4F7556}'
$legacyCSharpProjectType = '{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}'
if ($solutionText -match [regex]::Escape($legacyCSharpProjectType)) {
    throw 'SDK-style C# projects must use the CPS project-system GUID in shadowsocks-reborn.sln.'
}
$solutionProjectLines = @($solutionText -split "`r?`n" | Where-Object { $_ -match '^Project\(' })
if ($solutionProjectLines.Count -ne 6 -or @($solutionProjectLines | Where-Object { $_ -notmatch [regex]::Escape($sdkCSharpProjectType) }).Count -ne 0) {
    throw 'All six C# projects in shadowsocks-reborn.sln must use the SDK-style CPS project type GUID.'
}

$packageSources = @($nuGetConfig.SelectNodes("/*[local-name()='configuration']/*[local-name()='packageSources']/*[local-name()='add']"))
if ($packageSources.Count -ne 1 -or
    (Get-XmlAttributeText -Node $packageSources[0] -Name 'key') -ne 'nuget.org' -or
    (Get-XmlAttributeText -Node $packageSources[0] -Name 'value') -ne 'https://api.nuget.org/v3/index.json') {
    throw 'NuGet.Config must use the deterministic nuget.org v3 source for repository package restore.'
}
$networkServiceGuid = '{A36C6BB9-24CA-46A0-9FA7-EB960386E1B9}'
$winUiProjectPattern = '(?s)Project\([^\r\n]+\) = "Shadowsocks.WinUI".*?EndProject'
$winUiProjectBlock = [regex]::Match($solutionText, $winUiProjectPattern).Value
if ([string]::IsNullOrWhiteSpace($winUiProjectBlock) -or $winUiProjectBlock -notmatch [regex]::Escape($networkServiceGuid)) {
    throw 'Shadowsocks.WinUI must declare Shadowsocks.NetworkService as a solution ProjectDependency so Visual Studio builds the helper first without a ProjectReference.'
}
[xml]$coreProject = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Core\Shadowsocks.Core.csproj') -Raw

$coreEmbeddedResources = @(Get-ProjectItems -Project $coreProject -Name 'EmbeddedResource')
$appSettingsResource = @(
    $coreEmbeddedResources |
        Where-Object { (Get-XmlMetadataText -Node $_ -Name 'LogicalName') -eq 'Shadowsocks.Core.appsettings.json' }
)
if ($appSettingsResource.Count -ne 1 -or (Get-XmlAttributeText -Node $appSettingsResource[0] -Name 'Include') -ne '..\appsettings.json') {
    throw 'Shadowsocks.Core must embed the root appsettings.json as Shadowsocks.Core.appsettings.json.'
}
Write-Host 'Validated embedded appsettings.json resource metadata.'
if (@($coreEmbeddedResources | Where-Object { (Get-XmlAttributeText -Node $_ -Name 'Include') -match 'NLog\.config' }).Count -ne 0) {
    throw 'Shadowsocks.Core must not embed NLog.config.'
}

$coreTargetFramework = Get-ProjectPropertyValue -Project $coreProject -Name 'TargetFramework'
if ($coreTargetFramework -ne 'net10.0') {
    throw "Shadowsocks.Core must target plain net10.0; TargetFramework is '$coreTargetFramework'."
}
if (@(Get-ProjectPropertyValues -Project $coreProject -Name 'RuntimeIdentifier').Count -ne 0 -or
    @(Get-ProjectPropertyValues -Project $coreProject -Name 'EnableWindowsTargeting').Count -ne 0) {
    throw 'Shadowsocks.Core must not declare a Windows RuntimeIdentifier or EnableWindowsTargeting.'
}
$corePackages = @(
    Get-ProjectItems -Project $coreProject -Name 'PackageReference' |
        ForEach-Object { Get-XmlAttributeText -Node $_ -Name 'Include' } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
)
foreach ($forbiddenPackage in @('System.Management', 'System.Drawing.Common')) {
    if ($corePackages -contains $forbiddenPackage) {
        throw "Windows-only package leaked into Shadowsocks.Core: $forbiddenPackage"
    }
}
$coreReferences = @(Get-ProjectReferences -Project $coreProject)
if ($coreReferences.Count -ne 0) {
    throw "Shadowsocks.Core must not reference platform or presentation projects: $($coreReferences -join ', ')"
}

$coreSources = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Core') -Recurse -File -Filter '*.cs'
$coreForbiddenPatterns = @(
    'System\.Windows\.Forms',
    'System\.Windows(?:\.|;)',
    'Microsoft\.Win32',
    'System\.Management',
    '\[DllImport\(',
    'Verb\s*=\s*"runas"',
    'Shadowsocks\.(?:View|Views|ViewModels)',
    '\bProgram\.'
)
foreach ($pattern in $coreForbiddenPatterns) {
    $match = $coreSources | Select-String -Pattern $pattern | Select-Object -First 1
    if ($null -ne $match) {
        $relative = [System.IO.Path]::GetRelativePath($repoRoot, $match.Path)
        throw "Windows/UI dependency leaked into Shadowsocks.Core: ${relative}:$($match.LineNumber): $($match.Line.Trim())"
    }
}

[xml]$windowsProject = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Windows\Shadowsocks.Windows.csproj') -Raw
$windowsRuntimeIdentifiers = @(Get-ProjectPropertyValues -Project $windowsProject -Name 'RuntimeIdentifier')
if ($windowsRuntimeIdentifiers.Count -ne 0) {
    throw 'Shadowsocks.Windows is a class library and must not declare RuntimeIdentifier; the final executable owns the RID.'
}
$windowsAppendRid = Get-ProjectPropertyValue -Project $windowsProject -Name 'AppendRuntimeIdentifierToOutputPath'
if ($windowsAppendRid -ne 'false') {
    throw "Shadowsocks.Windows must keep AppendRuntimeIdentifierToOutputPath=false so all ProjectReference consumers use a stable library output path; found '$windowsAppendRid'."
}
$windowsReferences = @(Get-ProjectReferences -Project $windowsProject)
if ($windowsReferences -notcontains '..\Shadowsocks.Core\Shadowsocks.Core.csproj') {
    throw 'Shadowsocks.Windows must reference Shadowsocks.Core.'
}
if ($windowsReferences.Count -ne 1) {
    throw "Shadowsocks.Windows must reference only Shadowsocks.Core; found: $($windowsReferences -join ', ')"
}
$windowsSources = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Windows') -Recurse -File -Filter '*.cs'
$windowsUiPatterns = @(
    'System\.Windows\.Forms',
    'System\.Windows(?:\.|;)',
    'ReactiveUI',
    'WPFLocalizeExtension',
    'Microsoft\.UI\.Xaml',
    '\bWinUIEx\b',
    'Shadowsocks\.(?:View|Views|ViewModels)',
    '\bProgram\.'
)
foreach ($pattern in $windowsUiPatterns) {
    $match = $windowsSources | Select-String -Pattern $pattern | Select-Object -First 1
    if ($null -ne $match) {
        $relative = [System.IO.Path]::GetRelativePath($repoRoot, $match.Path)
        throw "Presentation dependency leaked into Shadowsocks.Windows: ${relative}:$($match.LineNumber): $($match.Line.Trim())"
    }
}

[xml]$winUiProject = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\Shadowsocks.WinUI.csproj') -Raw
$winUiAllowUnsafeBlocks = Get-ProjectPropertyValue -Project $winUiProject -Name 'AllowUnsafeBlocks'
if ($winUiAllowUnsafeBlocks -ne 'true') {
    throw 'Shadowsocks.WinUI must enable AllowUnsafeBlocks because LibraryImport source generation and the AppInstance redirect wait path emit unsafe code.'
}
$winUiRuntimeIdentifiers = Get-ProjectPropertyValue -Project $winUiProject -Name 'RuntimeIdentifiers'
if ($winUiRuntimeIdentifiers -ne 'win-x64') {
    throw "Shadowsocks.WinUI must declare RuntimeIdentifiers=win-x64 (plural) for current WinUI/MSBuild RID negotiation; found '$winUiRuntimeIdentifiers'."
}
if (@(Get-ProjectPropertyValues -Project $winUiProject -Name 'RuntimeIdentifier').Count -ne 0) {
    throw 'Shadowsocks.WinUI must not declare singular RuntimeIdentifier in the project file; the publish command/profile may still select -r win-x64.'
}
$winUiXamlFiles = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI') -File -Filter '*.xaml')
if ($winUiXamlFiles.Count -ne 1 -or $winUiXamlFiles[0].Name -ne 'App.xaml') {
    throw "Shadowsocks.WinUI must keep exactly one XAML file (App.xaml) for application-level Fluent resources; found: $($winUiXamlFiles.Name -join ', ')"
}
$winUiAppXamlPath = Join-Path $repoRoot 'Shadowsocks.WinUI\App.xaml'
$winUiAppXaml = Get-Content -LiteralPath $winUiAppXamlPath -Raw
if ($winUiAppXaml -notmatch 'XamlControlsResources') {
    throw 'Shadowsocks.WinUI App.xaml must own XamlControlsResources so Fluent control resources load through the normal WinUI application lifecycle.'
}
$applicationDefinition = @(Get-ProjectItems -Project $winUiProject -Name 'ApplicationDefinition')
if ($applicationDefinition.Count -ne 1 -or (Get-XmlAttributeText -Node $applicationDefinition[0] -Name 'Include') -ne 'App.xaml') {
    throw 'Shadowsocks.WinUI must compile App.xaml explicitly as the single ApplicationDefinition.'
}
$winUiDefineConstants = Get-ProjectPropertyValue -Project $winUiProject -Name 'DefineConstants'
if ($winUiDefineConstants -notmatch 'DISABLE_XAML_GENERATED_MAIN') {
    throw 'Shadowsocks.WinUI must define DISABLE_XAML_GENERATED_MAIN so Program.Main can perform AppInstance redirection before starting WinUI.'
}
$winUiDefaultPages = Get-ProjectPropertyValue -Project $winUiProject -Name 'EnableDefaultPageItems'
if ($winUiDefaultPages -ne 'false') {
    throw 'Shadowsocks.WinUI must keep EnableDefaultPageItems=false; MainWindow and feature pages remain code-only.'
}
$winUiProgramPath = Join-Path $repoRoot 'Shadowsocks.WinUI\Program.cs'
if (-not (Test-Path -LiteralPath $winUiProgramPath)) {
    throw 'Shadowsocks.WinUI is missing Program.cs.'
}
$winUiProgram = Get-Content -LiteralPath $winUiProgramPath -Raw
if ($winUiProgram -match 'XamlGeneratedProgram\.XamlGeneratedMain') {
    throw 'Shadowsocks.WinUI Program.cs must not call the generated XamlGeneratedProgram entry point directly.'
}
if ($winUiProgram -notmatch 'ComWrappersSupport\.InitializeComWrappers\(\)' -or
    $winUiProgram -notmatch 'Application\.Start\(' -or
    $winUiProgram -notmatch 'DispatcherQueueSynchronizationContext') {
    throw 'Shadowsocks.WinUI Program.cs must use the public WinUI custom-Main bootstrap: InitializeComWrappers + Application.Start + DispatcherQueueSynchronizationContext.'
}
if ($winUiProgram -notmatch 'using AppRuntimeEnvironment = Shadowsocks\.Core\.RuntimeEnvironment;' -or
    $winUiProgram -notmatch 'AppRuntimeEnvironment\.Initialize\(' -or
    $winUiProgram -match '(?m)^\s*RuntimeEnvironment\.') {
    throw 'Shadowsocks.WinUI Program.cs must alias Shadowsocks.Core.RuntimeEnvironment because System.Runtime.InteropServices exposes a conflicting RuntimeEnvironment type.'
}
$winUiAppSourceForStartup = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\App.cs') -Raw
if ($winUiAppSourceForStartup -notmatch 'public App\(\)' -or
    $winUiAppSourceForStartup -notmatch 'ConfigureStartupContext\(' -or
    $winUiProgram -notmatch 'App\.ConfigureStartupContext\(') {
    throw 'Shadowsocks.WinUI App.xaml requires a parameterless App constructor; AppInstance/activation state must be supplied through ConfigureStartupContext before Application.Start creates App.'
}
$winUiXamlReferenceTarget = @(Get-ProjectTargets -Project $winUiProject | Where-Object { (Get-XmlAttributeText -Node $_ -Name 'Name') -eq 'PrepareWinUIXamlProjectReferences' })[0]
$winUiXamlReferenceBeforeTargets = if ($null -eq $winUiXamlReferenceTarget) { $null } else { Get-XmlAttributeText -Node $winUiXamlReferenceTarget -Name 'BeforeTargets' }
if ($null -eq $winUiXamlReferenceTarget -or
    $winUiXamlReferenceBeforeTargets -notmatch 'DesignTimeMarkupCompilation' -or
    $winUiXamlReferenceBeforeTargets -notmatch 'MarkupCompilePass1' -or
    $winUiXamlReferenceBeforeTargets -notmatch 'XamlPreCompile') {
    throw 'Shadowsocks.WinUI must materialize Core/Windows ProjectReference outputs before WinUI XAML design-time/compile passes to avoid WMC1006.'
}
$winUiXamlReferenceMsBuild = @($winUiXamlReferenceTarget.SelectNodes("./*[local-name()='MSBuild']"))
if ($winUiXamlReferenceMsBuild.Count -ne 6 -or
    (Get-XmlAttributeText -Node $winUiXamlReferenceMsBuild[0] -Name 'Targets') -ne 'Restore' -or
    (Get-XmlAttributeText -Node $winUiXamlReferenceMsBuild[1] -Name 'Targets') -ne 'Restore' -or
    (Get-XmlAttributeText -Node $winUiXamlReferenceMsBuild[2] -Name 'Targets') -ne 'Restore' -or
    (Get-XmlAttributeText -Node $winUiXamlReferenceMsBuild[3] -Name 'Targets') -ne 'Build' -or
    (Get-XmlAttributeText -Node $winUiXamlReferenceMsBuild[4] -Name 'Targets') -ne 'Build' -or
    (Get-XmlAttributeText -Node $winUiXamlReferenceMsBuild[5] -Name 'Targets') -ne 'Build') {
    throw 'WinUI XAML prerequisite handling must keep Restore and Build as separate MSBuild evaluations for Core, Windows, and Windows.WinUI.'
}

$winUiReferences = @(Get-ProjectReferences -Project $winUiProject)
foreach ($requiredReference in @('..\Shadowsocks.Core\Shadowsocks.Core.csproj', '..\Shadowsocks.Windows\Shadowsocks.Windows.csproj', '..\Shadowsocks.Windows.WinUI\Shadowsocks.Windows.WinUI.csproj')) {
    if ($winUiReferences -notcontains $requiredReference) {
        throw "Shadowsocks.WinUI is missing required reference: $requiredReference"
    }
}
if ($winUiReferences.Count -ne 3) {
    throw "Shadowsocks.WinUI must reference Core, Windows, and Windows.WinUI only; executable NetworkService must be built explicitly, not through ProjectReference. Found: $($winUiReferences -join ', ')"
}
if ($winUiReferences -contains '..\Shadowsocks.NetworkService\Shadowsocks.NetworkService.csproj') {
    throw 'Shadowsocks.WinUI must not ProjectReference the executable NetworkService because self-contained WinUI publish would fail with NETSDK1150.'
}
$networkServiceProjectProperty = Get-ProjectPropertyValue -Project $winUiProject -Name 'NetworkServiceProject'
if ([string]::IsNullOrWhiteSpace($networkServiceProjectProperty)) {
    throw 'Shadowsocks.WinUI must keep an explicit NetworkServiceProject path for separate helper build/publish.'
}
$networkServiceBuildTarget = @(Get-ProjectTargets -Project $winUiProject | Where-Object { (Get-XmlAttributeText -Node $_ -Name 'Name') -eq 'BuildNetworkServiceForWinUIBootstrap' })[0]
$networkServicePublishTarget = @(Get-ProjectTargets -Project $winUiProject | Where-Object { (Get-XmlAttributeText -Node $_ -Name 'Name') -eq 'PrepareEmbeddedNetworkService' })[0]
if ($null -eq $networkServiceBuildTarget -or $null -eq $networkServicePublishTarget) {
    throw 'Shadowsocks.WinUI must build/publish NetworkService through explicit MSBuild targets, not ProjectReference.'
}

$networkServiceBuildMsBuild = @($networkServiceBuildTarget.SelectNodes("./*[local-name()='MSBuild']"))
if ($networkServiceBuildMsBuild.Count -ne 2 -or
    (Get-XmlAttributeText -Node $networkServiceBuildMsBuild[0] -Name 'Targets') -ne 'Restore' -or
    (Get-XmlAttributeText -Node $networkServiceBuildMsBuild[1] -Name 'Targets') -ne 'Build') {
    throw 'Shadowsocks.WinUI development helper build must run Restore and Build as separate MSBuild evaluations.'
}
$networkServicePublishMsBuild = @($networkServicePublishTarget.SelectNodes("./*[local-name()='MSBuild']"))
if ($networkServicePublishMsBuild.Count -ne 2 -or
    (Get-XmlAttributeText -Node $networkServicePublishMsBuild[0] -Name 'Targets') -ne 'Restore' -or
    (Get-XmlAttributeText -Node $networkServicePublishMsBuild[1] -Name 'Targets') -ne 'Publish') {
    throw 'Shadowsocks.WinUI helper publish must run Restore and Publish as separate MSBuild evaluations.'
}
$embeddedNetworkService = @(
    Get-ProjectItems -Project $winUiProject -Name 'EmbeddedResource' |
        Where-Object { (Get-XmlMetadataText -Node $_ -Name 'LogicalName') -eq 'Shadowsocks.WinUI.Embedded.Shadowsocks.NetworkService.exe' }
)[0]
if ($null -eq $embeddedNetworkService) {
    throw 'Phase 10 must embed the product NetworkService executable as Shadowsocks.WinUI.Embedded.Shadowsocks.NetworkService.exe.'
}
$finalSingleFileTarget = @(Get-ProjectTargets -Project $winUiProject | Where-Object { (Get-XmlAttributeText -Node $_ -Name 'Name') -eq 'ValidateFinalSingleFileLayout' })[0]
if ($null -eq $finalSingleFileTarget) {
    throw 'Phase 10 requires a publish-time validator that enforces a single Shadowsocks.exe output.'
}
$winUiTargetFramework = Get-ProjectPropertyValue -Project $winUiProject -Name 'TargetFramework'
if ($winUiTargetFramework -ne 'net10.0-windows10.0.26100.0') {
    throw "Shadowsocks.WinUI must compile against the current Windows 11 SDK TFM while retaining Windows 10 as the minimum OS; TargetFramework is '$winUiTargetFramework'."
}
$winUiMinimumOs = Get-ProjectPropertyValue -Project $winUiProject -Name 'SupportedOSPlatformVersion'
if ($winUiMinimumOs -ne '10.0.19041.0') {
    throw "Shadowsocks.WinUI must keep Windows 10 build 19041 as the supported minimum; SupportedOSPlatformVersion is '$winUiMinimumOs'."
}
$winUiUseWinUI = Get-ProjectPropertyValue -Project $winUiProject -Name 'UseWinUI'
$winUiPackageType = Get-ProjectPropertyValue -Project $winUiProject -Name 'WindowsPackageType'
$winUiAssemblyName = Get-ProjectPropertyValue -Project $winUiProject -Name 'AssemblyName'
if ($winUiUseWinUI -ne 'true' -or $winUiPackageType -ne 'None') {
    throw 'Shadowsocks.WinUI must be an unpackaged WinUI 3 project (UseWinUI=true, WindowsPackageType=None).'
}
if ($winUiAssemblyName -ne 'Shadowsocks') {
    throw "Phase-10 product shell must publish Shadowsocks.exe; AssemblyName is '$winUiAssemblyName'."
}
$winUiPackages = @(
    Get-ProjectItems -Project $winUiProject -Name 'PackageReference' |
        ForEach-Object { "$(Get-XmlAttributeText -Node $_ -Name 'Include')|$(Get-XmlMetadataText -Node $_ -Name 'Version')" }
)
if ($winUiPackages -notcontains 'Microsoft.WindowsAppSDK|2.4.0') {
    throw 'Shadowsocks.WinUI must reference Microsoft.WindowsAppSDK 2.4.0, the current stable Windows App SDK release.'
}
if ($winUiPackages -notcontains 'Microsoft.WindowsAppSDK.WinUI|2.3.6') {
    throw 'Shadowsocks.WinUI must pin Microsoft.WindowsAppSDK.WinUI 2.3.6, the WinUI servicing component required by Windows App SDK 2.4.0.'
}
if ($winUiPackages -notcontains 'Microsoft.Windows.SDK.BuildTools|10.0.28000.2526') {
    throw 'Shadowsocks.WinUI must reference Microsoft.Windows.SDK.BuildTools 10.0.28000.2526 so WinUI/WinRT compile references are deterministic on .NET 10.'
}
if ($winUiPackages | Where-Object { $_ -like 'WinUIEx|*' }) {
    throw 'Shadowsocks.WinUI must not reference WinUIEx directly; Windows-specific tray infrastructure belongs to Shadowsocks.Windows.WinUI.'
}

$windowsPackages = @(
    Get-ProjectItems -Project $windowsProject -Name 'PackageReference' |
        ForEach-Object { "$(Get-XmlAttributeText -Node $_ -Name 'Include')|$(Get-XmlMetadataText -Node $_ -Name 'Version')" }
)
if ($windowsPackages | Where-Object { $_ -like 'Microsoft.WindowsAppSDK*|*' -or $_ -like 'WinUIEx|*' }) {
    throw 'Shadowsocks.Windows must remain UI-framework agnostic so UnitTests and future frontends do not inherit Windows App SDK runtime requirements.'
}

[xml]$windowsWinUiProject = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Windows.WinUI\Shadowsocks.Windows.WinUI.csproj') -Raw
$windowsWinUiPackages = @(
    Get-ProjectItems -Project $windowsWinUiProject -Name 'PackageReference' |
        ForEach-Object { "$(Get-XmlAttributeText -Node $_ -Name 'Include')|$(Get-XmlMetadataText -Node $_ -Name 'Version')" }
)
if ($windowsWinUiPackages -notcontains 'Microsoft.WindowsAppSDK.WinUI|2.3.6') {
    throw 'Shadowsocks.Windows.WinUI must reference Microsoft.WindowsAppSDK.WinUI 2.3.6.'
}
if ($windowsWinUiPackages -notcontains 'WinUIEx|2.9.3') {
    throw 'Shadowsocks.Windows.WinUI must reference WinUIEx 2.9.3 for the notification-area adapter.'
}
if ($windowsWinUiPackages -notcontains 'System.Drawing.Common|10.0.11') {
    throw 'Shadowsocks.Windows.WinUI must reference System.Drawing.Common 10.0.11 for tray artwork and screen capture.'
}
[xml]$unitTestsProject = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.UnitTests\Shadowsocks.UnitTests.csproj') -Raw
$unitTestPackages = @(
    Get-ProjectItems -Project $unitTestsProject -Name 'PackageReference' |
        ForEach-Object { "{0}|{1}" -f (Get-XmlAttributeText -Node $_ -Name 'Include'), (Get-XmlMetadataText -Node $_ -Name 'Version') }
)
if ($unitTestPackages -notcontains 'Microsoft.NET.Test.Sdk|18.9.0') {
    throw 'Shadowsocks.UnitTests must reference Microsoft.NET.Test.Sdk 18.9.0.'
}
if ($windowsWinUiPackages -notcontains 'ZXing.Net|0.16.11') {
    throw 'Shadowsocks.Windows.WinUI must reference the platform-neutral ZXing.Net 0.16.11 package for screen QR scanning.'
}
if ($windowsWinUiPackages | Where-Object { $_ -like 'ZXing.Net.Bindings.Windows.Compatibility|*' }) {
    throw 'Shadowsocks.Windows.WinUI must not reference ZXing.Net.Bindings.Windows.Compatibility because its net9 WindowsBase reference conflicts with the .NET 10 desktop reference set.'
}
$windowsWinUiReferences = @(Get-ProjectReferences -Project $windowsWinUiProject)
if ($windowsWinUiReferences.Count -ne 0) {
    throw "Shadowsocks.Windows.WinUI tray adapter must not pull Core/controller dependencies; found: $($windowsWinUiReferences -join ', ')"
}

[xml]$networkServiceProject = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.NetworkService\Shadowsocks.NetworkService.csproj') -Raw
$networkServiceReferences = @(Get-ProjectReferences -Project $networkServiceProject)
if ($networkServiceReferences.Count -ne 0) {
    throw "Shadowsocks.NetworkService must stay isolated from desktop projects; found: $($networkServiceReferences -join ', ')"
}


$winUiProgramSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\Program.cs') -Raw
$winUiAppSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\App.cs') -Raw
$winUiWindowSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\MainWindow.cs') -Raw
$traySourcePath = Join-Path $repoRoot 'Shadowsocks.Windows.WinUI\Shell\TrayIconService.cs'
if (-not (Test-Path -LiteralPath $traySourcePath -PathType Leaf)) {
    throw 'Phase-7 Windows WinUI integration layer requires Shadowsocks.Windows.WinUI\Shell\TrayIconService.cs.'
}
$winUiTraySourcePath = Join-Path $repoRoot 'Shadowsocks.WinUI\Shell\TrayIconService.cs'
if (Test-Path -LiteralPath $winUiTraySourcePath -PathType Leaf) {
    throw 'Tray infrastructure must not live in Shadowsocks.WinUI; it belongs to Shadowsocks.Windows.WinUI.'
}
$traySource = Get-Content -LiteralPath $traySourcePath -Raw
if ($traySource -match '\?\?\s*static\s+[A-Za-z_][A-Za-z0-9_]*\s*=>') {
    throw 'TrayIconService contains an unparenthesized static lambda on the right side of ??; this is rejected by the C# parser.'
}
$screenQrSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Windows.WinUI\Shell\ScreenQrCodeScanner.cs') -Raw
if ($screenQrSource -match 'ZXing\.Windows\.Compatibility|BitmapLuminanceSource') {
    throw 'WinUI screen QR scanning must use platform-neutral ZXing RGBLuminanceSource and must not use the Windows.Compatibility binding.'
}
if ($screenQrSource -notmatch 'RGBLuminanceSource\.BitmapFormat\.BGRA32') {
    throw 'WinUI screen QR scanner must convert captured 32-bit ARGB bitmaps through ZXing RGBLuminanceSource BGRA32.'
}
$qrScannerSourcePath = Join-Path $repoRoot 'Shadowsocks.Windows.WinUI\Shell\ScreenQrCodeScanner.cs'
if (-not (Test-Path -LiteralPath $qrScannerSourcePath -PathType Leaf)) {
    throw 'Original tray parity requires the Windows.WinUI screen QR scanner.'
}
$qrScannerSource = Get-Content -LiteralPath $qrScannerSourcePath -Raw
if ($qrScannerSource -match 'System\.Windows\.Forms') {
    throw 'WinUI screen QR scanning must not reintroduce Windows Forms.'
}
$windowsCommandLineSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Windows\Shell\WindowsCommandLine.cs') -Raw
if (-not $traySource.TrimStart([char]0xFEFF).StartsWith('#nullable enable') -or
    -not $windowsCommandLineSource.TrimStart([char]0xFEFF).StartsWith('#nullable enable')) {
    throw 'New Windows shell files that use nullable annotations must opt into nullable context locally.'
}
if ($winUiProgramSource -notmatch 'AppInstance\.FindOrRegisterForKey' -or $winUiProgramSource -notmatch 'RedirectActivationToAsync') {
    throw 'Phase-5 WinUI shell must use Windows App SDK AppInstance for single-instance activation redirection.'
}
if ($winUiProgramSource -match '\bMutex\b') {
    throw 'Phase-5 WinUI shell must not reintroduce the legacy Mutex single-instance mechanism.'
}
if ($traySource -notmatch 'WinUIEx' -or
    $traySource -notmatch 'new TrayIcon\(' -or
    $traySource -notmatch 'MenuFlyout' -or
    $traySource -notmatch 'args\.Flyout = flyout') {
    throw 'Phase-7 Windows.WinUI tray service must use WinUIEx.TrayIcon with a WinUI MenuFlyout context menu.'
}
if ($traySource -match 'Shell_NotifyIcon|TrackPopupMenu|CreatePopupMenu|RegisterClassEx|CreateWindowEx|SetWindowLongPtr|CallWindowProc|UnmanagedCallersOnly') {
    throw 'Phase-7 Windows.WinUI tray adapter must not reintroduce custom Shell_NotifyIcon/window-procedure/context-menu interop; WinUIEx owns it.'
}
if ($traySource -notmatch 'args\.Flyout\s*=\s*flyout') {
    throw 'Tray context menus must use the stock WinUIEx Flyout path; the non-activating host experiment was reverted.'
}
if ($traySource -notmatch 'Glyph\s*=\s*"\\uEA18"' -or $traySource -notmatch 'Segoe Fluent Icons') {
    throw 'Administrator traffic mode must display the UAC/shield glyph before its label.'
}
foreach ($trayColorToken in @(
    'TraySystemProxyMode.Global => Color.FromArgb(255, 45, 84, 128)',
    'TraySystemProxyMode.Pac when state.UseOnlinePac => Color.FromArgb(255, 42, 112, 91)',
    'TraySystemProxyMode.Pac => Color.FromArgb(255, 166, 105, 38)',
    '_ => Color.FromArgb(255, 91, 101, 115)',
    '_state.UseOnlinePac != state.UseOnlinePac'
)) {
    if ($traySource -notmatch [regex]::Escape($trayColorToken)) {
        throw "Tray proxy-state color mapping is missing or regressed: $trayColorToken"
    }
}
foreach ($pacParityToken in @(
    'bool localPac = !state.UseOnlinePac',
    'if (localPac)',
    'if (state.UseOnlinePac)'
)) {
    if ($traySource -notmatch [regex]::Escape($pacParityToken)) {
        throw "PAC tray scenario-visibility token is missing: $pacParityToken"
    }
}
if ($traySource -match 'isEnabled:\s*localPac|isEnabled:\s*state\.UseOnlinePac') {
    throw 'Unavailable PAC tray actions must be omitted rather than rendered disabled.'
}
foreach ($mainWindowOnlyTrayToken in @(
    'ToggleVerboseLogging',
    'ToggleShowPluginOutput',
    'ToggleAutoCheckUpdates',
    'WriteTranslationTemplate',
    'Write translation template',
    'Scan QRCode from Screen',
    'Import URL from Clipboard',
    'Copy Local PAC URL')) {
    if ($traySource -match [regex]::Escape($mainWindowOnlyTrayToken)) {
        throw "Settings-only action leaked back into the tray menu: $mainWindowOnlyTrayToken"
    }
}
if ($winUiAppSource -notmatch 'EditOnlinePacUrlAsync\(enableOnlineAfterSave: true\)' -or
    $winUiAppSource -notmatch 'EditOnlinePacUrlAsync\(enableOnlineAfterSave: false\)') {
    throw 'Online PAC tray scenarios must preserve the original edit-before-enable behavior.'
}

foreach ($trayParityToken in @(
    'BuildSystemProxyMenu',
    'BuildTrafficMenu',
    'BuildServersMenu',
    'BuildPacMenu',
    'BuildHelpMenu',
    'LeftDoubleClick',
    'UpdateActivity')) {
    if ($traySource -notmatch [regex]::Escape($trayParityToken)) {
        throw "Original tray behavior is missing required token: $trayParityToken"
    }
}
if ($winUiWindowSource -notmatch 'AppWindow\.Hide\(\)' -or $winUiWindowSource -notmatch 'AppWindow\.Show\(true\)') {
    throw 'Phase-5 window shell must hide/show through AppWindow.'
}
if ($winUiWindowSource -notmatch 'args\.Cancel = true') {
    throw 'Phase-5 close handling must cancel the system close operation and hide to tray.'
}
if ($winUiAppSource -notmatch 'AutoStartup\.RegisterForRestart\(true\)' -or $winUiAppSource -notmatch 'AutoStartup\.Set\(') {
    throw 'Phase-5 WinUI shell must wire the existing Windows startup infrastructure.'
}
if ($winUiAppSource -notmatch '--start-hidden') {
    throw 'Phase-5 Start with Windows must preserve the hidden-start compatibility argument.'
}
if ($winUiAppSource -match '_window\.Activate\(\)') {
    throw 'Normal WinUI startup must not eagerly activate MainWindow; the classic Shadowsocks client is tray-first after first run.'
}
if ($winUiWindowSource -notmatch '_hasBeenActivated' -or
    $winUiWindowSource -notmatch 'if \(!_hasBeenActivated\)' -or
    $winUiWindowSource -notmatch 'Activate\(\)') {
    throw 'MainWindow must lazily activate on the first explicit ShowFromTray request.'
}
if ($winUiProgramSource -match 'ShowAlreadyRunningMessage' -or
    $winUiProgramSource -match 'MessageBoxW' -or
    $winUiProgramSource -match 'Find shadowsocks-reborn icon in your notify tray') {
    throw 'The WinUI bootstrap must not display the legacy second-launch Win32 MessageBox.'
}
if ($winUiProgramSource -notmatch 'return RedirectActivation\(activationArguments, keyInstance\);' -or
    $winUiProgramSource -notmatch 'internal static bool IsPlainLaunch') {
    throw 'A second launch must redirect activation to the existing WinUI instance.'
}
if ($winUiAppSource -notmatch 'Program\.IsPlainLaunch' -or
    $winUiAppSource -notmatch 'ShowAlreadyRunningDialogAsync') {
    throw 'The running WinUI instance must handle a redirected plain launch through the Fluent already-running dialog path.'
}
if ($winUiWindowSource -notmatch 'public async Task ShowAlreadyRunningDialogAsync\(\)' -or
    $winUiWindowSource -notmatch 'ContentDialogButton\.Close' -or
    $winUiWindowSource -notmatch 'The existing instance has been brought to the foreground\.') {
    throw 'MainWindow must provide the localized Fluent ContentDialog used for second-launch notification.'
}
if ($winUiAppSource -notmatch 'WinUIHotkeyManager' -or $winUiAppSource -notmatch 'ShutdownAndExit') {
    throw 'Phase-5 WinUI shell must initialize global hotkeys and own graceful shutdown.'
}
if ($winUiAppSource -notmatch '_controller\.Stop\(\)' -or $winUiAppSource -notmatch '_trayIcon\.Dispose\(\)') {
    throw 'Phase-5 Exit must dispose tray resources and stop the controller.'
}
if ($winUiAppSource -notmatch '\bExit\(\);') {
    throw 'Phase-5 tray Exit must terminate the Microsoft.UI.Xaml.Application after shell/controller teardown.'
}
if ($winUiAppSource -match 'System\.Windows\.Forms' -or $winUiWindowSource -match 'System\.Windows\.Forms') {
    throw 'Shadowsocks.WinUI must stay free of System.Windows.Forms.'
}
Write-Host 'Validated phase-5/7 system shell: AppInstance, tray-first lazy window activation, Windows.WinUI-owned WinUIEx tray/MenuFlyout, hotkeys, and graceful shutdown.'


$phase7RequiredSources = @(
    'Shadowsocks.WinUI\Pages\OverviewPage.cs',
    'Shadowsocks.WinUI\Pages\ForwardProxyPage.cs',
    'Shadowsocks.WinUI\Pages\HotkeysPage.cs',
    'Shadowsocks.WinUI\Pages\SharingPage.cs',
    'Shadowsocks.WinUI\Pages\ServersPage.cs',
    'Shadowsocks.WinUI\Pages\TrafficPage.cs',
    'Shadowsocks.WinUI\Pages\GamesPage.cs',
    'Shadowsocks.WinUI\Pages\PacGeositePage.cs',
    'Shadowsocks.WinUI\Pages\SettingsPage.cs',
    'Shadowsocks.WinUI\Pages\LogsPage.cs',
    'Shadowsocks.WinUI\Pages\AboutPage.cs',
    'Shadowsocks.WinUI\Pages\OnlineConfigPage.cs',
    'Shadowsocks.WinUI\UI\WinUIStyles.cs',
    'WINDOWS11_UI_GUIDE.md'
)
foreach ($relativePath in $phase7RequiredSources) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $relativePath) -PathType Leaf)) {
        throw "Phase-7 WinUI feature source is missing: $relativePath"
    }
}

$pacPageSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\Pages\PacGeositePage.cs') -Raw
foreach ($pacPageParityToken in @(
    'ApplyPacModeState(configuration.useOnlinePac)',
    'PromptForOnlinePacUrlAsync()',
    'SetOnlinePacToggleWithoutEvent(false)',
    '_savePacUrlButton.Visibility = onlineVisibility',
    '_sourceCard.Visibility = localVisibility',
    '_securePacToggle.IsEnabled = true',
    '_regeneratePacToggle.IsEnabled = true'
)) {
    if ($pacPageSource -notmatch [regex]::Escape($pacPageParityToken)) {
        throw "PAC / GeoSite page state-machine parity token is missing: $pacPageParityToken"
    }
}
$gameModeNavigationPattern = 'CreateNavigationItem\s*\(\s*_localization\["Game Mode"\]\s*,\s*GamesTag\s*,\s*Symbol\.Play(?:\s*,|\s*\))'
if ($winUiWindowSource -notmatch $gameModeNavigationPattern) {
    throw 'Phase-7 navigation must label and localize the automatic compatibility page as Game Mode, not Games.'
}
if ($winUiWindowSource -match 'CreateNavigationItem\s*\(\s*_localization\["Games"\]\s*,\s*GamesTag') {
    throw 'Phase-7 navigation must not regress the automatic compatibility page label from Game Mode back to Games.'
}

$phase7MainWindow = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\MainWindow.cs') -Raw
$phase7App = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\App.cs') -Raw
foreach ($requiredToken in @(
    'NavigationView',
    'TitleBar',
    'MicaBackdrop',
    'IsSettingsVisible = true',
    'NavigationViewPaneDisplayMode.Left',
    'AppThemePreference',
    'ContentDialog',
    'InfoBar')) {
    if ($phase7MainWindow -notmatch [regex]::Escape($requiredToken)) {
        throw "Phase-7 MainWindow is missing Windows 11 shell element: $requiredToken"
    }
}
if ($phase7App -notmatch 'InitializeComponent\(\)') {
    throw 'Phase-7 WinUI App must call generated InitializeComponent() so App.xaml loads Fluent resources before OnLaunched creates controls.'
}
if ($phase7App -match 'new XamlControlsResources\(') {
    throw 'Phase-7 WinUI App must not construct XamlControlsResources manually from C#; App.xaml owns the resource dictionary.'
}

$phase7WinUiSources = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI') -Recurse -File -Filter '*.cs')
foreach ($requiredControl in @('TeachingTip', 'NumberBox', 'ToggleSwitch')) {
    if (-not ($phase7WinUiSources | Select-String -Pattern ("\b" + $requiredControl + "\b") | Select-Object -First 1)) {
        throw "Phase-7 WinUI shell must exercise native Fluent control: $requiredControl"
    }
}

$configurationSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Core\Model\Configuration.cs') -Raw
if ($configurationSource -notmatch 'public string uiTheme;' -or $configurationSource -notmatch 'uiTheme = "System"') {
    throw 'System/Light/Dark theme preference must remain in the persistent settings model with System as the default.'
}
$windowsControllerSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Windows\Controller\ShadowsocksController.cs') -Raw
if ($windowsControllerSource -notmatch 'SetUiTheme\(' -or $phase7MainWindow -notmatch '_controller\?\.SetUiTheme\(') {
    throw 'Phase-7 theme persistence must flow through ShadowsocksController.SetUiTheme rather than direct UI file writes.'
}
$serversPageSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\Pages\ServersPage.cs') -Raw
foreach ($serverParityToken in @('Duplicate', 'Move up', 'Move down', 'Need Plugin Argument', 'Plugin Arguments', 'Discard changes', 'Apply')) {
    if ($serversPageSource -notmatch [regex]::Escape($serverParityToken)) {
        throw "Phase-7 Servers parity is missing required original Config workflow token: $serverParityToken"
    }
}
$trafficPageSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\Pages\TrafficPage.cs') -Raw
foreach ($trafficParityToken in @('TCP capture', 'UDP capture', 'CreateRoutingHeader', 'Enabled', 'Application', 'Action')) {
    if ($trafficPageSource -notmatch [regex]::Escape($trafficParityToken)) {
        throw "Phase-7 Traffic milestone is missing required capture/routing UI token: $trafficParityToken"
    }
}
$trafficModelsSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Core\Controller\Traffic\TrafficModels.cs') -Raw
if ($trafficModelsSource -notmatch 'TcpCaptureActive' -or $trafficModelsSource -notmatch 'UdpCaptureActive') {
    throw 'TrafficCaptureStatus must expose explicit TCP/UDP capture state rather than inferring it only from labels or ports.'
}

$logsPageSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\Pages\LogsPage.cs') -Raw
foreach ($logsParityToken in @(
    'BuildSparkline',
    'Traffic · last 60 seconds',
    'Top most',
    '_toolbar.Visibility = Visibility.Visible',
    'SaveLogViewerConfig',
    'MaxVisibleLogLines',
    'LogViewerHeight',
    'ListViewSelectionMode.None',
    'SystemFillColorCautionBrush',
    'SystemFillColorCriticalBrush',
    'TryGetLogLineSeverity',
    'TryParseLogLine',
    'CompactTimestamp',
    'ColumnSpacing = 12',
    'CornerRadius = new CornerRadius(9)'
)) {
    if ($logsPageSource -notmatch [regex]::Escape($logsParityToken)) {
        throw "Phase-7 Logs parity is missing required legacy workflow token: $logsParityToken"
    }
}
if ($logsPageSource -match [regex]::Escape('Content = "Show toolbar"') -or
    $logsPageSource -match [regex]::Escape('Content = "Font…"')) {
    throw 'Logs toolbar must stay always visible and the retired Show toolbar / Font controls must not return.'
}
foreach ($logsForbiddenNamespace in @('using Microsoft.UI.Text;', 'using Microsoft.UI.Xaml.Shapes;', 'global::Windows.Foundation', 'global::Windows.UI.Text')) {
    if ($logsPageSource -match [regex]::Escape($logsForbiddenNamespace)) {
        throw "LogsPage must keep the proven base WinUI compile surface; forbidden namespace/type dependency: $logsForbiddenNamespace"
    }
}
if ($phase7MainWindow -notmatch 'IsAlwaysOnTop') {
    throw 'Phase-7 Logs Top Most must map to the WinUI AppWindow OverlappedPresenter.'
}

$loggingConfiguratorSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Core\Logging\LoggingConfigurator.cs') -Raw
foreach ($loggingRetentionToken in @('ArchiveAboveSize', 'ArchiveEvery', 'ArchiveSuffixFormat', 'MaxArchiveFiles', 'MaxArchiveDays')) {
    if ($loggingConfiguratorSource -notmatch [regex]::Escape($loggingRetentionToken)) {
        throw "Automatic log retention is missing NLog token: $loggingRetentionToken"
    }
}

foreach ($serverValidationToken in @('Configuration.CheckServer(current)', 'RestoreServerSelection', 'ConfirmDiscardUnconfiguredServerAsync')) {
    if ($serversPageSource -notmatch [regex]::Escape($serverValidationToken)) {
        throw "Phase-7 Servers must preserve validation-before-selection semantics: $serverValidationToken"
    }
}

$aboutPageSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\Pages\AboutPage.cs') -Raw
foreach ($updateParityToken in @('Release notes', 'Download update', 'Skip this version', 'Not now')) {
    if ($aboutPageSource -notmatch [regex]::Escape($updateParityToken)) {
        throw "Phase-7 About/Update parity is missing required original update workflow token: $updateParityToken"
    }
}

Write-Host 'Validated phase-7 WinUI feature shell: Fluent navigation plus About, Hotkeys, Forward Proxy, GeoSite, Game Mode, Traffic, Logs, Servers, and Sharing editors.'


# Phase 8: WinUI localization abstraction keeps the existing CSV backend while
# decoupling the new presentation shell from the legacy static I18N API.
$localizationInterfacePath = Join-Path $repoRoot 'Shadowsocks.Core\Localization\ILocalizationService.cs'
$csvLocalizationPath = Join-Path $repoRoot 'Shadowsocks.Core\Localization\CsvLocalizationService.cs'
$winUiLocalizationPath = Join-Path $repoRoot 'Shadowsocks.WinUI\UI\WinUILocalization.cs'
foreach ($requiredLocalizationSource in @($localizationInterfacePath, $csvLocalizationPath, $winUiLocalizationPath)) {
    if (-not (Test-Path -LiteralPath $requiredLocalizationSource -PathType Leaf)) {
        throw "Phase-8 localization source is missing: $([System.IO.Path]::GetRelativePath($repoRoot, $requiredLocalizationSource))"
    }
}

$localizationInterfaceSource = Get-Content -LiteralPath $localizationInterfacePath -Raw
$csvLocalizationSource = Get-Content -LiteralPath $csvLocalizationPath -Raw
$i18nFacadeSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Core\Controller\I18N.cs') -Raw
$winUiLocalizationSource = Get-Content -LiteralPath $winUiLocalizationPath -Raw
$winUiPageContextSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\UI\WinUIPageContext.cs') -Raw

if ($localizationInterfaceSource -notmatch 'interface\s+ILocalizationService' -or
    $localizationInterfaceSource -notmatch 'string\s+this\[string\s+key\]' -or
    $localizationInterfaceSource -notmatch 'Format\(string\s+key') {
    throw 'ILocalizationService must expose the string indexer plus formatted lookup used by the WinUI shell.'
}
if ($csvLocalizationSource -notmatch 'class\s+CsvLocalizationService' -or
    $csvLocalizationSource -notmatch 'EmbeddedResources\.I18nCsv' -or
    $csvLocalizationSource -notmatch 'CultureInfo\.CurrentCulture' -or
    $csvLocalizationSource -notmatch 'locale\.Split\(') {
    throw 'CsvLocalizationService must keep the embedded i18n.csv backend, CurrentCulture selection, and same-language regional fallback.'
}
if ($i18nFacadeSource -notmatch 'ILocalizationService\s+Service' -or
    $i18nFacadeSource -notmatch 'Configure\(ILocalizationService\s+service\)' -or
    $i18nFacadeSource -notmatch 'Service\.Format\(key, args\)') {
    throw 'Legacy I18N must remain a compatibility facade over ILocalizationService.'
}
if ($winUiProgramSource -notmatch 'CsvLocalizationService\.CreateDefault\(\)' -or
    $winUiProgramSource -notmatch 'I18N\.Configure\(_localization\)' -or
    $winUiProgramSource -notmatch 'ConfigureStartupContext\(keyInstance, activationArguments, _localization\)') {
    throw 'Program must create one CSV localization service before WinUI bootstrap and pass the same instance into App/legacy I18N.'
}
if ($winUiAppSource -notmatch 'ILocalizationService\s+_localization' -or
    $winUiWindowSource -notmatch 'ILocalizationService\s+localization' -or
    $winUiPageContextSource -notmatch 'ILocalizationService\s+Localization') {
    throw 'Phase-8 localization service must flow Program -> App -> MainWindow -> WinUIPageContext.'
}
if ($winUiLocalizationSource -notmatch 'class\s+WinUILocalization' -or
    $winUiLocalizationSource -notmatch 'TextBlock' -or
    $winUiLocalizationSource -notmatch 'ToggleSwitch' -or
    $winUiLocalizationSource -notmatch 'InfoBar' -or
    $winUiLocalizationSource -notmatch 'TeachingTip' -or
    $winUiLocalizationSource -notmatch 'ContentControl') {
    throw 'WinUILocalization must translate the code-only WinUI visual tree through ILocalizationService.'
}

$directWinUiI18n = $phase7WinUiSources | Select-String -Pattern '\bI18N\.GetString\(' | Select-Object -First 1
if ($null -ne $directWinUiI18n) {
    $relative = [System.IO.Path]::GetRelativePath($repoRoot, $directWinUiI18n.Path)
    throw "Shadowsocks.WinUI must not call legacy I18N.GetString directly after Phase 8: ${relative}:$($directWinUiI18n.LineNumber)"
}

$phase8Pages = @(Get-ChildItem -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\Pages') -File -Filter '*.cs')
foreach ($page in $phase8Pages) {
    $pageText = Get-Content -LiteralPath $page.FullName -Raw
    if ($pageText -notmatch 'WinUILocalization\.Apply\(Content, _context\.Localization\)') {
        throw "Every code-only WinUI feature page must localize its static visual tree: $($page.Name)"
    }
}

$settingsSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\Pages\SettingsPage.cs') -Raw
if ($settingsSource -notmatch 'ComboBoxItem\s*\{\s*Content\s*=\s*"System",\s*Tag\s*=\s*AppThemePreference\.System' -or
    $settingsSource -notmatch 'Tag:\s*AppThemePreference\s+theme') {
    throw 'Localized theme display text must not become the persisted AppThemePreference value; ComboBoxItem.Tag must carry the enum.'
}
if ($trafficPageSource -notmatch 'Tag\s*=\s*TrafficRouteAction\.Proxy' -or
    $trafficPageSource -notmatch 'Tag:\s*TrafficRouteAction\s+selectedAction') {
    throw 'Localized Proxy/Direct/Block labels must use TrafficRouteAction values stored in ComboBoxItem.Tag.'
}

$i18nCsvPath = Join-Path $repoRoot 'Shadowsocks.Core\Data\i18n.csv'
if (-not (Test-Path -LiteralPath $i18nCsvPath -PathType Leaf)) {
    throw 'Phase-8 CSV localization backend is missing: Shadowsocks.Core\Data\i18n.csv'
}
$i18nRows = @(Import-Csv -LiteralPath $i18nCsvPath)
$i18nHeaders = @((Get-Content -LiteralPath $i18nCsvPath -TotalCount 1) -split ',')
$expectedI18nHeaders = @('en', 'ru-RU', 'zh-CN', 'zh-TW', 'ja', 'ko', 'fr')
if ($i18nHeaders.Count -ne $expectedI18nHeaders.Count -or (Compare-Object $i18nHeaders $expectedI18nHeaders -SyncWindow 0)) {
    throw 'i18n.csv must keep the existing seven-column language schema: en,ru-RU,zh-CN,zh-TW,ja,ko,fr.'
}
$i18nKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
foreach ($row in $i18nRows) {
    $key = [string]$row.en
    if ([string]::IsNullOrWhiteSpace($key) -or $key.TrimStart().StartsWith('#')) {
        continue
    }
    $normalizedKey = $key.Trim()
    if (-not $i18nKeys.Add($normalizedKey)) {
        throw "Duplicate normalized localization key in i18n.csv: $normalizedKey"
    }
    foreach ($language in @('ru-RU', 'zh-CN', 'zh-TW', 'ja', 'ko', 'fr')) {
        if ([string]::IsNullOrWhiteSpace([string]$row.$language)) {
            throw "Localization translation is missing for '$normalizedKey' in language '$language'."
        }
    }
}
foreach ($requiredWinUiKey in @(
    'Settings',
    'Game Mode',
    'Traffic Routing',
    'Forward Proxy',
    'PAC / GeoSite',
    'Share / QR',
    'Logs',
    'About & updates',
    'System shell',
    'Controller unavailable',
    'Shortcut format',
    'Game Mode is automatic',
    'Background mode')) {
    if (-not $i18nKeys.Contains($requiredWinUiKey)) {
        throw "Required Phase-8 WinUI localization key is missing from i18n.csv: $requiredWinUiKey"
    }
}

$localizationTestsPath = Join-Path $repoRoot 'Shadowsocks.UnitTests\LocalizationServiceTests.cs'
if (-not (Test-Path -LiteralPath $localizationTestsPath -PathType Leaf)) {
    throw 'Phase-8 localization requires unit tests for locale selection and fallback semantics.'
}
$localizationTests = Get-Content -LiteralPath $localizationTestsPath -Raw
foreach ($testToken in @('ru-RU', 'ru-UA', 'MissingTranslationFallsBackToEnglishKey', 'EmbeddedCatalogHasCompleteTranslations')) {
    if ($localizationTests -notmatch [regex]::Escape($testToken)) {
        throw "LocalizationServiceTests is missing required fallback coverage: $testToken"
    }
}

Write-Host "Validated localization: ILocalizationService -> CsvLocalizationService -> embedded i18n.csv, $($i18nKeys.Count) normalized keys, all six translated locales complete, WinUI free of direct I18N.GetString calls."

Write-Host 'Validated project layout: Core is platform-neutral; Windows owns OS integration; Windows.WinUI isolates WinUIEx tray support; WinUI is the only production presentation shell.'

$directoryBuildPropsPath = Join-Path $repoRoot 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $directoryBuildPropsPath -PathType Leaf)) {
    throw 'Directory.Build.props is required to pin the .NET 10 analyzer baseline.'
}
[xml]$directoryBuildProps = Get-Content -LiteralPath $directoryBuildPropsPath -Raw
$analysisLevel = Get-ProjectPropertyValue -Project $directoryBuildProps -Name 'AnalysisLevel'
if ($analysisLevel -ne '10.0') {
    throw "Directory.Build.props must pin AnalysisLevel to 10.0; found '$analysisLevel'."
}

$unitTestSources = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'Shadowsocks.UnitTests') -Recurse -File -Filter '*.cs'
$deprecatedDataTestMethod = $unitTestSources | Select-String -Pattern '\[DataTestMethod(?:Attribute)?\b' | Select-Object -First 1
if ($null -ne $deprecatedDataTestMethod) {
    $relative = [System.IO.Path]::GetRelativePath($repoRoot, $deprecatedDataTestMethod.Path)
    throw "Deprecated MSTest DataTestMethod usage detected: ${relative}:$($deprecatedDataTestMethod.LineNumber). Use TestMethod with DataRow."
}

$rasPath = Join-Path $repoRoot 'Shadowsocks.Windows\Util\SystemProxy\Ras.cs'
$rasSource = Get-Content -LiteralPath $rasPath -Raw
if ($rasSource -match '\[DllImport\(' -or $rasSource -notmatch '\[LibraryImport\(') {
    throw 'Ras interop must use source-generated LibraryImport instead of DllImport.'
}
$allowUnsafeBlocks = Get-ProjectPropertyValue -Project $windowsProject -Name 'AllowUnsafeBlocks'
if ($allowUnsafeBlocks -ne 'true') {
    throw 'Shadowsocks.Windows must enable AllowUnsafeBlocks for source-generated RAS interop.'
}

Write-Host 'Validated .NET 10 style baseline: analyzer level pinned, deprecated MSTest attribute removed, Ras uses LibraryImport.'


$nativeInteractionPath = Join-Path $repoRoot 'Shadowsocks.Windows\Shell\NativeUserInteractionService.cs'
$powerMonitorPath = Join-Path $repoRoot 'Shadowsocks.Windows.WinUI\Shell\PowerModeMonitor.cs'
if (-not (Test-Path -LiteralPath $nativeInteractionPath -PathType Leaf)) {
    throw 'Phase 9 requires NativeUserInteractionService so controller-level synchronous UX survives without WinForms/WPF.'
}
if (-not (Test-Path -LiteralPath $powerMonitorPath -PathType Leaf)) {
    throw 'Phase 9 requires PowerModeMonitor so suspend/resume behavior survives UI retirement.'
}
$nativeInteractionSource = Get-Content -LiteralPath $nativeInteractionPath -Raw
$powerMonitorSource = Get-Content -LiteralPath $powerMonitorPath -Raw
foreach ($token in @('IUserInteractionService', 'MessageBoxW', 'SetClipboardData')) {
    if ($nativeInteractionSource -notmatch [regex]::Escape($token)) {
        throw "NativeUserInteractionService is missing required Phase-9 behavior: $token"
    }
}
foreach ($token in @('WmPowerBroadcast', 'PbtApmSuspend', 'PbtApmResumeSuspend', 'PbtApmResumeAutomatic')) {
    if ($powerMonitorSource -notmatch [regex]::Escape($token)) {
        throw "PowerModeMonitor is missing required Phase-9 behavior: $token"
    }
}
foreach ($token in @(
    'new NativeUserInteractionService()',
    'new ShadowsocksController(_userInteraction)',
    'InitializePowerModeMonitor()',
    'controller.Stop()',
    'controller.Start(systemWakeUp: true)',
    'ScheduleStartupOnlineConfigRefresh()',
    'UpdateAllOnlineConfig()')) {
    if ($winUiAppSource -notmatch [regex]::Escape($token)) {
        throw "WinUI App is missing required Phase-9 parity behavior: $token"
    }
}
if ($winUiAppSource -notmatch 'Task\.Delay\(TimeSpan\.FromSeconds\(10\)') {
    throw 'Phase 9 must preserve the approximately 10-second resume/startup compatibility delay.'
}
Write-Host 'Validated Phase 9 non-page parity: native user interaction, suspend/resume and startup Online Config refresh.'

$controllerEventSubscriptionIndex = $winUiAppSource.IndexOf('SubscribeControllerEvents(_controller)', [System.StringComparison]::Ordinal)
$controllerStartIndex = $winUiAppSource.IndexOf('_controller.Start()', [System.StringComparison]::Ordinal)
if ($controllerEventSubscriptionIndex -lt 0 -or $controllerStartIndex -lt 0 -or $controllerEventSubscriptionIndex -gt $controllerStartIndex) {
    throw 'WinUI must subscribe to ShadowsocksController.Errored before controller.Start() so startup listener failures are not lost.'
}
if ($winUiAppSource -notmatch 'OnMainWindowContentLoaded' -or $winUiAppSource -notmatch 'DrainControllerErrorQueue') {
    throw 'WinUI startup listener errors must be drained after the MainWindow XamlRoot is loaded.'
}
if ($windowsControllerSource -notmatch 'SocketError\.AddressAlreadyInUse' -or $windowsControllerSource -notmatch 'Port \{0\} already in use') {
    throw 'The original human-readable occupied-port error translation must remain in ShadowsocksController.Reload().'
}
if ($windowsControllerSource -notmatch 'GetTrafficCaptureStatus\(' -or $windowsControllerSource -notmatch 'SaveTrafficRoutingAsync\(') {
    throw 'Phase-7 Traffic/Game Mode pages require controller-owned runtime status and routing-save APIs.'
}
if ($phase7MainWindow -notmatch 'ForwardProxyPage' -or $phase7MainWindow -notmatch 'HotkeysPage' -or $phase7MainWindow -notmatch 'SharingPage') {
    throw 'Phase-7 NavigationView must expose Forward Proxy, Hotkeys, and Sharing pages.'
}
Write-Host 'Validated occupied-port startup reporting and phase-7 controller/UI feature wiring.'
foreach ($appTrayParityToken in @(
    'BuildTrayMenuState',
    'StartAutomaticUpdateCheck',
    'TrafficChanged += OnControllerTrafficChanged')) {
    if ($phase7App -notmatch [regex]::Escape($appTrayParityToken)) {
        throw "WinUI application is missing original tray/lifecycle behavior: $appTrayParityToken"
    }
}
if ($phase7App -notmatch 'firstRun' -or $phase7App -notmatch 'NavigateToServers') {
    throw 'First-run WinUI behavior must open Servers, matching the verified baseline startup workflow.'
}
Write-Host 'Validated tray command hierarchy, traffic activity icon updates, first-run Servers, and startup update check.'

$winUiProjectPath = Join-Path $repoRoot 'Shadowsocks.WinUI\Shadowsocks.WinUI.csproj'
$winUiProjectText = [System.IO.File]::ReadAllText($winUiProjectPath)
if ($winUiProjectText -match '<UseUwp(?:Tools)?>' -or
    $winUiProjectText -match 'EnableUnsafeMixedMicrosoftWindowsUIXamlProjections' -or
    $winUiProjectText -match 'CsWinRTUseWindowsUIXamlProjections') {
    throw 'WinUI frontend must use a pure Microsoft.UI.Xaml projection graph; UWP/mixed Windows.UI.Xaml projections are forbidden.'
}
$hotkeysPagePath = Join-Path $repoRoot 'Shadowsocks.WinUI\Pages\HotkeysPage.cs'
$hotkeysPageText = [System.IO.File]::ReadAllText($hotkeysPagePath)
if ($hotkeysPageText -match 'Windows\.UI\.Core|CoreVirtualKeyStates|InputKeyboardSource\.GetKeyStateForCurrentThread') {
    throw 'Hotkeys page must not pull legacy Windows.UI.Core projections into the WinUI 3 frontend.'
}
if ($hotkeysPageText -notmatch 'LibraryImport\("user32\.dll"\)' -or $hotkeysPageText -notmatch 'GetKeyState\(\(int\)key\) < 0') {
    throw 'Hotkeys page must query modifier state through Win32 GetKeyState.'
}
$settingsPagePath = Join-Path $repoRoot 'Shadowsocks.WinUI\Pages\SettingsPage.cs'
$settingsPageText = [System.IO.File]::ReadAllText($settingsPagePath)
foreach ($retiredSettingsToken in @('Check for Updates at Startup', 'Verbose Logging', 'Show Plugin Output')) {
    if ($settingsPageText -match [regex]::Escape($retiredSettingsToken)) {
        throw "Settings page must not contain feature-specific preference that belongs to Logs/About: $retiredSettingsToken"
    }
}
foreach ($logsPreferenceToken in @('Verbose Logging', 'Show Plugin Output')) {
    if ($logsPageSource -notmatch [regex]::Escape($logsPreferenceToken)) {
        throw "Logs page is missing required logging preference: $logsPreferenceToken"
    }
}
if ($aboutPageSource -notmatch [regex]::Escape('Check for Updates at Startup')) {
    throw 'About page must expose Check for Updates at Startup next to the other update controls.'
}
if ($traySource -match 'Check for Updates at Startup|Verbose Logging|Show Plugin Output|Write translation template') {
    throw 'Main-window-only preferences or translation-template command must not appear in the tray.'
}

# Pass-3 regressions: imported servers must notify the WinUI shell, tray navigation must
# force an already-visible window to the foreground, and logging subscriptions must be symmetric.
$controllerSource = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'Shadowsocks.Windows\Controller\ShadowsocksController.cs'))
$importMethodPattern = '(?s)public bool AddServerBySSURL\(string ssURL\).{0,3000}?SaveConfig\(_config\).{0,1000}?ConfigChanged\?\.Invoke'
if ($controllerSource -notmatch $importMethodPattern) {
    throw 'ss:// imports must persist configuration and raise ConfigChanged so cached WinUI server lists refresh.'
}
$serversPageText = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'Shadowsocks.WinUI\Pages\ServersPage.cs'))
if ($serversPageText -notmatch 'MergeExternallyAddedServers' -or $serversPageText -notmatch '_loadedServerIdentities') {
    throw 'Servers page must merge externally imported servers even when its cached editor has unsaved local edits.'
}
if ($serversPageText -notmatch 'Server Name' -or $serversPageText -notmatch 'Share Server Config') {
    throw 'Servers page must expose a server-name field and an explicit share action.'
}
$configurationText = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'Shadowsocks.Core\Model\Configuration.cs'))
if ($configurationText -notmatch 'EnsureServerNames' -or $configurationText -notmatch 'candidate = \$"Server \{sequence\+\+\}"') {
    throw 'Configured servers without an explicit name must receive a deterministic Server N display name.'
}
$stylesText = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'Shadowsocks.WinUI\UI\WinUIStyles.cs'))
$mainWindowLayoutText = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'Shadowsocks.WinUI\MainWindow.cs'))
if ($stylesText -notmatch 'PageGutter = 16' -or $mainWindowLayoutText -match 'DisplayMode == NavigationViewDisplayMode.Minimal \? 12 : 24') {
    throw 'WinUI pages must use one fixed content gutter across NavigationView display modes.'
}
$logsLayoutText = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'Shadowsocks.WinUI\Pages\LogsPage.cs'))
if ($logsLayoutText -notmatch 'loggingOptionsRow' -or $logsLayoutText -notmatch 'Grid.SetColumn\(_pluginOutputToggle, 1\)') {
    throw 'Verbose Logging and Show Plugin Output must share one row in the Logs page.'
}
$sharingPageText = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'Shadowsocks.WinUI\Pages\SharingPage.cs'))
foreach ($sharingToken in @('Scan QRCode from Screen', 'Open image', 'Scan QR from clipboard image', 'Paste URL from clipboard', 'OnDeleteServerClicked')) {
    if ($sharingPageText -notmatch [regex]::Escape($sharingToken)) {
        throw "Share / QR page is missing required main-window action: $sharingToken"
    }
}
$pacPageText = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'Shadowsocks.WinUI\Pages\PacGeositePage.cs'))
if ($pacPageText -notmatch 'Copy Local PAC URL') {
    throw 'PAC page must expose Copy Local PAC URL in the main window.'
}
$mainWindowRegressionText = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'Shadowsocks.WinUI\MainWindow.cs'))
if ($mainWindowRegressionText -notmatch 'OnlineConfigTag' -or $mainWindowRegressionText -notmatch '_localization\["Online Config"\]') {
    throw 'Online Config must be a first-class main-window navigation item.'
}
foreach ($foregroundToken in @('presenter.Restore(true)', 'AppWindow.Show(true)', 'Activate()', 'SetForegroundWindow(windowHandle)')) {
    if ($mainWindowRegressionText -notmatch [regex]::Escape($foregroundToken)) {
        throw "Tray foreground activation is missing token: $foregroundToken"
    }
}
if ($mainWindowRegressionText -notmatch 'ShowPluginOutputChanged \+= OnControllerStateChanged' -or
    $mainWindowRegressionText -notmatch 'ShowPluginOutputChanged -= OnControllerStateChanged') {
    throw 'MainWindow must subscribe/unsubscribe ShowPluginOutputChanged so the Logs page stays synchronized.'
}
if ([regex]::Matches($logsPageSource, 'UnsubscribeTraffic\(\);').Count -ne 1) {
    throw 'LogsPage must unsubscribe from traffic exactly once on unload.'
}

# Every tray command must exist in the catalog that was already validated above.
# Do not re-import i18n.csv here: the Phase-8 localization block has already
# guaranteed that every normalized key is unique and that all six translated
# locale columns (including ru-RU) are non-empty. Re-parsing the same CSV here
# is redundant and has historically made this StrictMode validator brittle.
$requiredTranslatedTrayKeys = @(
    'System Proxy', 'Disable', 'PAC', 'Global', 'Traffic Mode', 'User Mode', 'Admin Mode',
    'Traffic Routing', 'Servers', 'Share Server Config',
    'Local PAC', 'Online PAC', 'Edit Local PAC File',
    'Update Local PAC from Geosite', 'GeoSite Sources', 'Edit User Rule for Geosite', 'Require secret for local PAC URL',
    'Regenerate Local PAC after application updates', 'Edit Online PAC URL',
    'Forward Proxy', 'Online Config', 'Start on Boot', 'Associate ss:// Links',
    'Allow other devices to connect', 'Hotkeys', 'Help', 'Logs', 'Updates',
    'Check for Updates', 'Include prerelease versions', 'About', 'Quit', 'More than 20 servers (total: {0})'
)
foreach ($trayKey in $requiredTranslatedTrayKeys) {
    if (-not $i18nKeys.Contains($trayKey)) {
        throw "Tray localization key is missing from the fully translated embedded catalog: $trayKey"
    }
}
$localizationSource = [System.IO.File]::ReadAllText((Join-Path $repoRoot 'Shadowsocks.Core\Localization\CsvLocalizationService.cs'))
if ($localizationSource -notmatch 'EmbeddedResources\.I18nCsv') {
    throw 'Localization must load the embedded i18n.csv catalog.'
}
if ($localizationSource -match 'ApplyOverrides' -or
    $localizationSource -match 'LocalizationOverrideFile' -or
    $localizationSource -match 'File\.ReadAllText') {
    throw 'Localization must have exactly one source of truth: the embedded i18n.csv catalog.'
}


$mainWindowPath = Join-Path $repoRoot 'Shadowsocks.WinUI\MainWindow.cs'
if (Test-Path -LiteralPath $mainWindowPath) {
    $mainWindowText = [System.IO.File]::ReadAllText($mainWindowPath)
    if ($mainWindowText.Contains('.Navigate(pageType') -or $mainWindowText.Contains('Frame.Navigate(')) {
        throw 'Phase 7 code-only WinUI pages must not use Frame.Navigate(Type,...); construct pages explicitly instead.'
    }
}

# Product packaging must target the WinUI frontend and must always use its single-file profile.
$buildReleasePath = Join-Path $repoRoot 'packaging\Build-Release.ps1'
$buildReleaseText = [System.IO.File]::ReadAllText($buildReleasePath)
if ($buildReleaseText -notmatch "Shadowsocks\.WinUI\\Shadowsocks\.WinUI\.csproj") {
    throw 'Build-Release.ps1 must publish Shadowsocks.WinUI.'
}
if ($buildReleaseText -notmatch 'PublishProfile=FolderProfile') {
    throw 'Build-Release.ps1 must use the WinUI FolderProfile so product publish remains single-file.'
}
$ciPath = Join-Path $repoRoot '.github\workflows\ci.yml'
$ciText = [System.IO.File]::ReadAllText($ciPath)
if ($ciText -notmatch 'Shadowsocks\.WinUI\\Shadowsocks\.WinUI\.csproj.*PublishProfile=FolderProfile') {
    throw 'CI WinUI product publish must use FolderProfile.'
}
if ($buildReleaseText -match 'Shadowsocks\.UI' -or $ciText -match 'Shadowsocks\.UI') {
    throw 'Release/CI must not reference the retired Shadowsocks.UI project.'
}

[xml]$winUiPublishProfile = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\Properties\PublishProfiles\FolderProfile.pubxml') -Raw
foreach ($requiredPublishProperty in @('WindowsAppSDKSelfContained', 'SelfContained', 'EnableMsixTooling', 'IncludeAllContentForSelfExtract', 'PublishSingleFile')) {
    if ((Get-ProjectPropertyValue -Project $winUiPublishProfile -Name $requiredPublishProperty) -ne 'true') {
        throw "WinUI FolderProfile must keep $requiredPublishProperty=true for portable single-file publishing."
    }
}
$icoItems = @(Get-ProjectItems -Project $winUiProject -Name 'None')
if (@($icoItems | Where-Object {
        (Get-XmlAttributeText -Node $_ -Name 'Update') -eq 'shadowsocks.ico' -and
        (Get-XmlMetadataText -Node $_ -Name 'CopyToPublishDirectory') -ne 'Never'
    }).Count -ne 0) {
    throw 'shadowsocks.ico is compiled into the application and must not be emitted as a publish sidecar.'
}


# Phase 10: file-backed LocalAppData/Clean-Mode storage, on-demand helper extraction, and one-file release.
$phase10RequiredFiles = @(
    'Shadowsocks.Core\Storage\ISettingsStore.cs',
    'Shadowsocks.Core\Storage\AppStoragePaths.cs',
    'Shadowsocks.Core\Storage\JsonFileSettingsStore.cs',
    'Shadowsocks.Windows\Storage\WindowsStorageBootstrapper.cs',
    'Shadowsocks.Windows\Controller\Traffic\NetworkServiceRuntime.cs'
)
foreach ($relativePath in $phase10RequiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $relativePath) -PathType Leaf)) {
        throw "Phase 10 storage/deployment file is missing: $relativePath"
    }
}
if (Test-Path -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Windows\Storage\RegistrySettingsStore.cs')) {
    throw 'Application configuration must not use RegistrySettingsStore; settings belong under LocalAppData or the Clean Mode Temp root.'
}

$settingsStoreSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Core\Storage\ISettingsStore.cs') -Raw
if ($settingsStoreSource -notmatch 'interface\s+ISettingsStore' -or
    $settingsStoreSource -notmatch 'TryGetString' -or
    $settingsStoreSource -notmatch 'SetString') {
    throw 'Phase 10 requires a UI-independent ISettingsStore abstraction.'
}

$jsonStoreSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Core\Storage\JsonFileSettingsStore.cs') -Raw
foreach ($fileStoreToken in @('JObject', 'File.Replace', 'backupPath', 'SaveDocument', 'LoadDocument')) {
    if ($jsonStoreSource -notmatch [regex]::Escape($fileStoreToken)) {
        throw "JSON settings store is missing durability token: $fileStoreToken"
    }
}
if ($jsonStoreSource -match 'Microsoft\.Win32' -or $jsonStoreSource -match 'Registry\.') {
    throw 'JSON settings store must not depend on the Windows Registry.'
}

$storagePathsSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Core\Storage\AppStoragePaths.cs') -Raw
foreach ($storageToken in @(
    'Environment.SpecialFolder.LocalApplicationData',
    'Path.GetTempPath()',
    'StorageRoot',
    'SettingsFile',
    'SettingsBackupFile',
    'CleanSessionsRoot',
    'IsCleanModeExecutableName',
    'EndsWith("p"',
    'CleanupCleanSession',
    'Cache',
    'GeoSite',
    'Logs',
    'NetworkService',
    'StartupExecutableFile')) {
    if ($storagePathsSource -notmatch [regex]::Escape($storageToken)) {
        throw "Phase 10 AppStoragePaths is missing required storage token: $storageToken"
    }
}
if ($storagePathsSource -notmatch 'TempRoot\s*=>\s*Path\.Combine\(StorageRoot,\s*"Temp"\)') {
    throw 'All normal-mode application-owned scratch/helper/update data must remain below LocalAppData via StorageRoot\Temp.'
}

$storageBootstrapSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Windows\Storage\WindowsStorageBootstrapper.cs') -Raw
foreach ($storageBootstrapToken in @(
    'JsonFileSettingsStore',
    'AppStoragePaths.SettingsFile',
    'AppStoragePaths.SettingsBackupFile',
    'MigrateLegacySidecarData',
    'MigrateLegacyConfiguration',
    'RemoveObsoleteLocalizationOverride',
    '!AppStoragePaths.IsCleanMode',
    'CleanupStaleTempData',
    'CleanupStaleRuntime')) {
    if ($storageBootstrapSource -notmatch [regex]::Escape($storageBootstrapToken)) {
        throw "Phase 10 storage bootstrap is missing token: $storageBootstrapToken"
    }
}
if ($storageBootstrapSource -match 'RegistrySettingsStore' -or
    $storageBootstrapSource -match 'Software\\Shadowsocks Reborn\\Settings') {
    throw 'Storage bootstrap must ignore old application configuration Registry values.'
}
if ($storageBootstrapSource -match '\["i18n\.csv"\]') {
    throw 'External i18n.csv must not be migrated; localization is embedded only.'
}

if ($configurationSource -notmatch 'ConfigureSettingsStore\(' -or
    $configurationSource -notmatch 'SettingsValueName\s*=\s*"Configuration"' -or
    $configurationSource -notmatch 'SettingsBackupValueName\s*=\s*"ConfigurationBackup"') {
    throw 'Configuration must persist through the JSON ISettingsStore with primary and rollback snapshots.'
}
if ($configurationSource -match 'File\.WriteAllText\([^\r\n]*gui-config\.json' -or
    $configurationSource -match 'File\.Open[^\r\n]*gui-config\.json') {
    throw 'Product Configuration must never write gui-config.json beside the executable.'
}

$winUiProgramStorageSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\Program.cs') -Raw
if ($winUiProgramStorageSource -notmatch 'AppRuntimeEnvironment\.Initialize' -or
    $winUiProgramStorageSource -notmatch 'AppStoragePaths\.Initialize\(executablePath\)' -or
    $winUiProgramStorageSource.IndexOf('InitializeProcessEnvironment();', [System.StringComparison]::Ordinal) -lt 0 -or
    $winUiProgramStorageSource.IndexOf('InitializeProcessEnvironment();', [System.StringComparison]::Ordinal) -gt
        $winUiProgramStorageSource.IndexOf('CsvLocalizationService.CreateDefault()', [System.StringComparison]::Ordinal)) {
    throw 'WinUI Main must select normal/Clean storage before localization and application initialization.'
}
if ($phase7App -notmatch 'WindowsStorageBootstrapper\.Initialize\(\)' -or
    $phase7App -notmatch 'Directory\.SetCurrentDirectory\(AppStoragePaths\.RuntimeRoot\)' -or
    $phase7App -notmatch 'AppStoragePaths\.CleanupCleanSession\(\)') {
    throw 'WinUI startup/shutdown must initialize file storage, move current directory away from the EXE, and clean Clean Mode sessions.'
}
$pacDaemonLifecycleSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Core\Controller\Service\PACDaemon.cs') -Raw
if ($pacDaemonLifecycleSource -notmatch 'class\s+PACDaemon\s*:\s*IDisposable' -or
    $pacDaemonLifecycleSource -notmatch 'PACFileWatcher\?\.Dispose\(\)' -or
    $pacDaemonLifecycleSource -notmatch 'UserRuleFileWatcher\?\.Dispose\(\)') {
    throw 'PACDaemon must dispose both FileSystemWatcher instances so Clean Mode can remove its Temp session on Quit.'
}
if ($controllerSource -notmatch '_pacDaemon\.Dispose\(\)' -or $controllerSource -notmatch '_pacDaemon\s*=\s*null') {
    throw 'ShadowsocksController.Stop must release PACDaemon resources before Clean Mode storage cleanup.'
}

$pacDaemonSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Core\Controller\Service\PACDaemon.cs') -Raw
$onlinePacSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Core\Controller\Service\OnlinePacCache.cs') -Raw
$geositeSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Core\Controller\Service\GeositeUpdater.cs') -Raw
if ($geositeSource -notmatch 'TryMigrateLegacyCache' -or $geositeSource -notmatch 'AppStoragePaths\.IsCleanMode') {
    throw 'GeoSite legacy executable-side cache migration must be disabled in Clean Mode.'
}
$loggingSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Core\Logging\LoggingConfigurator.cs') -Raw
$tempUtilSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Core\Util\Util.cs') -Raw
$winDivertInstallerSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Windows\Controller\Traffic\WinDivertInstaller.cs') -Raw
$updateCheckerSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Windows\Controller\Service\UpdateChecker.cs') -Raw
$sip003Source = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Windows\Controller\Service\Sip003Plugin.cs') -Raw
foreach ($storageCheck in @(
    @{ Name = 'PAC'; Source = $pacDaemonSource; Token = 'AppStoragePaths.PacDataDirectory' },
    @{ Name = 'Online PAC'; Source = $onlinePacSource; Token = 'AppStoragePaths.OnlinePacCacheFile' },
    @{ Name = 'GeoSite'; Source = $geositeSource; Token = 'AppStoragePaths.GeositeCacheDirectory' },
    @{ Name = 'Logging'; Source = $loggingSource; Token = 'AppStoragePaths.LogFile' },
    @{ Name = 'Temp'; Source = $tempUtilSource; Token = 'AppStoragePaths.TempWorkingRoot' },
    @{ Name = 'Updates'; Source = $updateCheckerSource; Token = 'AppStoragePaths.TempUpdatesRoot' },
    @{ Name = 'SIP003'; Source = $sip003Source; Token = 'AppStoragePaths.TempWorkingRoot' },
    @{ Name = 'WinDivert'; Source = $winDivertInstallerSource; Token = 'AppStoragePaths.WinDivertRuntimeRoot' }
)) {
    if ($storageCheck.Source -notmatch [regex]::Escape($storageCheck.Token)) {
        throw "Phase 10 $($storageCheck.Name) storage must use $($storageCheck.Token)."
    }
}

if ($serversPageSource -match 'Portable Mode') {
    throw 'Portable Mode UI must stay removed; Clean Mode is selected only by the executable p-suffix.'
}
if ($sip003Source -match 'RuntimeEnvironment\.WorkingDirectory' -or
    $sip003Source -match 'AppContext\.BaseDirectory') {
    throw 'SIP003 plugin discovery must not depend on the Shadowsocks.exe directory; use absolute paths or PATH.'
}

$autoStartupSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Windows\Controller\System\AutoStartup.cs') -Raw
foreach ($startupToken in @('AppStoragePaths.StartupExecutableFile', 'SHA256.HashData', 'BuildStartupCommand', '--start-hidden', 'AppStoragePaths.IsCleanMode')) {
    if ($autoStartupSource -notmatch [regex]::Escape($startupToken)) {
        throw "Start with Windows policy is missing token: $startupToken"
    }
}
if ($autoStartupSource -match [regex]::Escape('RuntimeEnvironment.ExecutablePath}"')) {
    throw 'Start with Windows must never register the original executable/download/USB path.'
}

$settingsPageSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\Pages\SettingsPage.cs') -Raw
foreach ($settingsStorageToken in @('Content = "Open"', 'AppStoragePaths.StorageRoot', 'AppStoragePaths.IsCleanMode', '_startupToggle.IsEnabled')) {
    if ($settingsPageSource -notmatch [regex]::Escape($settingsStorageToken)) {
        throw "Settings storage/Clean Mode UI is missing token: $settingsStorageToken"
    }
}
$trayIconSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Windows.WinUI\Shell\TrayIconService.cs') -Raw
if ($trayIconSource -notmatch 'StartWithWindowsAvailable' -or $trayIconSource -notmatch 'startupItem.IsEnabled') {
    throw 'Tray Start on Boot item must be disabled in Clean Mode.'
}

if ($trafficPageSource -notmatch 'OnCaptureModeChecked' -or
    $trafficPageSource -notmatch 'SetTrafficCaptureModeAsync' -or
    $trafficPageSource -notmatch '\\uEA18') {
    throw 'WinUI Traffic must apply User/Admin mode immediately and show the shield/UAC glyph for Administrator mode.'
}

$gamesPageSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\Pages\GamesPage.cs') -Raw
$gameDiscoverySource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Windows\Controller\Traffic\Applications\GameDiscoveryService.cs') -Raw
foreach ($gameDiscoveryToken in @('Steam', 'Epic Games', 'GOG', 'Xbox')) {
    if ($gameDiscoverySource -notmatch [regex]::Escape($gameDiscoveryToken)) {
        throw "Game Mode discovery is missing launcher source: $gameDiscoveryToken"
    }
}
foreach ($gameUiToken in @('Scan installed games', 'GameDiscoveryService.Discover', 'Add application manually')) {
    if ($gamesPageSource -notmatch [regex]::Escape($gameUiToken)) {
        throw "Game Mode discovery/manual-add UI is missing token: $gameUiToken"
    }
}

$serverModelSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Core\Model\Server.cs') -Raw
if ($serverModelSource -notmatch 'importedFromUrl' -or
    $serversPageSource -notmatch 'IsPasswordRevealAllowed' -or
    $serversPageSource -notmatch '_showPassword.Visibility') {
    throw 'URL/subscription server credentials must keep password reveal hidden while manual servers retain the option.'
}

$networkServiceRuntimeSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Windows\Controller\Traffic\NetworkServiceRuntime.cs') -Raw
foreach ($helperToken in @(
    'Shadowsocks.WinUI.Embedded.Shadowsocks.NetworkService.exe',
    'AppStoragePaths.TempNetworkServiceRoot',
    'SHA256.HashData',
    'Shadowsocks.Reborn.NetworkService.Extraction',
    'FileShare.Read',
    'Guid.NewGuid()',
    'StaleRuntimeAge')) {
    if ($networkServiceRuntimeSource -notmatch [regex]::Escape($helperToken)) {
        throw "Phase 10 NetworkService runtime materialization is missing token: $helperToken"
    }
}
$adminCaptureSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Windows\Controller\Traffic\AdminCaptureManager.cs') -Raw
if ($adminCaptureSource -notmatch 'NetworkServiceRuntime\.AcquireHelper\(\)' -or
    $adminCaptureSource -notmatch '_helperLease\?\.Dispose\(\)' -or
    $adminCaptureSource -notmatch 'ValidateHelperVersion' -or
    $adminCaptureSource -notmatch 'SendCommandAsync\("ping"') {
    throw 'Admin Mode must acquire an on-demand NetworkService lease, validate its protocol version, and release/delete it with the elevated broker.'
}
if ($adminCaptureSource -match 'FindHelperPath\(' -or $adminCaptureSource -match 'EnsureHelperAvailable\(') {
    throw 'Legacy sidecar NetworkService discovery must not be used by the Phase 10 product path.'
}

$networkServiceProtocolSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.NetworkService\Ipc\Protocol.cs') -Raw
$networkServiceProgramSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.NetworkService\Program.cs') -Raw
if ($networkServiceProtocolSource -notmatch 'string\s+Version' -or
    $networkServiceProgramSource -notmatch 'ServiceVersion') {
    throw 'NetworkService must expose its assembly version through the control protocol.'
}

$winUiProgramSource = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.WinUI\Program.cs') -Raw
if ($winUiProgramSource -notmatch 'return\s+"Shadowsocks\.Reborn\.WinUI"' -or
    $winUiProgramSource -match 'CreateInstanceKey[\s\S]{0,900}SHA256\.HashData') {
    throw 'Phase 10 requires one global per-user WinUI AppInstance key so normal/Clean instances cannot race on Windows integration state.'
}

if ($buildReleaseText -notmatch "exactly Shadowsocks\.exe" -and $buildReleaseText -notmatch "only Shadowsocks\.exe") {
    throw 'Build-Release.ps1 must enforce a one-file Shadowsocks.exe release.'
}
if ($buildReleaseText -notmatch "Shadowsocks\.NetworkService\.exe" -or
    $ciText -notmatch "Shadowsocks\.NetworkService\.exe") {
    throw 'Release and CI validators must explicitly reject a NetworkService sidecar.'
}
if ($ciText -notmatch '\$entries\.Count -ne 1' -or $ciText -notmatch "Shadowsocks\.exe") {
    throw 'CI must require exactly one product publish file: Shadowsocks.exe.'
}

Write-Host 'Validated Phase 10 LocalAppData/Clean-Mode storage policy, embedded-only localization, guarded on-demand NetworkService extraction, and one-file release layout.'
