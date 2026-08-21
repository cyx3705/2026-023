[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Module,
    [switch]$Publish,
    [switch]$AllowDirtySource,
    [switch]$RequireCleanSource,
    # AI 工作区里的提交级验证：从指定工作树构建并跑门禁。正式 Clio z 的 -Publish
    # 永远只从主树来，因此本参数与 -Publish 互斥。无 -Publish 时，工作树把已验证
    # 候选写入该树自己的版本化 z-Publish 包，供 diana.release.cycle 严格安装到运行区。
    # 不能叫 ProjectRoot：PowerShell 变量大小写不敏感，会和脚本内的 $projectRoot 撞成同一个，
    # 被后者覆盖后判断恒真，表现为主树构建也去传工作树参数。
    [string]$SourceWorktree
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$dianaRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectsRoot = [IO.Path]::GetFullPath((Join-Path $dianaRoot '..'))
$transactionId = [Guid]::NewGuid().ToString('N')
$stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
$workRoot = $null

# 普通 module 的定义与验证步骤由注册表提供；新增普通模块只需新增一项 JSON。
$registryPath = Join-Path $dianaRoot 'b-Code\module-publish.manifest.json'
if (-not (Test-Path -LiteralPath $registryPath -PathType Leaf)) {
    throw "Module publish registry is missing: $registryPath"
}
$registry = [IO.File]::ReadAllText($registryPath, [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
if ($registry.schemaVersion -ne 1) {
    throw "Unsupported module publish registry schema: $($registry.schemaVersion)"
}
$definitions = @{}
foreach ($entry in @($registry.modules)) {
    if ([string]::IsNullOrWhiteSpace([string]$entry.name) -or $entry.kind -ne 'module') {
        throw 'Every registry module must declare a non-empty name and kind=module.'
    }
    if ($definitions.ContainsKey([string]$entry.name)) {
        throw "Duplicate module publish registry entry: $($entry.name)"
    }
    $definitions[[string]$entry.name] = $entry
}

# Vulcan 是宿主，保留宿主快照与门禁的特例配置。
$definitions['HistoryVulcan'] = [ordered]@{
        Kind = 'host'
        ProjectDirectory = '2026-023-HistoryVulcan'
        VersionProps = 'b-Code-HistoryVulcan\VulcanVersion.props'
        VersionProperty = 'VulcanVersion'
        # 宿主没有"源 manifest"：身份由版本源加快照 manifest 表达，没有第三处可漂移。
        SourceManifest = ''
        SnapshotManifest = 'manifest.json'
        IdentityProperty = 'product'
        CandidateDirectory = 'z-Publish'
        FormalDirectory = 'z-Publish'
        BuildScript = 'b-Code-HistoryVulcan\eng\Build-HistoryVulcanPackage.ps1'
        PackageDocuments = 'b-Office\package'
        ContractScript = ''
        SmokeProject = ''
        UiSmokeProject = ''
        TestProject = 'b-Code-Tests\HistoryVulcan.Tests\HistoryVulcan.Tests.csproj'
        GateScripts = @(
            'b-Code-HistoryVulcan\eng\Test-QualityGate.ps1',
            'b-Code-HistoryVulcan\eng\Assert-PublicApiBaseline.ps1'
        )
}

function Repair-VulcanAutostart {
    param([Parameter(Mandatory = $true)][string]$ModuleRoot)

    $hostExecutable = Join-Path $ModuleRoot 'host\HistoryVulcan.exe'
    if (-not (Test-Path -LiteralPath $hostExecutable -PathType Leaf)) {
        Write-Warning "HistoryVulcan 登录启动未修复：正式快照中找不到 $hostExecutable"
        return
    }

    try {
        & $hostExecutable '--repair-autostart'
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "HistoryVulcan 登录启动修复失败（退出码 $LASTEXITCODE）"
            return
        }
        Write-Host "HistoryVulcan 登录启动已按正式 Z 快照修复"
    }
    catch {
        Write-Warning "HistoryVulcan 登录启动修复失败：$($_.Exception.Message)"
    }
}

function Get-VulcanHostRoot {
    $publishRoot = Join-Path $projectsRoot '2026-023-HistoryVulcan\z-Publish'
    if (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) {
        return $null
    }

    # Vulcan 4.0.0 的当前快照固定是 z-Publish/host（不是模块式的
    # HistoryVulcan-v<version> 子目录）。优先返回平铺根，避免旧迁移包
    # 抢在当前宿主前被模块构建选中。
    $flatManifest = Join-Path $publishRoot 'manifest.json'
    $flatSums = Join-Path $publishRoot 'SHA256SUMS'
    $flatHost = Join-Path $publishRoot 'host\HistoryVulcan.exe'
    if ((Test-Path -LiteralPath $flatManifest -PathType Leaf) -and
        (Test-Path -LiteralPath $flatSums -PathType Leaf) -and
        (Test-Path -LiteralPath $flatHost -PathType Leaf)) {
        try {
            $identity = Get-PackageIdentity $publishRoot
            if ($identity.Name -ne 'HistoryVulcan') {
                throw "宿主快照 manifest product 不是 HistoryVulcan: $($identity.Name)"
            }
            return Assert-ChildPath $publishRoot $projectsRoot 'Vulcan flat host snapshot'
        }
        catch {
            throw "HistoryVulcan flat host snapshot is invalid: $($_.Exception.Message)"
        }
    }

    $flatShapePresent = (Test-Path -LiteralPath $flatManifest -PathType Leaf) -or
        (Test-Path -LiteralPath $flatSums -PathType Leaf) -or
        (Test-Path -LiteralPath (Join-Path $publishRoot 'host') -PathType Container)
    if ($flatShapePresent) {
        throw "HistoryVulcan flat host snapshot is incomplete or invalid: $publishRoot"
    }

    # 仅为 4.0 迁移窗口保留旧版化候选兼容；新宿主候选不得再走这条路径。
    $packages = @(Get-ChildItem -LiteralPath $publishRoot -Directory -Filter 'HistoryVulcan-v*' |
        Where-Object {
            (Test-Path -LiteralPath (Join-Path $_.FullName 'manifest.json') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $_.FullName 'SHA256SUMS') -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $_.FullName 'host\HistoryVulcan.exe') -PathType Leaf)
        })
    if ($packages.Count -eq 1) {
        return Assert-ChildPath $packages[0].FullName $projectsRoot 'Vulcan host snapshot'
    }
    throw "HistoryVulcan z-Publish has no valid flat host snapshot or migration candidate: $publishRoot"
}

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    $fullParent = [IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($fullParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description escapes its allowed root: $fullPath"
    }
    return $fullPath
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$Description
    )

    Write-Host "[$Module] $Description"
    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$Description failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }
}

