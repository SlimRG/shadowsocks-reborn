[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
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
    'Shadowsocks.Engine\Shadowsocks.Engine.csproj',
    'Shadowsocks.UI\Shadowsocks.UI.csproj',
    'Shadowsocks.NetworkService\Shadowsocks.NetworkService.csproj',
    'Shadowsocks.UnitTests\Shadowsocks.UnitTests.csproj'
)
foreach ($relativeProject in $expectedProjects) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $relativeProject) -PathType Leaf)) {
        throw "Expected project is missing: $relativeProject"
    }
}


$solutionPath = Join-Path $repoRoot 'shadowsocks-reborn.sln'
if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw 'Expected solution is missing: shadowsocks-reborn.sln'
}

[xml]$uiProject = Get-Content -LiteralPath (Join-Path $repoRoot 'Shadowsocks.UI\Shadowsocks.UI.csproj') -Raw
$uiAssemblyName = @($uiProject.Project.PropertyGroup.AssemblyName | Where-Object { $_ })[0]
if ($uiAssemblyName -ne 'shadowsocks-reborn') {
    throw "Shadowsocks.UI must build shadowsocks-reborn.exe; AssemblyName is '$uiAssemblyName'."
}

$engineProjectPath = Join-Path $repoRoot 'Shadowsocks.Engine\Shadowsocks.Engine.csproj'
[xml]$engineProject = Get-Content -LiteralPath $engineProjectPath -Raw
$useWpf = @($engineProject.Project.PropertyGroup.UseWPF | Where-Object { $_ })[0]
$useWinForms = @($engineProject.Project.PropertyGroup.UseWindowsForms | Where-Object { $_ })[0]
if ($useWpf -eq 'true' -or $useWinForms -eq 'true') {
    throw 'Shadowsocks.Engine must not enable WPF or Windows Forms.'
}

$engineSources = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'Shadowsocks.Engine') -Recurse -File -Filter '*.cs'
$uiPatterns = @(
    'System\.Windows\.Forms',
    'System\.Windows(?:\.|;)',
    'WPFLocalizeExtension',
    'ReactiveUI\.WPF',
    'Shadowsocks\.(?:View|Views|ViewModels)',
    '\bProgram\.'
)
foreach ($pattern in $uiPatterns) {
    $match = $engineSources | Select-String -Pattern $pattern | Select-Object -First 1
    if ($null -ne $match) {
        $relative = [System.IO.Path]::GetRelativePath($repoRoot, $match.Path)
        throw "UI framework dependency leaked into Shadowsocks.Engine: ${relative}:$($match.LineNumber): $($match.Line.Trim())"
    }
}

Write-Host 'Validated project layout: Engine has no WinForms/WPF references.'
