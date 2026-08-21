# OneHistory 项目合同校验内核（体系唯一真值，由 HistoryDiana 拥有）。
#
# 本文件是点源库，不可直接执行。两个入口消费它：
#   - OneHistory.ModuleContract.ps1：模块侧，强制对齐，不提供任何项目钩子；
#   - OneHistory.HostContract.ps1  ：宿主侧，在通用规则之上追加宿主专属门禁。
#
# 背景：校验脚本此前以逐字副本散落在模板、Mercury、Minerva、Diana、Vulcan 五处。
# Mercury/Minerva 与模板逐字相同，Diana 多出两处修复，Vulcan 是含宿主规则的合法分叉。
# 收口原则：通用规则只此一份；模块差异一律经 project.manifest.json 的 contract 节
# 声明，模块侧不留脚本级扩展点，避免副本漂移以「项目特殊需求」的名义重新长出来。

Set-StrictMode -Version Latest

function Initialize-ContractContext {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot
    )

    $script:ContractErrors = [Collections.Generic.List[string]]::new()
    $script:ContractRepoRoot = [IO.Path]::GetFullPath($RepoRoot)

    if (-not (Test-Path -LiteralPath $script:ContractRepoRoot -PathType Container)) {
        throw "Project root does not exist: $script:ContractRepoRoot"
    }

    $manifestPath = Join-Path $script:ContractRepoRoot 'project.manifest.json'
    $script:ContractManifest = $null

    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        Add-ContractError 'Missing project.manifest.json.'
        return
    }

    try {
        $script:ContractManifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        Add-ContractError "project.manifest.json is not valid JSON: $($_.Exception.Message)"
    }
}

function Add-ContractError {
    param([string]$Message)

    $script:ContractErrors.Add($Message)
}

# StrictMode 下访问不存在的属性会抛异常，取值统一走本函数。
function Get-NodeValue {
    param(
        [object]$Node,
        [string]$Name
    )

    if ($null -eq $Node) { return $null }
    $property = $Node.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Get-ContractSetting {
    param(
        [string]$Name,
        [object]$Default
    )

    $contract = Get-NodeValue $script:ContractManifest 'contract'
    $value = Get-NodeValue $contract $Name
    if ($null -eq $value) { return $Default }
    return $value
}

function Test-RequiredProperty {
    param(
        [object]$Object,
        [string]$Name,
        [string]$Context
    )

    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) {
        Add-ContractError "$Context is missing property '$Name'."
        return $false
    }

    return $true
}

function Resolve-ContractPath {
    param(
        [string]$RelativePath,
        [string]$Context
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        Add-ContractError "$Context must be a non-empty repository-relative path: $RelativePath"
        return $null
    }

    $fullPath = [IO.Path]::GetFullPath((Join-Path $script:ContractRepoRoot $RelativePath))
    $rootPrefix = $script:ContractRepoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($fullPath -ne $script:ContractRepoRoot -and
        -not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Add-ContractError "$Context points outside the repository: $RelativePath"
        return $null
    }

    return $fullPath
}

# ---------------------------------------------------------------- 必需文件

function Test-ContractRequiredFiles {
    $requiredFiles = @(Get-ContractSetting 'requiredFiles' @(
            'AGENTS.md'
            'README.md'
            '.ignore'
            'project.manifest.json'
        ))

    foreach ($relativePath in $requiredFiles) {
        $fullPath = Resolve-ContractPath -RelativePath ([string]$relativePath) -Context 'Required file'
        if ($null -ne $fullPath -and -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            Add-ContractError "Missing required file: $relativePath"
        }
    }
}

# ---------------------------------------------------------------- 清单结构