function Read-ModuleVersion {
    param([string]$PropsPath, [string]$PropertyName)

    [xml]$props = [IO.File]::ReadAllText($PropsPath, [Text.UTF8Encoding]::new($false))
    $values = @($props.Project.PropertyGroup | ForEach-Object {
        if ($_.PSObject.Properties.Name -contains $PropertyName) {
            $_.PSObject.Properties[$PropertyName].Value
        }
    } | Where-Object { $_ })
    if ($values.Count -ne 1 -or [string]$values[0] -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version source must declare exactly one semantic version property ${PropertyName}: $PropsPath"
    }
    return [string]$values[0]
}

function Assert-ModuleSnapshot {
    param(
        [string]$Root,
        [string]$ExpectedName,
        [string]$ExpectedVersion,
        [string]$ManifestName = 'module.manifest.json',
        [string]$IdentityProperty = 'name',
        [string]$Kind = 'module'
    )

    $manifestPath = Join-Path $Root $ManifestName
    $sumsPath = Join-Path $Root 'SHA256SUMS'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $sumsPath -PathType Leaf)) {
        throw "Module snapshot is incomplete: $Root"
    }

    $manifest = [IO.File]::ReadAllText($manifestPath, [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
    if ($manifest.$IdentityProperty -ne $ExpectedName -or $manifest.version -ne $ExpectedVersion) {
        throw "Module snapshot identity mismatch: expected $ExpectedName $ExpectedVersion"
    }

    $hashes = @{}
    foreach ($line in [IO.File]::ReadAllLines($sumsPath, [Text.UTF8Encoding]::new($false))) {
        if ($line -notmatch '^(?<hash>[0-9A-Fa-f]{64}) [ *](?<file>.+)$') {
            throw "Invalid SHA256SUMS line in ${Root}: $line"
        }
        if ($hashes.ContainsKey($Matches.file)) {
            throw "Duplicate checksum entry in ${Root}: $($Matches.file)"
        }
        $hashes[$Matches.file] = $Matches.hash.ToUpperInvariant()
    }

    # Janus and host snapshots have nested directories; flat modules produce the same relative keys as file names.
    $rootPrefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $historyPrefix = [IO.Path]::GetFullPath((Join-Path $Root 'history')).TrimEnd('\') + '\'
    $files = @(Get-ChildItem -LiteralPath $Root -File -Recurse |
        Where-Object {
            $_.Name -ne 'SHA256SUMS' -and
            -not ($Kind -eq 'host' -and $_.FullName.StartsWith($historyPrefix, [StringComparison]::OrdinalIgnoreCase))
        })
    if ($files.Count -ne $hashes.Count) {
        throw ('SHA256SUMS does not cover the complete snapshot ({0} files vs {1} entries): {2}' -f $files.Count, $hashes.Count, $Root)
    }
    foreach ($file in $files) {
        $key = $file.FullName.Substring($rootPrefix.Length).Replace('\', '/')
        $actual = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        if (-not $hashes.ContainsKey($key) -or $hashes[$key] -ne $actual) {
            throw "Checksum mismatch in ${Root}: $key"
        }
    }
}

function Get-ChecksumMarker {
    param([Parameter(Mandatory = $true)][string]$SumsPath)

    if (Test-Path -LiteralPath $SumsPath -PathType Leaf) {
        foreach ($line in [IO.File]::ReadAllLines($SumsPath, [Text.UTF8Encoding]::new($false))) {
            if ($line -match '^[0-9A-Fa-f]{64} \*') { return ' *' }
            if ($line -match '^[0-9A-Fa-f]{64}  ') { return '  ' }
        }
    }
    return '  '
}

function Write-SnapshotChecksums {
    param([Parameter(Mandatory = $true)][string]$Root)

    $rootPrefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $sumsPath = Join-Path $Root 'SHA256SUMS'
    $marker = Get-ChecksumMarker $sumsPath
    $files = @(Get-ChildItem -LiteralPath $Root -File -Recurse |
        Where-Object Name -ne 'SHA256SUMS' |
        Sort-Object FullName)
    if ($files.Count -eq 0) {
        throw "Snapshot has no files to checksum: $Root"
    }
    $lines = foreach ($file in $files) {
        $key = $file.FullName.Substring($rootPrefix.Length).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToUpperInvariant()
        "$hash$marker$key"
    }
    [IO.File]::WriteAllLines($sumsPath, $lines, [Text.UTF8Encoding]::new($false))
}

function Get-PackageIdentity {
    param([Parameter(Mandatory = $true)][string]$Root)

    foreach ($pair in @(
        @('module.manifest.json', 'name'),
        @('manifest.json', 'product')
    )) {
        $path = Join-Path $Root $pair[0]
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $manifest = [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
        $name = [string]$manifest.($pair[1])
        $version = [string]$manifest.version
        if (-not [string]::IsNullOrWhiteSpace($name) -and $version -match '^\d+\.\d+\.\d+$') {
            return [pscustomobject]@{ Name = $name; Version = $version }
        }
    }
    throw "Package identity manifest is missing or invalid: $Root"
}

function Assert-ArchiveSlot {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Destination -PathType Container)) { return }
    $sourceSums = [IO.File]::ReadAllText((Join-Path $Source 'SHA256SUMS')).Replace("`r`n", "`n")
    $destinationSums = [IO.File]::ReadAllText((Join-Path $Destination 'SHA256SUMS')).Replace("`r`n", "`n")
    if (-not $sourceSums.Equals($destinationSums, [StringComparison]::OrdinalIgnoreCase)) {
        throw "History package already exists with different content. Bump the version instead of overwriting: $Destination"
    }
}

function Copy-PackageDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

function Archive-PackageDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Package,
        [Parameter(Mandatory = $true)][string]$HistoryRoot
    )

    $identity = Get-PackageIdentity $Package
    $archive = Join-Path $HistoryRoot "$($identity.Name)-v$($identity.Version)"
    if (Test-Path -LiteralPath $archive -PathType Container) {
        $sourceSums = [IO.File]::ReadAllText((Join-Path $Package 'SHA256SUMS')).Replace("`r`n", "`n")
        $destinationSums = [IO.File]::ReadAllText((Join-Path $archive 'SHA256SUMS')).Replace("`r`n", "`n")
        if ($sourceSums.Equals($destinationSums, [StringComparison]::OrdinalIgnoreCase)) {
            return $archive
        }
        # 同版本归档已存在且内容不同：当前候选仍必须让位给新版本，写入旁路时间戳槽。
        $archive = Join-Path $HistoryRoot ("{0}-v{1}-{2}" -f $identity.Name, $identity.Version, (Get-Date -Format 'yyyyMMdd-HHmmss'))
    }
    Copy-PackageDirectory $Package $archive
    Write-Host "Archived previous candidate: $archive"
    return $archive
}

