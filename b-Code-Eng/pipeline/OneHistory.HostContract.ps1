[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProjectRoot,
    [switch]$Instantiation
)

# OneHistory 宿主侧项目合同校验。
#
# 宿主（当前为 HistoryVulcan）在通用规则之上，还要守住模块不需要、也不该有的门禁：
# 身份自证、V3 冻结标签、版本源与清单同步、UI 设计令牌合同。这些规则原先散在
# Vulcan 自己的脚本副本里，与通用规则纠缠在一起，导致该副本无法与其他四份对齐。
# 现在通用部分回到内核，宿主规则留在本文件，两边各自可独立演进。
#
# 具体取值全部来自 project.manifest.json 的 contract.host 节，本脚本只表达
# 「宿主类项目需要哪几类门禁」，不硬编码某一个宿主的字符串。

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$corePath = Join-Path $PSScriptRoot 'OneHistory.ContractCore.ps1'
if (-not (Test-Path -LiteralPath $corePath -PathType Leaf)) {
    Write-Host "[host] Contract core is missing: $corePath" -ForegroundColor Red
    exit 2
}
. $corePath

Initialize-ContractContext -RepoRoot $ProjectRoot
Invoke-CommonContractChecks -Instantiation:$Instantiation

$host_ = Get-ContractSetting 'host' $null
if ($null -eq $host_) {
    Add-ContractError 'Host projects must declare a contract.host section in project.manifest.json.'
    Complete-ContractValidation -Scope 'host' -Instantiation:$Instantiation
}

$project = Get-NodeValue $script:ContractManifest 'project'

# ---------------------------------------------------------------- 身份自证

$identity = Get-NodeValue $host_ 'identity'
if ($null -ne $identity -and $null -ne $project) {
    $expectedId = [string](Get-NodeValue $identity 'id')
    $expectedName = [string](Get-NodeValue $identity 'name')
    $actualId = [string](Get-NodeValue $project 'id')
    $actualName = [string](Get-NodeValue $project 'name')

    if ($actualId -ne $expectedId -or $actualName -ne $expectedName) {
        Add-ContractError "Project identity must be $expectedId/$expectedName (found $actualId/$actualName)."
    }
}

# ---------------------------------------------------------------- 冻结标签

# 冻结标签一经发布不得移动；版本线可以前进，但冻结点必须留在原处。
#
# 期望值刻意存放在 Diana 的发布注册表，而不是宿主自己的 project.manifest.json：
# 若两者同处一文件，移动冻结点的人会在同一次编辑里把期望值一并改掉，闸口等于没有。
# 注册表在另一个仓库，改它是一次显式的、可被单独审阅的动作。
if ($null -ne $project) {
    $projectName = [string](Get-NodeValue $project 'name')
    $registryPath = Join-Path $PSScriptRoot 'module-publish.manifest.json'

    if (-not (Test-Path -LiteralPath $registryPath -PathType Leaf)) {
        Add-ContractError "Publish registry is missing, freeze tag cannot be verified: $registryPath"
    }
    else {
        try {
            $registry = Get-Content -LiteralPath $registryPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $hostEntry = @(Get-NodeValue $registry 'hosts') |
                Where-Object { $null -ne $_ -and [string](Get-NodeValue $_ 'name') -eq $projectName } |
                Select-Object -First 1

            if ($null -eq $hostEntry) {
                Add-ContractError "Publish registry declares no host entry for $projectName."
            }
            else {
                $expectedFreezeTag = [string](Get-NodeValue $hostEntry 'freezeTag')
                $actualFreezeTag = [string](Get-NodeValue $project 'freezeTag')
                if ($actualFreezeTag -ne $expectedFreezeTag) {
                    Add-ContractError "Freeze tag must remain $expectedFreezeTag (found $actualFreezeTag)."
                }
            }
        }
        catch {
            Add-ContractError "Publish registry could not be read: $($_.Exception.Message)"
        }
    }
}

# ---------------------------------------------------------------- 版本源同步

