[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$activeRoots = @('b-Code-HistoryVulcan\src', 'b-Code-Samples')
$excluded = '\\(bin|obj|artifacts|history|b-Publish|z-HistoryVulcan)\\'
$suppressionPattern = 'NoWarn|SuppressMessage|#pragma\s+warning\s+disable'
$violations = [System.Collections.Generic.List[string]]::new()

foreach ($relativeRoot in $activeRoots) {
    $path = Join-Path $root $relativeRoot
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $files = Get-ChildItem -LiteralPath $path -Recurse -File |
        Where-Object { $_.Extension -in '.cs', '.csproj', '.props', '.targets' -and $_.FullName -notmatch $excluded }
    foreach ($file in $files) {
        $text = [IO.File]::ReadAllText($file.FullName)
        if ($text -match $suppressionPattern) {
            $violations.Add("Suppression token: $($file.FullName)")
        }
    }
}

$hotspots = Get-ChildItem -LiteralPath (Join-Path $root 'b-Code-HistoryVulcan\src') -Recurse -File |
    Where-Object { $_.Extension -in '.cs', '.xaml' -and $_.FullName -notmatch $excluded } |
    ForEach-Object { [pscustomobject]@{ Path = $_.FullName; Lines = (Get-Content -LiteralPath $_.FullName).Count } } |
    Where-Object Lines -gt 1000 |
    Sort-Object Lines -Descending

foreach ($hotspot in $hotspots) {
    Write-Warning ("Hotspot: {0} ({1} lines); split by responsibility." -f $hotspot.Path, $hotspot.Lines)
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Error $_ }
    exit 1
}
if ($hotspots.Count -gt 0) {
    Write-Error 'Production files exceed the 1000-line quality limit.'
    exit 1
}
Write-Host ("Quality gate passed: suppression tokens 0; hotspots reported {0}." -f $hotspots.Count)