function Publish-VersionedCandidate {
    param(
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][string]$ExpectedName,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion
    )

    $packageName = "$ExpectedName-v$ExpectedVersion"
    $historyRoot = Join-Path $PublishRoot 'history'
    New-Item -ItemType Directory -Force -Path $historyRoot | Out-Null
    $incoming = Join-Path $PublishRoot ".incoming-$transactionId"
    Copy-PackageDirectory $Stage $incoming
    Assert-ModuleSnapshot $incoming $ExpectedName $ExpectedVersion $definition.SnapshotManifest $definition.IdentityProperty $definition.Kind

    $destination = Join-Path $PublishRoot $packageName
    # A version is immutable. Re-running an identical package is idempotent; changing
    # its content under the same version must fail before the existing candidate moves.
    Assert-ArchiveSlot $Stage $destination

    $current = @(Get-ChildItem -LiteralPath $PublishRoot -Directory -Filter 'History*-v*')
    foreach ($package in $current) {
        Archive-PackageDirectory $package.FullName $historyRoot | Out-Null
    }

    # 迁移旧根部平铺候选。history 自身与新式候选目录不属于旧包内容。
    $legacyManifest = Join-Path $PublishRoot $definition.SnapshotManifest
    if (Test-Path -LiteralPath $legacyManifest -PathType Leaf) {
        $legacy = Join-Path $workRoot 'legacy-package'
        New-Item -ItemType Directory -Force -Path $legacy | Out-Null
        foreach ($item in Get-ChildItem -LiteralPath $PublishRoot -Force) {
            if ($item.Name -eq 'history' -or $item.Name -like 'History*-v*' -or $item.Name -like '.incoming-*') { continue }
            Copy-Item -LiteralPath $item.FullName -Destination $legacy -Recurse -Force
        }
        $legacyIdentity = Get-PackageIdentity $legacy
        Assert-ModuleSnapshot $legacy $legacyIdentity.Name $legacyIdentity.Version $definition.SnapshotManifest $definition.IdentityProperty $definition.Kind
        Archive-PackageDirectory $legacy $historyRoot | Out-Null
    }

    foreach ($package in $current) {
        Remove-Item -LiteralPath $package.FullName -Recurse -Force
    }
    foreach ($item in @(Get-ChildItem -LiteralPath $PublishRoot -Force)) {
        if ($item.Name -eq 'history' -or $item.Name -eq (Split-Path -Leaf $incoming)) { continue }
        Remove-Item -LiteralPath $item.FullName -Recurse -Force
    }

    Move-Item -LiteralPath $incoming -Destination $destination
    return $destination
}

