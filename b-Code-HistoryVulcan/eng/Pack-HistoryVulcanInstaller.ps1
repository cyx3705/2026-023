param(
    [Parameter(Mandatory = $false)]
    [string]$Version,
    [string]$SnapshotRoot,
    [string]$OutputRoot,
    [string]$IsccPath = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    [string]$SevenZipPath = 'C:\Program Files\7-Zip\7z.exe'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$componentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRoot = [IO.Path]::GetFullPath((Join-Path $componentRoot '..'))
$issPath = Join-Path $PSScriptRoot 'release\HistoryVulcan.iss'
$formalRoot = Join-Path $repoRoot 'z-HistoryVulcan'

if (-not $SnapshotRoot) {
    $SnapshotRoot = $formalRoot
}
$SnapshotRoot = [IO.Path]::GetFullPath($SnapshotRoot)

$manifestPath = Join-Path $SnapshotRoot 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Snapshot manifest missing: $manifestPath"
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]$manifest.product -ne 'HistoryVulcan') {
    throw "Unexpected product in snapshot manifest: $($manifest.product)"
}
if (-not $Version) {
    $Version = [string]$manifest.version
}
if ([string]$manifest.version -ne $Version) {
    throw "Snapshot version $($manifest.version) does not match requested $Version"
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Invalid semantic version: $Version"
}

$hostExe = Join-Path $SnapshotRoot 'host\HistoryVulcan.exe'
if (-not (Test-Path -LiteralPath $hostExe -PathType Leaf)) {
    throw "Host executable missing: $hostExe"
}
$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($hostExe).FileVersion
$expectedFileVersion = if ($Version -match '^(\d+\.\d+\.\d+)') { "$($Matches[1]).0" } else { "$Version.0" }
if ($fileVersion -ne $expectedFileVersion -and $fileVersion -ne $Version) {
    throw "Host FileVersion $fileVersion does not match $expectedFileVersion"
}

if (-not $OutputRoot) {
    $OutputRoot = Join-Path $formalRoot 'installer'
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

if (-not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw "Inno Setup compiler not found: $IsccPath"
}
if (-not (Test-Path -LiteralPath $SevenZipPath -PathType Leaf)) {
    throw "7-Zip not found: $SevenZipPath"
}
if (-not (Test-Path -LiteralPath $issPath -PathType Leaf)) {
    throw "Inno script missing: $issPath"
}

$setupBase = "HistoryVulcan-$Version-Setup"
$portableBase = "HistoryVulcan-$Version-win-x64"
$setupExe = Join-Path $OutputRoot "$setupBase.exe"
$portableArchive = Join-Path $OutputRoot "$portableBase.7z"

foreach ($stale in @(
        $setupExe,
        $portableArchive,
        (Join-Path $OutputRoot 'SHA256SUMS'),
        (Join-Path $OutputRoot 'README.md'),
        (Join-Path $OutputRoot 'manifest.json'))) {
    if (Test-Path -LiteralPath $stale) {
        Remove-Item -LiteralPath $stale -Force
    }
}

$stageRoot = Join-Path $repoRoot ('b-Publish\.installer-stage-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $SnapshotRoot -Force) {
        if ($item.Name -eq 'installer') {
            continue
        }
        Copy-Item -LiteralPath $item.FullName -Destination $stageRoot -Recurse -Force
    }

    Write-Host "Packing portable archive from staged snapshot"
    & $SevenZipPath a -t7z -mx=9 -m0=lzma2 $portableArchive (Join-Path $stageRoot '*') | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "7-Zip failed with exit code $LASTEXITCODE"
    }

    Write-Host "Compiling Inno Setup installer"
    & $IsccPath `
        "/DMyAppVersion=$Version" `
        "/DSourceDir=$stageRoot" `
        "/DOutputDir=$OutputRoot" `
        "/DOutputBase=$setupBase" `
        $issPath | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC failed with exit code $LASTEXITCODE"
    }
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path -LiteralPath $setupExe -PathType Leaf)) {
    throw "Installer was not produced: $setupExe"
}

$readme = @"
# HistoryVulcan $Version installer package

Built from formal host snapshot ``z-HistoryVulcan`` (win-x64, framework-dependent).
Delivered under ``z-HistoryVulcan/installer/``.

## Artifacts

| File | Purpose |
| --- | --- |
| ``$setupBase.exe`` | Windows installer (Program Files, Start Menu, optional desktop shortcut) |
| ``$portableBase.7z`` | Portable snapshot (host/docs layout without this installer folder) |

## Requirements

- Windows 10/11 x64
- .NET 8 Desktop Runtime (framework-dependent host)

## Install

1. Run ``$setupBase.exe`` as administrator (or accept UAC).
2. Launch HistoryVulcan from the Start Menu, or run ``{install}\host\HistoryVulcan.exe``.

## Portable

Extract ``$portableBase.7z`` and run ``host\HistoryVulcan.exe``.

## Integrity

See ``SHA256SUMS`` in this directory.
"@
[IO.File]::WriteAllText((Join-Path $OutputRoot 'README.md'), $readme, [Text.UTF8Encoding]::new($false))

$artifactFiles = @(
    Get-Item -LiteralPath $setupExe
    Get-Item -LiteralPath $portableArchive
    Get-Item -LiteralPath (Join-Path $OutputRoot 'README.md')
) | Sort-Object Name

$checksumLines = foreach ($file in $artifactFiles) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    "$hash  $($file.Name)"
}
[IO.File]::WriteAllLines((Join-Path $OutputRoot 'SHA256SUMS'), $checksumLines, [Text.UTF8Encoding]::new($false))

$packageManifest = [ordered]@{
    schemaVersion = 1
    product = 'HistoryVulcan'
    version = $Version
    channel = 'installer'
    runtime = [string]$manifest.runtime
    selfContained = [bool]$manifest.selfContained
    sourceSnapshot = 'z-HistoryVulcan'
    sourceCommit = [string]$manifest.sourceCommit
    artifacts = @($artifactFiles | ForEach-Object {
        [ordered]@{
            file = $_.Name
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
}
[IO.File]::WriteAllText(
    (Join-Path $OutputRoot 'manifest.json'),
    (($packageManifest | ConvertTo-Json -Depth 8) + "`n"),
    [Text.UTF8Encoding]::new($false))

Write-Host "HistoryVulcan $Version installer package ready at $OutputRoot"
Get-ChildItem -LiteralPath $OutputRoot -File | ForEach-Object {
    Write-Host ("  {0,12}  {1}" -f $_.Length, $_.Name)
}
