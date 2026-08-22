[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$activeRoots = @('b-Code-HistoryVulcan')
$excluded = '\\(bin|obj|artifacts|history|b-Publish|z-Publish)\\'
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

# ---- 候选快照形状 --------------------------------------------------------
# 当前候选必须是扁平的 z-Publish/host + docs + manifest.json（技术合同 REQ-PKG-001、
# DEC-041）。全部模块仓的工程按 $(HistoryVulcanPackageRoot)\host 解析宿主运行库，
# 相对路径落在仓库根之外，因此这个形状是跨仓构建的硬契约，不只是本仓的整洁问题。
#
# 2026-08-18 曾被破坏一次：一次发布把 36 个文件从 z-Publish/host/* 改名到
# z-Publish/HistoryVulcan-v3.12.1/host/*（提交 2559bf1，套用了模块的快照形状）。
# 契约没改，磁盘改了，于是 Janus / Aurora 等模块仓从干净状态一律构建失败，而本仓
# 自身的构建与测试全绿——没有任何门禁会响。本检查就是补上那声警报。
$candidateRoot = Join-Path $root 'z-Publish'
if (Test-Path -LiteralPath $candidateRoot) {
    $hostExecutable = Join-Path $candidateRoot 'host\HistoryVulcan.exe'
    if (-not (Test-Path -LiteralPath $hostExecutable)) {
        $violations.Add(
            'Candidate snapshot is not flat: z-Publish\host\HistoryVulcan.exe is missing. ' +
            'Module repositories resolve host assemblies through $(HistoryVulcanPackageRoot)\host ' +
            'and will fail to build. Regenerate with b-Code-Eng\Build-HistoryVulcanPackage.ps1.')
    }
    foreach ($stray in @(Get-ChildItem -LiteralPath $candidateRoot -Directory -Force |
            Where-Object { $_.Name -like 'HistoryVulcan-v*' })) {
        $violations.Add(
            "Versioned directory at the candidate root: $($stray.Name). " +
            'The current candidate lives flat at z-Publish\; versioned packages belong in z-Publish\history\.')
    }
}

# Force array semantics so the single-hotspot case behaves the same in Windows
# PowerShell 5.1 and pwsh. Without this, a scalar PSCustomObject has no Count
# property and one violation incorrectly passes the gate.
$hotspots = @(Get-ChildItem -LiteralPath (Join-Path $root 'b-Code-HistoryVulcan') -Recurse -File |
    Where-Object { $_.Extension -in '.cs', '.xaml' -and $_.FullName -notmatch $excluded } |
    ForEach-Object { [pscustomobject]@{ Path = $_.FullName; Lines = (Get-Content -LiteralPath $_.FullName).Count } } |
    Where-Object Lines -gt 1000 |
    Sort-Object Lines -Descending)

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