function Publish-FlatHostCandidate {
    param(
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$PublishRoot,
        [Parameter(Mandatory = $true)][string]$ExpectedName,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion
    )

    # HistoryVulcan 4.0.0 is a host snapshot, not a module package: the current
    # payload remains directly under z-Publish and only immutable copies go below
    # history/. Keep all validation on a staged copy before touching that root.
    $historyRoot = Join-Path $PublishRoot 'history'
    $incoming = Join-Path $workRoot ".host-incoming-$transactionId"
    $backup = Join-Path $workRoot ".host-previous-$transactionId"
    $current = Join-Path $workRoot ".host-current-$transactionId"
    New-Item -ItemType Directory -Force -Path $historyRoot, $incoming, $backup | Out-Null
    Copy-PackageDirectory $Stage $incoming
    Assert-ModuleSnapshot $incoming $ExpectedName $ExpectedVersion $definition.SnapshotManifest $definition.IdentityProperty $definition.Kind

    # Preserve a flat current payload, if present, before replacing it. This also
    # turns a legacy versioned host candidate into a normal history entry.
    $currentManifest = Join-Path $PublishRoot $definition.SnapshotManifest
    $currentSums = Join-Path $PublishRoot 'SHA256SUMS'
    if ((Test-Path -LiteralPath $currentManifest -PathType Leaf) -and
        (Test-Path -LiteralPath $currentSums -PathType Leaf)) {
        New-Item -ItemType Directory -Force -Path $current | Out-Null
        foreach ($item in Get-ChildItem -LiteralPath $PublishRoot -Force) {
            if ($item.Name -eq 'history' -or $item.Name -like '.incoming-*' -or
                $item.Name -like 'HistoryVulcan-v*') { continue }
            Copy-Item -LiteralPath $item.FullName -Destination $current -Recurse -Force
        }
        $currentIdentity = Get-PackageIdentity $current
        Assert-ModuleSnapshot $current $currentIdentity.Name $currentIdentity.Version $definition.SnapshotManifest $definition.IdentityProperty $definition.Kind
        Archive-PackageDirectory $current $historyRoot | Out-Null
    }

    # A prior failed migration may have left a versioned host directory. Archive
    # it before removing it, so the new root cannot silently select the wrong shape.
    foreach ($legacy in @(Get-ChildItem -LiteralPath $PublishRoot -Directory -Filter 'HistoryVulcan-v*')) {
        $legacyIdentity = Get-PackageIdentity $legacy.FullName
        Assert-ModuleSnapshot $legacy.FullName $legacyIdentity.Name $legacyIdentity.Version $definition.SnapshotManifest $definition.IdentityProperty $definition.Kind
        Archive-PackageDirectory $legacy.FullName $historyRoot | Out-Null
    }

    $movedExisting = [Collections.Generic.List[string]]::new()
    $movedIncoming = [Collections.Generic.List[string]]::new()
    try {
        foreach ($item in @(Get-ChildItem -LiteralPath $PublishRoot -Force |
                Where-Object { $_.Name -ne 'history' })) {
            Move-Item -LiteralPath $item.FullName -Destination $backup -Force
            $movedExisting.Add($item.Name)
        }
        foreach ($item in @(Get-ChildItem -LiteralPath $incoming -Force)) {
            Move-Item -LiteralPath $item.FullName -Destination $PublishRoot -Force
            $movedIncoming.Add($item.Name)
        }
        Assert-ModuleSnapshot $PublishRoot $ExpectedName $ExpectedVersion $definition.SnapshotManifest $definition.IdentityProperty $definition.Kind
    }
    catch {
        foreach ($name in $movedIncoming) {
            $path = Join-Path $PublishRoot $name
            if (Test-Path -LiteralPath $path) {
                Remove-Item -LiteralPath $path -Recurse -Force
            }
        }
        foreach ($name in $movedExisting) {
            $path = Join-Path $backup $name
            if (Test-Path -LiteralPath $path) {
                Move-Item -LiteralPath $path -Destination $PublishRoot -Force
            }
        }
        throw
    }

    return [IO.Path]::GetFullPath($PublishRoot)
}

