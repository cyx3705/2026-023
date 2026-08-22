[CmdletBinding()]
param(
    [switch]$Instantiation
)

# HistoryVulcan (host) contract validation entry point.
#
# The rules themselves are not here. HistoryVulcan is the host, so it consumes the
# shared host entry in b-Code-Eng/pipeline/OneHistory.HostContract.ps1: common
# project rules plus the host-only gates (freeze tag, version source, UI tokens).
#
# This file used to be a forked copy of the shared checker. The fork was legitimate
# (it carried real host rules) but it could no longer be kept aligned with the other
# copies. Those host rules now live in the host entry and are driven by the
# contract.host section of project.manifest.json; the freeze tag expectation is held
# by the publish registry, deliberately outside the file it guards.
#
# Keep this file ASCII only: it has no BOM, so Windows PowerShell would decode
# non-ASCII bytes with the system code page and fail to parse.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestPath = Join-Path $repoRoot 'project.manifest.json'
$manifestBytes = [IO.File]::ReadAllBytes($manifestPath)
if ($manifestBytes.Length -ge 3 -and
    $manifestBytes[0] -eq 0xEF -and $manifestBytes[1] -eq 0xBB -and $manifestBytes[2] -eq 0xBF) {
    throw 'project.manifest.json must be UTF-8 without a BOM so the pipeline can read it as UTF-8.'
}

$hostEntry = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot 'pipeline\OneHistory.HostContract.ps1'))

if (-not (Test-Path -LiteralPath $hostEntry -PathType Leaf)) {
    Write-Host "Shared host contract is missing: $hostEntry" -ForegroundColor Red
    exit 2
}

& $hostEntry -ProjectRoot $repoRoot -Instantiation:$Instantiation
exit $LASTEXITCODE