function Test-ContractManifestShape {
    param([switch]$Instantiation)

    $manifest = $script:ContractManifest
    if ($null -eq $manifest) { return }

    $schemaVersion = Get-NodeValue $manifest 'schemaVersion'
    if ((Test-RequiredProperty $manifest 'schemaVersion' 'manifest') -and $schemaVersion -ne 1) {
        Add-ContractError "Unsupported schemaVersion: $schemaVersion"
    }

    foreach ($name in @('template', 'project', 'paths', 'documents', 'commands', 'contextExclusions')) {
        $null = Test-RequiredProperty $manifest $name 'manifest'
    }

    $template = Get-NodeValue $manifest 'template'
    if ($null -ne $template -and (Test-RequiredProperty $template 'isTemplate' 'template')) {
        $isTemplateValue = Get-NodeValue $template 'isTemplate'
        if ($isTemplateValue -isnot [bool]) {
            Add-ContractError 'template.isTemplate must be a Boolean.'
        }
        elseif ($Instantiation -and $isTemplateValue) {
            Add-ContractError 'Instantiation validation requires template.isTemplate to be false.'
        }
    }

    $project = Get-NodeValue $manifest 'project'
    if ($null -ne $project) {
        $requiredProjectFields = @(Get-ContractSetting 'requiredProjectFields' @(
                'id', 'name', 'title', 'kind', 'status'
            ))
        foreach ($name in $requiredProjectFields) {
            if (Test-RequiredProperty $project ([string]$name) 'project') {
                $value = @(Get-NodeValue $project ([string]$name))
                if ($value.Count -eq 0 -or [string]::IsNullOrWhiteSpace([string]$value[0])) {
                    Add-ContractError "project.$name cannot be empty."
                }
            }
        }
    }

    $commands = Get-NodeValue $manifest 'commands'
    if ($null -ne $commands) {
        foreach ($name in @('setup', 'build', 'test', 'verify', 'run', 'package')) {
            if (Test-RequiredProperty $commands $name 'commands') {
                $value = Get-NodeValue $commands $name
                if ($null -ne $value -and ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value))) {
                    Add-ContractError "commands.$name must be a non-empty string or null."
                }
            }
        }
    }
}

# ---------------------------------------------------------------- 目录与文档

function Test-ContractPaths {
    $paths = Get-NodeValue $script:ContractManifest 'paths'
    if ($null -eq $paths) { return }

    foreach ($name in @('activeRoots', 'sourceRoots', 'generatedRoots', 'archiveRoots',
            'allowedRootDirectoryPrefixes')) {
        $null = Test-RequiredProperty $paths $name 'paths'
    }

    $declared = @(Get-NodeValue $paths 'activeRoots') + @(Get-NodeValue $paths 'sourceRoots')
    foreach ($relativePath in $declared) {
        if ($null -eq $relativePath) { continue }
        $fullPath = Resolve-ContractPath -RelativePath ([string]$relativePath) -Context 'Declared directory'
        if ($null -ne $fullPath -and -not (Test-Path -LiteralPath $fullPath -PathType Container)) {
            Add-ContractError "Declared directory does not exist: $relativePath"
        }
    }

    $allowedPrefixes = @(Get-NodeValue $paths 'allowedRootDirectoryPrefixes')
    foreach ($expectedPrefix in @('a-', 'b-', 'z-')) {
        if ($allowedPrefixes -notcontains $expectedPrefix) {
            Add-ContractError "paths.allowedRootDirectoryPrefixes must include '$expectedPrefix'."
        }
    }
    foreach ($prefix in $allowedPrefixes) {
        if ([string]::IsNullOrWhiteSpace([string]$prefix)) {
            Add-ContractError 'Root directory prefixes cannot be empty.'
        }
    }

    # 宿主在根目录保留 artifacts 一类非 a-/b-/z- 目录，经白名单声明，
    # 而不是各自改脚本——这是原先 Vulcan 副本分叉的原因之一。
    $allowedNames = @(Get-NodeValue $paths 'allowedRootDirectoryNames')

    foreach ($directory in Get-ChildItem -LiteralPath $script:ContractRepoRoot -Directory -Force) {
        if ($directory.Name.StartsWith('.')) { continue }

        $allowed = @($allowedPrefixes | Where-Object {
                -not [string]::IsNullOrWhiteSpace([string]$_) -and
                $directory.Name.StartsWith([string]$_, [StringComparison]::OrdinalIgnoreCase)
            }).Count -ne 0

        if (-not $allowed) {
            $allowed = @($allowedNames | Where-Object {
                    $null -ne $_ -and $directory.Name.Equals([string]$_, [StringComparison]::OrdinalIgnoreCase)
                }).Count -ne 0
        }

        if (-not $allowed) {
            Add-ContractError "Root directory is not allowed by the naming contract: $($directory.Name)"
        }
    }
}