function Merge-PackageDocumentsIntoSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$SnapshotRoot
    )

    if (-not (Test-Path -LiteralPath $SourceRoot -PathType Container)) {
        throw "Package document source is missing: $SourceRoot"
    }
    $documents = @(Get-ChildItem -LiteralPath $SourceRoot -Filter '*.md' -File | Sort-Object Name)
    if ($documents.Count -eq 0) {
        throw "No consumer Markdown documents found: $SourceRoot"
    }

    $docsRoot = Join-Path $SnapshotRoot 'docs'
    New-Item -ItemType Directory -Force -Path $docsRoot | Out-Null
    foreach ($document in $documents) {
        Copy-Item -LiteralPath $document.FullName -Destination (Join-Path $docsRoot $document.Name) -Force
    }
    Write-SnapshotChecksums $SnapshotRoot
}

function Invoke-ConfiguredModuleValidation {
    param(
        [Parameter(Mandatory = $true)][object[]]$Steps,
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string]$CandidateRoot
    )

    foreach ($step in $Steps) {
        if ([string]::IsNullOrWhiteSpace([string]$step.tool) -or
            @($step.arguments).Count -eq 0) {
            throw 'Every module validation step must declare a tool and arguments.'
        }
        $configurations = if ($step.PSObject.Properties.Name -contains 'configurations') {
            @($step.configurations | ForEach-Object { [string]$_ })
        } else {
            @('')
        }
        foreach ($configuration in $configurations) {
            $moduleOutput = ''
            if ($step.PSObject.Properties.Name -contains 'moduleOutputRoot') {
                $moduleOutput = Join-Path $ProjectRoot "$($step.moduleOutputRoot)\$configuration\net8.0-windows"
            }
            $capturePath = Join-Path $workRoot 'ui-smoke-320x680-dark.png'
            $isolatedOutputBase = if ($step.PSObject.Properties.Name -contains 'isolatedOutputRoot') {
                Join-Path $ProjectRoot ([string]$step.isolatedOutputRoot)
            } else {
                Join-Path $workRoot 'test-output'
            }
            $isolatedOutputRoot = $isolatedOutputBase.TrimEnd([IO.Path]::DirectorySeparatorChar) +
                [IO.Path]::DirectorySeparatorChar
            $arguments = @($step.arguments | ForEach-Object {
                ([string]$_).Replace('{configuration}', $configuration).
                    Replace('{moduleOutput}', $moduleOutput).
                    Replace('{capturePath}', $capturePath).
                    Replace('{isolatedOutputRoot}', $isolatedOutputRoot).
                    Replace('{candidateRoot}', $CandidateRoot)
            })
            if ([string]$step.tool -eq 'dotnet.exe' -and
                -not [string]::IsNullOrWhiteSpace($env:HISTORYVULCAN_PACKAGE_ROOT)) {
                $hostProperty = "-p:HistoryVulcanPackageRoot=$($env:HISTORYVULCAN_PACKAGE_ROOT)"
                $separator = [array]::IndexOf($arguments, '--')
                if ($separator -ge 0) {
                    $arguments = @($arguments[0..($separator - 1)]) + $hostProperty + @($arguments[$separator..($arguments.Length - 1)])
                }
                else {
                    $arguments += $hostProperty
                }
            }
            $description = ([string]$step.description).Replace('{configuration}', $configuration)
            try {
                Invoke-Checked ([string]$step.tool) $arguments $ProjectRoot $description
            }
            finally {
                if ($step.PSObject.Properties.Name -contains 'isolatedOutputRoot' -and
                    (Test-Path -LiteralPath $isolatedOutputBase)) {
                    Remove-Item -LiteralPath $isolatedOutputBase -Recurse -Force
                }
                $legacyOutputRoot = "$isolatedOutputBase$configuration"
                if ($step.PSObject.Properties.Name -contains 'isolatedOutputRoot' -and
                    $legacyOutputRoot -ne $isolatedOutputBase -and
                    (Test-Path -LiteralPath $legacyOutputRoot)) {
                    Remove-Item -LiteralPath $legacyOutputRoot -Recurse -Force
                }
            }
        }
    }
}

