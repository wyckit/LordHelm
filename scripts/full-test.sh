#!/usr/bin/env bash
# full-test.sh — entire solution including E2E; designed for overnight runs.
set -uo pipefail
start=$(date +%s)
echo "Running FULL test suite (solution-level) ..."
dotnet test C:/Software/LordHelm/LordHelm.slnx --nologo -v minimal
code=$?
elapsed=$(( $(date +%s) - start ))
echo
echo "full-test total: ${elapsed}s"
exit $code