function Test-ContractDocuments {
    $documents = Get-NodeValue $script:ContractManifest 'documents'
    if ($null -eq $documents) { return }

    foreach ($property in $documents.PSObject.Properties) {
        $relativePath = [string]$property.Value
        $fullPath = Resolve-ContractPath -RelativePath $relativePath -Context "documents.$($property.Name)"
        if ($null -ne $fullPath -and -not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            Add-ContractError "Declared document does not exist: $relativePath"
        }
    }
}

# ---------------------------------------------------------------- Markdown 链接

function Get-ContractMarkdownFiles {
    # declared：只检查清单声明的文档与根 README（宿主原有行为，规模可控）。
    # recursive：全仓扫描并排除归档（模块侧默认）。归档默认按后缀 *\history\* 匹配，
    # 因此 b-Office/history 与 Diana 拆分出的 b-Office-Diana/history 都能命中。
    $mode = [string](Get-ContractSetting 'linkCheckMode' 'recursive')

    if ($mode -eq 'declared') {
        $files = [Collections.Generic.List[string]]::new()
        $files.Add((Join-Path $script:ContractRepoRoot 'README.md'))
        $documents = Get-NodeValue $script:ContractManifest 'documents'
        if ($null -ne $documents) {
            foreach ($property in $documents.PSObject.Properties) {
                $relativePath = [string]$property.Value
                if ($relativePath.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase)) {
                    $files.Add((Join-Path $script:ContractRepoRoot $relativePath))
                }
            }
        }
        foreach ($extra in @(Get-ContractSetting 'linkCheckAdditionalFiles' @())) {
            $files.Add((Join-Path $script:ContractRepoRoot ([string]$extra)))
        }
        return @($files | Select-Object -Unique | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    }

    $exclusions = @(Get-ContractSetting 'linkCheckExclusions' @('*\history\*', '*-References\*'))
    return @(
        Get-ChildItem -LiteralPath $script:ContractRepoRoot -Filter '*.md' -File -Recurse |
            Where-Object {
                $candidate = $_.FullName
                @($exclusions | Where-Object { $candidate -like [string]$_ }).Count -eq 0
            } | ForEach-Object FullName
    )
}

function Test-ContractMarkdownLinks {
    $linkPattern = [regex]'\[[^\]]+\]\((?<target>[^)]+)\)'

    foreach ($filePath in Get-ContractMarkdownFiles) {
        # 空文件 -Raw 返回 $null，Matches($null) 会抛 ArgumentNullException。
        $content = Get-Content -LiteralPath $filePath -Raw -Encoding UTF8
        if ([string]::IsNullOrEmpty($content)) { continue }

        foreach ($match in $linkPattern.Matches($content)) {
            $target = $match.Groups['target'].Value.Trim().Trim('<', '>')
            if ($target.StartsWith('#') -or $target -match '^[a-zA-Z][a-zA-Z0-9+.-]*:') { continue }

            $pathPart = ($target -split '#', 2)[0]
            if ([string]::IsNullOrWhiteSpace($pathPart)) { continue }

            try {
                $pathPart = [Uri]::UnescapeDataString($pathPart)
            }
            catch {
                Add-ContractError "Markdown link cannot be decoded: $filePath -> $target"
                continue
            }

            $linkedPath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $filePath) $pathPart))
            if (-not (Test-Path -LiteralPath $linkedPath)) {
                $relativeFile = $filePath.Substring($script:ContractRepoRoot.Length + 1)
                Add-ContractError "Broken Markdown link: $relativeFile -> $target"
            }
        }
    }
}

