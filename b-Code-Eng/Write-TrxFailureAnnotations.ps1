#requires -Version 5.1

param(
    [Parameter(Mandatory = $true)]
    [string]$ResultsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$trxFiles = @(
    Get-ChildItem -LiteralPath $ResultsDirectory -Filter '*.trx' -File -ErrorAction SilentlyContinue
)
if ($trxFiles.Count -eq 0) {
    Write-Warning "No TRX files found in $ResultsDirectory"
    exit 0
}

$failures = @(
    foreach ($trxFile in $trxFiles) {
        [xml]$trx = Get-Content -LiteralPath $trxFile.FullName -Raw -Encoding UTF8
        foreach ($result in @($trx.TestRun.Results.UnitTestResult)) {
            if ($result.outcome -ne 'Failed') {
                continue
            }

            $message = [string]$result.Output.ErrorInfo.Message
            $firstLine = @($message -split '\r?\n')[0].Trim()
            [pscustomobject]@{
                File = $trxFile.Name
                Test = [string]$result.testName
                Message = $firstLine
            }
        }
    }
)

if ($failures.Count -eq 0) {
    Write-Host 'No failed tests found in the available TRX files.'
    exit 0
}

foreach ($failure in $failures) {
    $annotation = "$($failure.Test) [$($failure.File)]"
    if (-not [string]::IsNullOrWhiteSpace($failure.Message)) {
        $annotation += ": $($failure.Message)"
    }
    $annotation = $annotation.Replace('%', '%25').Replace("`r", '%0D').Replace("`n", '%0A')
    Write-Host "::error title=HistoryVulcan test failure::$annotation"
}

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding UTF8 -Value "### Failed HistoryVulcan tests`n"
    foreach ($failure in $failures) {
        $summaryLine = "- ``$($failure.Test)`` ($($failure.File))"
        if (-not [string]::IsNullOrWhiteSpace($failure.Message)) {
            $summaryLine += ": $($failure.Message)"
        }
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Encoding UTF8 -Value $summaryLine
    }
}

Write-Host "Reported $($failures.Count) failed test(s)."
