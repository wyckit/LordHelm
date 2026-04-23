# full-test.ps1 — the full test suite including E2E (WebApplicationFactory)
# and anything else that would blow the 2-minute spotcheck budget. Designed
# for overnight runs / CI / before cutting a release.
#
# Usage:  pwsh scripts/full-test.ps1
# Budget: not enforced — runs to completion.

$ErrorActionPreference = "Stop"
$start = Get-Date

Write-Host "Running FULL test suite (solution-level) ..."
dotnet test C:/Software/LordHelm/LordHelm.slnx --nologo -v minimal
$code = $LASTEXITCODE

$elapsed = (Get-Date) - $start
Write-Host ""
Write-Host "full-test total: $($elapsed.TotalMinutes.ToString('0.0'))m"
exit $code