# 版本只允许有一个真值（构建用的 .props）。清单里的 project.version 必须与之一致，
# 否则发布快照与源码会各说各话。
$versionPropsRelative = [string](Get-NodeValue $host_ 'versionProps')
if (-not [string]::IsNullOrWhiteSpace($versionPropsRelative) -and $null -ne $project) {
    $versionPropsPath = Resolve-ContractPath -RelativePath $versionPropsRelative -Context 'contract.host.versionProps'
    if ($null -ne $versionPropsPath) {
        if (-not (Test-Path -LiteralPath $versionPropsPath -PathType Leaf)) {
            Add-ContractError "Version source is missing: $versionPropsRelative"
        }
        else {
            $versionProperty = [string](Get-NodeValue $host_ 'versionProperty')
            if ([string]::IsNullOrWhiteSpace($versionProperty)) {
                Add-ContractError 'contract.host.versionProperty is required when versionProps is declared.'
            }
            else {
                try {
                    $propsXml = [xml](Get-Content -LiteralPath $versionPropsPath -Raw -Encoding UTF8)
                    $sourceVersion = [string]$propsXml.Project.PropertyGroup.$versionProperty
                    $manifestVersion = [string](Get-NodeValue $project 'version')
                    if ($sourceVersion -ne $manifestVersion) {
                        Add-ContractError (
                            "project.version ($manifestVersion) must match " +
                            "$versionPropsRelative/$versionProperty ($sourceVersion).")
                    }
                }
                catch {
                    Add-ContractError "Version source could not be read: $versionPropsRelative -> $($_.Exception.Message)"
                }
            }
        }
    }
}

# ---------------------------------------------------------------- UI 设计令牌合同

# 宿主的设计令牌是所有模块前端的视觉真值。令牌字典里新增一个键却没写进风格文档，
# 模块方就无从知道它存在，只能自己造色值——这正是令牌体系失效的起点。
# 因此：令牌字典中的每个键，都必须在风格文档里出现。
$uiStyle = Get-NodeValue $host_ 'uiStyle'
if ($null -ne $uiStyle) {
    $styleDocumentRelative = [string](Get-NodeValue $uiStyle 'document')
    $themeDirectoryRelative = [string](Get-NodeValue $uiStyle 'themeDirectory')
    $tokenFiles = @(Get-NodeValue $uiStyle 'tokenFiles')

    $styleDocumentPath = $null
    if (-not [string]::IsNullOrWhiteSpace($styleDocumentRelative)) {
        $styleDocumentPath = Resolve-ContractPath -RelativePath $styleDocumentRelative -Context 'contract.host.uiStyle.document'
    }

    $themeDirectoryPath = $null
    if (-not [string]::IsNullOrWhiteSpace($themeDirectoryRelative)) {
        $themeDirectoryPath = Resolve-ContractPath -RelativePath $themeDirectoryRelative -Context 'contract.host.uiStyle.themeDirectory'
    }

    if ($null -ne $styleDocumentPath -and $null -ne $themeDirectoryPath -and $tokenFiles.Count -ne 0) {
        if (-not (Test-Path -LiteralPath $styleDocumentPath -PathType Leaf)) {
            Add-ContractError "UI style contract document is missing: $styleDocumentRelative"
        }
        else {
            $styleText = Get-Content -LiteralPath $styleDocumentPath -Raw -Encoding UTF8
            if ([string]::IsNullOrEmpty($styleText)) { $styleText = '' }

            $tokenPattern = [string](Get-NodeValue $uiStyle 'tokenPattern')
            if ([string]::IsNullOrWhiteSpace($tokenPattern)) {
                $tokenPattern = 'x:Key="(?<key>Shell\.(?:Brush|Radius|Font|Space|Size)\.[^"]+)"'
            }

            $tokenKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($tokenFileName in $tokenFiles) {
                $tokenPath = Join-Path $themeDirectoryPath ([string]$tokenFileName)
                if (-not (Test-Path -LiteralPath $tokenPath -PathType Leaf)) {
                    Add-ContractError "Theme token file is missing: $themeDirectoryRelative/$tokenFileName"
                    continue
                }
                $tokenText = Get-Content -LiteralPath $tokenPath -Raw -Encoding UTF8
                if ([string]::IsNullOrEmpty($tokenText)) { continue }
                foreach ($tokenMatch in [regex]::Matches($tokenText, $tokenPattern)) {
                    $null = $tokenKeys.Add($tokenMatch.Groups['key'].Value)
                }
            }

            foreach ($tokenKey in $tokenKeys) {
                if ($styleText.IndexOf($tokenKey, [StringComparison]::Ordinal) -lt 0) {
                    Add-ContractError "UI style contract is missing theme token: $tokenKey"
                }
            }
        }
    }
}

Complete-ContractValidation -Scope 'host' -Instantiation:$Instantiation
