<#
.SYNOPSIS
    构建 HistoryVulcan 宿主候选快照。
.DESCRIPTION
    这是 Diana Publish-OneHistoryModule.ps1 对 BuildScript 的统一调用形状：只产候选、
    不提升正式区。版本从 VulcanVersion.props 现读，不由调用方传入。
    宿主与模块的快照形状不同（host/ 与 docs/ 多层目录、manifest.json 而非
    module.manifest.json、SHA256SUMS 用带 / 的相对路径），差异在 Diana 侧按 Kind 区分。
#>
param(
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$componentRoot = Join-Path $repoRoot 'b-Code-HistoryVulcan'
$publishRoot = Join-Path $repoRoot 'z-Publish'
$candidateRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $publishRoot
} else {
    [IO.Path]::GetFullPath($OutputRoot)
}
$solution = Join-Path $repoRoot 'HistoryVulcan.sln'
$project = Join-Path $componentRoot 'App\App.csproj'
$documentRoot = Join-Path $repoRoot 'b-Office\package'
$releaseRoot = Join-Path $PSScriptRoot 'release'
$documentManifestPath = Join-Path $releaseRoot 'consumer-docs.json'
$versionOutput = & dotnet msbuild $project -nologo -getProperty:VulcanVersion -getProperty:FileVersion
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to evaluate HistoryVulcan version source'
}
$versionProperties = (($versionOutput -join "`n") | ConvertFrom-Json).Properties
$Version = [string]$versionProperties.VulcanVersion
$expectedFileVersion = [string]$versionProperties.FileVersion
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Invalid semantic version: $Version"
}

$documentManifest = Get-Content -LiteralPath $documentManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$documentNames = @($documentManifest.documents | ForEach-Object { [string]$_.file })
if ($documentManifest.schemaVersion -ne 1 -or $documentNames.Count -eq 0 -or
    @($documentNames | Select-Object -Unique).Count -ne $documentNames.Count) {
    throw 'Consumer document manifest is invalid'
}

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Assert-HostDirectory {
    param([string]$Path)
    $exe = Join-Path $Path 'HistoryVulcan.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Host snapshot is missing HistoryVulcan.exe: $Path"
    }
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion
    if ($fileVersion -ne $expectedFileVersion) {
        throw "Host executable version $fileVersion does not match $expectedFileVersion"
    }
}

function Get-SnapshotFiles {
    param([string]$Path)
    $pathPrefix = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $installerPrefix = $pathPrefix + 'installer' + [IO.Path]::DirectorySeparatorChar
    $historyPrefix = $pathPrefix + 'history' + [IO.Path]::DirectorySeparatorChar
    return @(Get-ChildItem -LiteralPath $Path -Recurse -File |
        Where-Object {
            $_.Name -ne 'SHA256SUMS' -and
            -not $_.FullName.StartsWith($historyPrefix, [StringComparison]::OrdinalIgnoreCase) -and
            -not $_.FullName.StartsWith($installerPrefix, [StringComparison]::OrdinalIgnoreCase)
        } |
        Sort-Object FullName)
}

function Assert-Snapshot {
    param([string]$Path)

    Assert-HostDirectory (Join-Path $Path 'host')
    $docsRoot = Join-Path $Path 'docs'
    if (-not (Test-Path -LiteralPath $docsRoot -PathType Container)) {
        throw 'Snapshot is missing docs/'
    }
    foreach ($documentName in $documentNames) {
        if (-not (Test-Path -LiteralPath (Join-Path $docsRoot $documentName) -PathType Leaf)) {
            throw "Snapshot is missing docs/$documentName"
        }
    }
    foreach ($required in @('manifest.json', 'SHA256SUMS')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $required) -PathType Leaf)) {
            throw "Snapshot is missing $required"
        }
    }

    $manifest = Get-Content -LiteralPath (Join-Path $Path 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]$manifest.version -ne $Version -or [string]$manifest.product -ne 'HistoryVulcan') {
        throw 'Snapshot manifest identity does not match the requested HistoryVulcan version'
    }

    $pathPrefix = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $checksumLines = @(Get-Content -LiteralPath (Join-Path $Path 'SHA256SUMS') -Encoding UTF8)
    foreach ($line in $checksumLines) {
        if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') {
            throw "Malformed checksum line: $line"
        }
        $target = [IO.Path]::GetFullPath((Join-Path $Path $matches[2].Replace('/', '\')))
        if (-not $target.StartsWith($pathPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $target -PathType Leaf)) {
            throw "Checksum target escaped or is missing: $($matches[2])"
        }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $matches[1].ToUpperInvariant()) {
            throw "Checksum mismatch: $($matches[2])"
        }
    }
    if ($checksumLines.Count -ne (Get-SnapshotFiles $Path).Count) {
        throw 'Snapshot checksum coverage mismatch'
    }
}

$transactionRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'HistoryVulcan.Package.' + [Guid]::NewGuid().ToString('N'))
$temporary = Join-Path $transactionRoot 'candidate'
$candidateBackup = Join-Path $transactionRoot 'previous'
$buildOutputRoot = Join-Path $transactionRoot 'build'
try {
    $temporaryHost = Join-Path $temporary 'host'
    $temporaryDocs = Join-Path $temporary 'docs'
    New-Item -ItemType Directory -Force -Path $temporaryHost, $temporaryDocs | Out-Null

    Invoke-Dotnet @(
        'restore', $solution, '--locked-mode', '--nologo',
        '-p:NuGetAudit=false')
    Invoke-Dotnet @(
        'restore', $project, '-r', 'win-x64', '--locked-mode', '--nologo',
        '-p:NuGetAudit=false', '-p:RestoreRecursive=false')
    Invoke-Dotnet @(
        'publish', $project, '-c', 'Release', '--no-restore',
        '--self-contained', 'false', '-r', 'win-x64', '-o', $temporaryHost,
        ('-p:BaseOutputPath=' + (Join-Path $buildOutputRoot '')),
        '-p:NuGetAudit=false')
    Assert-HostDirectory $temporaryHost

    foreach ($documentName in $documentNames) {
        $documentSource = Join-Path $documentRoot $documentName
        if (-not (Test-Path -LiteralPath $documentSource -PathType Leaf)) {
            throw "Consumer document is missing: $documentSource"
        }
        Copy-Item -LiteralPath $documentSource -Destination (Join-Path $temporaryDocs $documentName)
    }

    $sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
    $releaseInputs = @('b-Code-HistoryVulcan', 'b-Code-Eng', 'b-Office/package', 'project.manifest.json')
    $sourceStatus = & git -C $repoRoot status --porcelain -- $releaseInputs
    $sourceDirty = -not [string]::IsNullOrWhiteSpace(($sourceStatus -join "`n"))
    $payloadFiles = Get-SnapshotFiles $temporary
    $manifest = [ordered]@{
        schemaVersion = 1
        product = 'HistoryVulcan'
        version = $Version
        channel = 'current-host'
        runtime = 'win-x64-framework-dependent'
        executable = 'host/HistoryVulcan.exe'
        selfContained = $false
        sourceCommit = $sourceCommit
        sourceDirty = $sourceDirty
        documents = @($documentNames)
        files = @($payloadFiles | ForEach-Object {
            $relative = $_.FullName.Substring($temporary.Length).TrimStart('\', '/').Replace('\', '/')
            [ordered]@{
                file = $relative
                bytes = $_.Length
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            }
        })
    }
    [IO.File]::WriteAllText(
        (Join-Path $temporary 'manifest.json'),
        (($manifest | ConvertTo-Json -Depth 8) + "`n"),
        [Text.UTF8Encoding]::new($false))

    $checksumLines = Get-SnapshotFiles $temporary | ForEach-Object {
        $relative = $_.FullName.Substring($temporary.Length).TrimStart('\', '/').Replace('\', '/')
        "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)  $relative"
    }
    [IO.File]::WriteAllLines(
        (Join-Path $temporary 'SHA256SUMS'),
        $checksumLines,
        [Text.UTF8Encoding]::new($false))
    Assert-Snapshot $temporary

    New-Item -ItemType Directory -Force -Path $candidateRoot, $candidateBackup | Out-Null
    $movedPrevious = [Collections.Generic.List[string]]::new()
    $movedCandidate = [Collections.Generic.List[string]]::new()
    try {
        foreach ($item in @(Get-ChildItem -LiteralPath $candidateRoot -Force |
                Where-Object { $_.Name -ne 'history' })) {
            Move-Item -LiteralPath $item.FullName -Destination $candidateBackup
            $movedPrevious.Add($item.Name)
        }
        foreach ($item in @(Get-ChildItem -LiteralPath $temporary -Force)) {
            Move-Item -LiteralPath $item.FullName -Destination $candidateRoot
            $movedCandidate.Add($item.Name)
        }
    }
    catch {
        foreach ($name in $movedCandidate) {
            $path = Join-Path $candidateRoot $name
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Recurse -Force
            }
        }
        foreach ($name in $movedPrevious) {
            $path = Join-Path $candidateBackup $name
            if (Test-Path -LiteralPath $path) {
                Move-Item -LiteralPath $path -Destination $candidateRoot
            }
        }
        throw
    }
    Write-Host "Prepared HistoryVulcan $Version host snapshot at $candidateRoot"
}
finally {
    if (Test-Path -LiteralPath $transactionRoot) {
        Remove-Item -LiteralPath $transactionRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
