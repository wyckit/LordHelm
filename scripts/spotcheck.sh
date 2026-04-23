#!/usr/bin/env bash
# spotcheck.sh — ≤2-minute test gate for after every meaningful change.
# Mirrors spotcheck.ps1 on bash/zsh. Skips E2E and any suite that hits
# real MCP transport or spins up Kestrel. Over-budget → warn, not fail.
set -uo pipefail

start=$(date +%s)
projects=(
    "tests/LordHelm.Consensus.Tests/LordHelm.Consensus.Tests.csproj"
    "tests/LordHelm.Skills.Tests/LordHelm.Skills.Tests.csproj"
    "tests/LordHelm.Execution.Tests/LordHelm.Execution.Tests.csproj"
    "tests/LordHelm.Core.Tests/LordHelm.Core.Tests.csproj"
    "tests/LordHelm.Web.UnitTests/LordHelm.Web.UnitTests.csproj"
)

total_failed=0
for p in "${projects[@]}"; do
    [ -f "$p" ] || continue
    run_start=$(date +%s)
    echo "--- $p"
    dotnet test "$p" --nologo -v q
    code=$?
    [ $code -ne 0 ] && total_failed=$((total_failed + 1))
    echo "    elapsed $(( $(date +%s) - run_start ))s"
done

elapsed=$(( $(date +%s) - start ))
echo
echo "spotcheck total: ${elapsed}s"
[ $elapsed -gt 120 ] && echo "WARNING: spotcheck exceeded 120s budget — consider moving a suite to full-test.sh"

if [ $total_failed -gt 0 ]; then
    echo "FAILED ($total_failed project(s) with failures)"
    exit 1
fi
echo "OK"
