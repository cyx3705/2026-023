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

$componentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repoRoot = [IO.Path]::GetFullPath((Join-Path $componentRoot '..'))
$publishRoot = Join-Path $repoRoot 'b-Publish'
$candidateRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $publishRoot 'current'
} else {
    [IO.Path]::GetFullPath($OutputRoot)
}
$solution = Join-Path $repoRoot 'HistoryVulcan.sln'
$project = Join-Path $componentRoot 'src\App\App.csproj'
$documentRoot = Join-Path $repoRoot 'b-Office\package'
$releaseRoot = Join-Path $componentRoot 'eng\release'
$documentManifestPath = Join-Path $releaseRoot 'consumer-docs.json'
$reuseTemplatePath = Join-Path $releaseRoot 'HistoryVulcan.reuse.template.md'
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
    return @(Get-ChildItem -LiteralPath $Path -Recurse -File |
        Where-Object {
            $_.Name -ne 'SHA256SUMS' -and
            -not $_.FullName.StartsWith($installerPrefix, [StringComparison]::OrdinalIgnoreCase)
        } |
        Sort-Object FullName)
}

function Assert-Snapshot {
    param([string]$Path)

    Assert-HostDirectory (Join-Path $Path 'host')
    foreach ($required in @('HistoryVulcan.reuse.md', 'README.md', 'manifest.json', 'SHA256SUMS')) {
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

New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
$temporary = Join-Path $publishRoot ('.current-next-' + [Guid]::NewGuid().ToString('N'))
$candidateBackup = Join-Path $publishRoot ('.current-previous-' + [Guid]::NewGuid().ToString('N'))
$buildOutputRoot = Join-Path $publishRoot ('.build-' + [Guid]::NewGuid().ToString('N'))
try {
    $temporaryHost = Join-Path $temporary 'host'
    New-Item -ItemType Directory -Force -Path $temporaryHost | Out-Null

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

    $reuseTemplate = Get-Content -LiteralPath $reuseTemplatePath -Raw -Encoding UTF8
    if ($reuseTemplate.IndexOf('{{VERSION}}', [StringComparison]::Ordinal) -lt 0) {
        throw 'Reuse template is missing the {{VERSION}} placeholder'
    }
    [IO.File]::WriteAllText(
        (Join-Path $temporary 'HistoryVulcan.reuse.md'),
        $reuseTemplate.Replace('{{VERSION}}', $Version),
        [Text.UTF8Encoding]::new($false))

    $readme = @"
# HistoryVulcan $Version

This is the current HistoryVulcan host snapshot.

- `host/HistoryVulcan.exe`: win-x64, framework-dependent HistoryVulcan host.
- `HistoryVulcan.reuse.md`: minimal entry point for projects and AI consumers.
- `manifest.json` and `SHA256SUMS`: snapshot identity and integrity.

Run `host/HistoryVulcan.exe`. Historical releases are stored under `b-Publish/history/`.
"@
    [IO.File]::WriteAllText(
        (Join-Path $temporary 'README.md'),
        $readme,
        [Text.UTF8Encoding]::new($false))

    $sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
    $releaseInputs = @('b-Code-HistoryVulcan', 'b-Office/package', 'project.manifest.json')
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
        # 消费文档不再随快照分发：单一真值由 HistoryDiana 的 b-Office-OneHistory 托管，
        # 发布管线在每次部署后同步镜像。快照内再放一份只会产生第二处会漂移的副本。
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

    $candidateMoved = $false
    try {
        if (Test-Path -LiteralPath $candidateRoot) {
            Move-Item -LiteralPath $candidateRoot -Destination $candidateBackup
            $candidateMoved = $true
        }
        Move-Item -LiteralPath $temporary -Destination $candidateRoot
        if ($candidateMoved) {
            Remove-Item -LiteralPath $candidateBackup -Recurse -Force
        }
    }
    catch {
        if ($candidateMoved -and -not (Test-Path -LiteralPath $candidateRoot) -and
            (Test-Path -LiteralPath $candidateBackup)) {
            Move-Item -LiteralPath $candidateBackup -Destination $candidateRoot
        }
        throw
    }
    Write-Host "Prepared HistoryVulcan $Version host snapshot at $candidateRoot"
}
finally {
    foreach ($path in @($temporary, $candidateBackup, $buildOutputRoot)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
