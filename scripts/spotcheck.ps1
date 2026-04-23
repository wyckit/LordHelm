# spotcheck.ps1 — the ≤2-minute test gate run after every meaningful change.
# Skips slow suites (E2E WebApplicationFactory spin-ups, full solution test
# sweeps, anything touching real engram MCP transport). Add new fast unit
# test projects here as they land; move anything exceeding the 2-minute
# budget into full-test.ps1.
#
# Usage:  pwsh scripts/spotcheck.ps1
# Budget: 120 seconds hard ceiling (reports over-budget; does not kill).

$ErrorActionPreference = "Stop"
$start = Get-Date

$projects = @(
    "tests/LordHelm.Consensus.Tests/LordHelm.Consensus.Tests.csproj",
    "tests/LordHelm.Skills.Tests/LordHelm.Skills.Tests.csproj",
    "tests/LordHelm.Execution.Tests/LordHelm.Execution.Tests.csproj",
    "tests/LordHelm.Core.Tests/LordHelm.Core.Tests.csproj",
    "tests/LordHelm.Web.UnitTests/LordHelm.Web.UnitTests.csproj"
)

$totalFailed = 0
foreach ($p in $projects) {
    if (-not (Test-Path $p)) { continue }
    $run = Get-Date
    Write-Host "--- $p"
    dotnet test $p --nologo -v q
    if ($LASTEXITCODE -ne 0) { $totalFailed += 1 }
    $el = (Get-Date) - $run
    Write-Host "    elapsed $($el.TotalSeconds.ToString('0.0'))s"
}

$elapsed = (Get-Date) - $start
Write-Host ""
Write-Host "spotcheck total: $($elapsed.TotalSeconds.ToString('0.0'))s"
if ($elapsed.TotalSeconds -gt 120) {
    Write-Host "WARNING: spotcheck exceeded 120s budget — consider moving a suite to full-test.ps1" -ForegroundColor Yellow
}
if ($totalFailed -gt 0) {
    Write-Host "FAILED ($totalFailed project(s) with failures)" -ForegroundColor Red
    exit 1
}
Write-Host "OK" -ForegroundColor Green
