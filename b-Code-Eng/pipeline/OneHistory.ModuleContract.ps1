[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProjectRoot,
    [switch]$Instantiation
)

# OneHistory 模块侧项目合同校验（强制对齐入口）。
#
# 体系内所有模块（HistoryJanus / HistoryMercury / HistoryMinerva / HistoryDiana 自身）
# 共用这一份，规则完全一致。本入口刻意不提供脚本级扩展钩子：
# 模块的合理差异（文档区布局、归档排除、必需文件清单）一律经 project.manifest.json
# 的 contract 节声明；无法用清单表达的差异，说明它要么该进内核，要么该被消除。
#
# 这条约束是这次收口的目的本身——旧体系里五份副本正是从「这个项目有点特殊」开始漂移的。
# 宿主的专属门禁不走这里，见 OneHistory.HostContract.ps1。

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$corePath = Join-Path $PSScriptRoot 'OneHistory.ContractCore.ps1'
if (-not (Test-Path -LiteralPath $corePath -PathType Leaf)) {
    Write-Host "[module] Contract core is missing: $corePath" -ForegroundColor Red
    exit 2
}
. $corePath

Initialize-ContractContext -RepoRoot $ProjectRoot
Invoke-CommonContractChecks -Instantiation:$Instantiation
Complete-ContractValidation -Scope 'module' -Instantiation:$Instantiation