# ---------------------------------------------------------------- 实例化严格模式

function Test-ContractInstantiation {
    param([switch]$Instantiation)

    $manifest = $script:ContractManifest

    $isTemplate = $true
    $template = Get-NodeValue $manifest 'template'
    $isTemplateValue = Get-NodeValue $template 'isTemplate'
    if ($isTemplateValue -is [bool]) { $isTemplate = $isTemplateValue }

    if (-not ($Instantiation -or -not $isTemplate)) { return }

    $project = Get-NodeValue $manifest 'project'
    if ($null -ne $project) {
        if ((Get-NodeValue $project 'id') -eq '0000-001') {
            Add-ContractError 'Instantiated projects must replace the template project.id.'
        }
        if ((Get-NodeValue $project 'name') -eq 'AIReady') {
            Add-ContractError 'Instantiated projects must replace the template project.name.'
        }
        if ((Get-NodeValue $project 'status') -eq 'template') {
            Add-ContractError 'Instantiated projects must replace the template project.status.'
        }
    }

    $rootReadmePath = Join-Path $script:ContractRepoRoot 'README.md'
    if (Test-Path -LiteralPath $rootReadmePath -PathType Leaf) {
        $rootReadme = Get-Content -LiteralPath $rootReadmePath -Raw -Encoding UTF8
        if ($rootReadme -match '(?m)^# OneHistory AI-Ready') {
            Add-ContractError 'Instantiated projects must replace the template root README.'
        }
    }

    # 现行文档按「路径含 current/」识别，而不是硬编码 b-Office/current。
    # 旧副本写死后者，导致 Diana 的 b-Office-Diana/current 一份都没被占位符检查覆盖。
    $currentDocuments = @()
    $documents = Get-NodeValue $manifest 'documents'
    if ($null -ne $documents) {
        $currentDocuments = @(
            $documents.PSObject.Properties |
                ForEach-Object { [string]$_.Value } |
                Where-Object { $_ -match '(^|/)current/' }
        )
    }

    $placeholderPattern = [regex]'\{\{[^{}\r\n]+\}\}'
    $strictFiles = @('README.md', 'project.manifest.json') + $currentDocuments

    foreach ($relativePath in @($strictFiles | Select-Object -Unique)) {
        $fullPath = Join-Path $script:ContractRepoRoot $relativePath
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            $content = Get-Content -LiteralPath $fullPath -Raw -Encoding UTF8
            if (-not [string]::IsNullOrEmpty($content) -and $placeholderPattern.IsMatch($content)) {
                Add-ContractError "Instantiation placeholder remains in: $relativePath"
            }
        }
    }
}

# ---------------------------------------------------------------- 组合与收尾

function Invoke-CommonContractChecks {
    param([switch]$Instantiation)

    Test-ContractRequiredFiles
    Test-ContractManifestShape -Instantiation:$Instantiation
    Test-ContractPaths
    Test-ContractDocuments
    Test-ContractMarkdownLinks
    Test-ContractInstantiation -Instantiation:$Instantiation
}

function Complete-ContractValidation {
    param(
        [Parameter(Mandatory = $true)][string]$Scope,
        [switch]$Instantiation
    )

    if ($script:ContractErrors.Count -ne 0) {
        Write-Host "[$Scope] Project contract validation failed with $($script:ContractErrors.Count) error(s):" `
            -ForegroundColor Red
        foreach ($contractError in $script:ContractErrors) {
            Write-Host "  - $contractError" -ForegroundColor Red
        }
        exit 1
    }

    $mode = if ($Instantiation) { 'instantiation' } else { 'project' }
    Write-Host "[$Scope] Project contract validation passed ($mode mode): $script:ContractRepoRoot" `
        -ForegroundColor Green
    exit 0
}