$definition = $definitions[$Module]
if ($null -eq $definition) {
    throw "Module '$Module' is not registered. Add a kind=module entry to $registryPath."
}
if (-not [string]::IsNullOrWhiteSpace($SourceWorktree)) {
    if ($Publish) {
        throw '-SourceWorktree 只用于工作区门禁验证，不能与 -Publish 同用：正式促级必须从主树构建。'
    }
    $projectRoot = [IO.Path]::GetFullPath($SourceWorktree)
    if (-not (Test-Path -LiteralPath $projectRoot -PathType Container)) {
        throw "指定的工作树不存在: $projectRoot"
    }
    Write-Host "[$Module] 从工作树构建并验证，写入该树 z-*（不写正式 Clio z）: $projectRoot"
}
else {
    $projectRoot = Assert-ChildPath (Join-Path $projectsRoot $definition.ProjectDirectory) $projectsRoot 'Module project'
}
$workRoot = Join-Path $projectRoot ".publish-stage\module-release-$transactionId"
$versionPropsPath = Join-Path $projectRoot $definition.VersionProps
$sourceManifestPath = Join-Path $projectRoot $definition.SourceManifest
$publishRoot = Assert-ChildPath (Join-Path $projectRoot $definition.CandidateDirectory) $projectRoot 'Publish directory'
$documentSourceRoot = Join-Path $projectRoot $definition.PackageDocuments
$moduleVersion = Read-ModuleVersion $versionPropsPath $definition.VersionProperty
$stagedCandidateRoot = Join-Path $workRoot "$Module-v$moduleVersion"

