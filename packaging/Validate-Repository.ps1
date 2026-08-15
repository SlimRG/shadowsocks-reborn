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
