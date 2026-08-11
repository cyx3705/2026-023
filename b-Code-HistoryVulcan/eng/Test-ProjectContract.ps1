[CmdletBinding()]
param(
    [switch]$Instantiation
)

# HistoryVulcan (host) contract validation entry point.
#
# The rules themselves are not here. HistoryVulcan is the host, so it consumes the
# host entry owned by HistoryDiana (b-Code/OneHistory.HostContract.ps1): common
# project rules plus the host-only gates (freeze tag, version source, UI tokens).
#
# This file used to be a forked copy of the shared checker. The fork was legitimate
# (it carried real host rules) but it could no longer be kept aligned with the other
# copies. Those host rules now live in the host entry and are driven by the
# contract.host section of project.manifest.json; the freeze tag expectation is held
# by HistoryDiana's publish registry, deliberately outside the file it guards.
#
# Keep this file ASCII only: it has no BOM, so Windows PowerShell would decode
# non-ASCII bytes with the system code page and fail to parse.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$dianaEntry = [IO.Path]::GetFullPath(
    (Join-Path $repoRoot '..\2026-019-HistoryDiana\b-Code\OneHistory.HostContract.ps1'))

if (-not (Test-Path -LiteralPath $dianaEntry -PathType Leaf)) {
    Write-Host "Shared host contract is missing: $dianaEntry" -ForegroundColor Red
    Write-Host 'Expected HistoryDiana to be checked out alongside this project.' -ForegroundColor Red
    exit 2
}

& $dianaEntry -ProjectRoot $repoRoot -Instantiation:$Instantiation
exit $LASTEXITCODE