# 模块的身份写在三处(版本源、源 manifest、快照 manifest)，这里对齐前两处。
# 宿主只有两处：版本源与快照 manifest，没有源 manifest 可对，也就少一处可漂移。
if ($definition.Kind -eq 'module') {
    if (-not (Test-Path -LiteralPath $sourceManifestPath -PathType Leaf)) {
        throw "Source module manifest is missing: $sourceManifestPath"
    }
    $sourceManifest = [IO.File]::ReadAllText($sourceManifestPath, [Text.UTF8Encoding]::new($false)) | ConvertFrom-Json
    if ($sourceManifest.name -ne $Module -or $sourceManifest.version -ne $moduleVersion) {
        throw "Source manifest must match $Module $moduleVersion before release"
    }
}

$sourceStatus = @(& git -C $projectRoot status --porcelain -- ':!z-Publish/**' ":!$($definition.FormalDirectory)/**")
if ($LASTEXITCODE -ne 0) { throw "Unable to read Git status: $projectRoot" }
$sourceDirty = $sourceStatus.Count -gt 0
if ($sourceDirty -and $RequireCleanSource -and -not $AllowDirtySource) {
    throw "Source worktree is dirty. Default is deploy-then-commit; pass -RequireCleanSource only when you need a clean HEAD, or omit it and commit source + z together after publish:`n$($sourceStatus -join [Environment]::NewLine)"
}
if ($sourceDirty) {
    Write-Warning "Publishing $Module from a dirty worktree. Commit source together with z after publish."
}
if ($AllowDirtySource) {
    Write-Warning "-AllowDirtySource is now the default deploy-then-commit path; the switch is kept only for old callers."
}
$sourceCommit = (& git -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommit)) {
    throw "Unable to resolve source commit: $projectRoot"
}

