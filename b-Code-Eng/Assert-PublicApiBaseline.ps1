#requires -Version 5.1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# REQ-A8：HistoryVulcan.Shell 已随前端整体迁往 2026-026-HistoryAurora，宿主不再有该工程。
# 门禁只核对本仓仍发布的三份 Unshipped：Core / Services / ServiceHost。
$projects = @(
    'HistoryVulcan.Core'
    'HistoryVulcan.Services'
    'HistoryVulcan.ServiceHost'
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$componentRoot = Join-Path $repoRoot 'b-Code-HistoryVulcan'
$violations = @()
$versionProps = Get-Content -LiteralPath (Join-Path $componentRoot 'VulcanVersion.props') -Raw -Encoding UTF8
$versionMatch = [regex]::Match($versionProps, '<VulcanVersion>(?<version>[^<]+)</VulcanVersion>')
if (-not $versionMatch.Success) {
    throw 'VulcanVersion.props does not contain VulcanVersion'
}
$version = $versionMatch.Groups['version'].Value

# 批准基线只住在 public-api-baselines\<当前版本>\。升版本时由发布器从上一版目录复制；
# 历史版本目录不进当前门禁，也不在本仓保留（旧标签各自带当时副本）。
$baselineDir = Join-Path $PSScriptRoot "public-api-baselines\$version"
if (-not (Test-Path -LiteralPath $baselineDir -PathType Container)) {
    throw "Missing approved Unshipped baseline directory for $version : $baselineDir"
}

$approved = @{}
foreach ($project in $projects) {
    $baselinePath = Join-Path $baselineDir "$project.Unshipped.txt"
    if (-not (Test-Path -LiteralPath $baselinePath -PathType Leaf)) {
        throw "Missing approved Unshipped baseline: $baselinePath"
    }
    $approved[$project] = @(
        [System.IO.File]::ReadAllLines($baselinePath, [System.Text.UTF8Encoding]::new($false)) |
            ForEach-Object { $_.Trim().TrimStart([char]0xFEFF) } |
            Where-Object { $_ -ne '' -and $_ -ne '#nullable enable' }
    )
}

foreach ($project in $projects) {
    $path = Join-Path $componentRoot "$project\PublicAPI.Unshipped.txt"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $violations += "$project : PublicAPI.Unshipped.txt missing"
        continue
    }

    $entries = @(
        [System.IO.File]::ReadAllLines($path, [System.Text.UTF8Encoding]::new($false)) |
            ForEach-Object { $_.Trim().TrimStart([char]0xFEFF) } |
            Where-Object { $_ -ne '' -and $_ -ne '#nullable enable' }
    )

    $difference = @(Compare-Object -ReferenceObject @($approved[$project]) -DifferenceObject $entries)
    if ($difference.Count -ne 0) {
        $violations += "$project : unshipped API differs from approved $version baseline -> $($difference -join '; ')"
    }
}

if ($violations.Count -ne 0) {
    Write-Host 'Public API freeze gate failed:' -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'Public API changes must exactly match the version-approved Unshipped baseline.'
    exit 1
}

Write-Host "Public API baseline gate passed: $($projects.Count) package Unshipped files match the approved $version baseline."