New-Item -ItemType Directory -Force -Path $workRoot | Out-Null
try {
    # 从工作树构建时，工作树在库根之外，构建脚本自己那条"同库根"的相对路径必然指空，
    # 因此把真实的宿主快照根显式传下去。主树构建时不传，脚本沿用原有缺省，行为不变。
    $buildArguments = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $projectRoot $definition.BuildScript),
        '-Configuration', 'Release', '-OutputRoot', $stagedCandidateRoot
    )
    if ($definition.Kind -eq 'module') {
        $hostSnapshot = Get-VulcanHostRoot
        if ([string]::IsNullOrWhiteSpace($hostSnapshot)) {
            throw 'HistoryVulcan host snapshot is missing under 2026-023-HistoryVulcan/z-Publish'
        }
        $env:HISTORYVULCAN_PACKAGE_ROOT = $hostSnapshot
        $buildArguments += @('-HistoryVulcanPackageRoot', $hostSnapshot)
    }
    Invoke-Checked 'powershell.exe' $buildArguments $projectRoot 'Build candidate package'
    Assert-ModuleSnapshot $stagedCandidateRoot $Module $moduleVersion $definition.SnapshotManifest $definition.IdentityProperty $definition.Kind

    if ($definition.Kind -eq 'module') {
        # 强制对齐：模块合同由 Diana 这一份执行，且不经注册表配置——
        # 模块无法跳过、替换或"因为本项目特殊"而改写规则。这是收口的约束点本身。
        Invoke-Checked 'powershell.exe' @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
            (Join-Path $PSScriptRoot 'OneHistory.ModuleContract.ps1'),
            '-ProjectRoot', $projectRoot, '-Instantiation'
        ) $projectRoot 'Run aligned module contract'
        Invoke-ConfiguredModuleValidation @($definition.validation) $projectRoot $stagedCandidateRoot
    } elseif ($definition.Kind -eq 'host') {
        # 宿主合同同样由 Diana 这一份执行；宿主专属门禁（冻结标签、版本源、UI 令牌）在其中。
        Invoke-Checked 'powershell.exe' @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
            (Join-Path $PSScriptRoot 'OneHistory.HostContract.ps1'),
            '-ProjectRoot', $projectRoot, '-Instantiation'
        ) $projectRoot 'Run aligned host contract'
        Invoke-Checked 'dotnet.exe' @(
            'test', (Join-Path $projectRoot $definition.TestProject), '-c', 'Release', '--nologo',
            '--no-restore', '-p:NuGetAudit=false'
        ) $projectRoot 'Run host unit tests'
        foreach ($gate in @($definition.GateScripts)) {
            Invoke-Checked 'powershell.exe' @(
                '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $projectRoot $gate)
            ) $projectRoot "Run host gate $(Split-Path -Leaf $gate)"
        }
    } else {
        throw "Unsupported publish kind: $($definition.Kind)"
    }

    Merge-PackageDocumentsIntoSnapshot $documentSourceRoot $stagedCandidateRoot
    Assert-ModuleSnapshot $stagedCandidateRoot $Module $moduleVersion $definition.SnapshotManifest $definition.IdentityProperty $definition.Kind
    $candidateRoot = if ($definition.Kind -eq 'host') {
        Publish-FlatHostCandidate $stagedCandidateRoot $publishRoot $Module $moduleVersion
    }
    else {
        Publish-VersionedCandidate $stagedCandidateRoot $publishRoot $Module $moduleVersion
    }
    Assert-ModuleSnapshot $candidateRoot $Module $moduleVersion $definition.SnapshotManifest $definition.IdentityProperty $definition.Kind

    if (-not $Publish) {
        if ($SourceWorktree) {
            Write-Host "Worktree versioned candidate is ready: $candidateRoot"
            Write-Host 'diana.release.cycle will strictly replace the AppData runtime package with this candidate.'
            return
        }

        Write-Host "Verified candidate $Module ${moduleVersion}: $candidateRoot"
        Write-Host 'Pass -Publish to run the release cycle; Vulcan installation is performed by the host command path.'
        return
    }
    Write-Host "Published candidate $Module ${moduleVersion}: $candidateRoot"
    Write-Host "Published documents: $(Join-Path $candidateRoot 'docs')"
    if ($definition.Kind -eq 'host' -and $Module -eq 'HistoryVulcan') {
        Repair-VulcanAutostart $candidateRoot
    }
    Write-Host ('Deploy-then-commit: commit source and z-Publish candidate in {0}; runtime reload is vulcan.module.install via diana.release.cycle.' -f $projectRoot)
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($workRoot) -and (Test-Path -LiteralPath $workRoot)) {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}
