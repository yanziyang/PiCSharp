#!/usr/bin/env bash
#
# Test-parity audit.
#
# docs/differential-testing.md makes the upstream suite the specification: "Ported
# with the implementation, in the same PR. Never deferred." The skip audit catches
# tests that were ported and then skipped. It cannot catch tests that were never
# ported at all — a package can land at 2% coverage and stay green.
#
# This closes that hole. For each ported package it compares the number of C# test
# cases against the upstream TypeScript cases, and fails if the count drops below
# the floor recorded in .test-parity. Floors ratchet upward only: raising one is a
# normal part of adding tests; lowering one requires deleting the line deliberately.
#
# Usage:  tools/test-parity.sh          audit against .test-parity
#         tools/test-parity.sh --update rewrite floors to current counts
set -uo pipefail

cd "$(dirname "$0")/.."

REF="reference/pi/packages"
BUDGET=".test-parity"
UPDATE=0
[ "${1:-}" = "--update" ] && UPDATE=1

# upstream package -> C# test project
PAIRS="
protocol:Pi.Protocol
telemetry:Pi.Telemetry
ai:Pi.Ai
agent:Pi.AgentCore
client:Pi.Client
server:Pi.Server
tui:Pi.Tui
"

count_upstream() {
  local pkg="$1"
  [ -d "$REF/$pkg" ] || { echo 0; return; }
  find "$REF/$pkg" -name '*.test.ts' -not -path '*/node_modules/*' -exec grep -hoE '^[[:space:]]*(it|test)\(' {} + 2>/dev/null | wc -l | tr -d ' '
}

# Implementation LOC. C# runs 1.2-1.5x more verbose than TypeScript, so a ratio
# near 100% indicates UNDER-porting, not parity. Reported, not gated: this is a
# visibility signal, and the wave 1-5 review missed it entirely.
loc_upstream() {
  local pkg="$1"
  [ -d "$REF/$pkg/src" ] || { echo 0; return; }
  find "$REF/$pkg/src" -name '*.ts' ! -name '*.test.ts' -exec cat {} + 2>/dev/null | wc -l | tr -d ' '
}

loc_ported() {
  local proj="$1"
  local dirs=""
  for d in "src/$proj" "src/$proj.Abstractions" "src/$proj.Testing"; do
    [ -d "$d" ] && dirs="$dirs $d"
  done
  [ -n "$dirs" ] || { echo 0; return; }
  # shellcheck disable=SC2086
  find $dirs -name '*.cs' ! -path '*/obj/*' ! -path '*/bin/*' -exec cat {} + 2>/dev/null | wc -l | tr -d ' '
}

count_ported() {
  local proj="$1"
  local dir="tests/${proj}.Tests"
  [ -d "$dir" ] || { echo 0; return; }
  # ProjectReferenceTests.cs is scaffold wiring, not a ported upstream test.
  local attrs skips
  attrs=$(find "$dir" -name '*.cs' ! -name 'ProjectReferenceTests.cs' \
    -exec grep -hoE '\[(Fact|Theory)[]( ]' {} + 2>/dev/null | wc -l | tr -d ' ')
  # Skipped tests do not count as ported coverage. Without this the two gates
  # contradict each other: the skip audit penalises a skip while parity rewards
  # it, so a packet could raise its parity score by adding empty skipped stubs.
  # A Skip= can sit on a later line of a multi-line attribute, so match the
  # Skip= line itself, exactly as .github/workflows/ci.yml does.
  skips=$(find "$dir" -name '*.cs' ! -name 'ProjectReferenceTests.cs' \
    -exec grep -hcE 'Skip[[:space:]]*=' {} + 2>/dev/null | awk '{s+=$1} END {print s+0}')
  echo $(( attrs - skips ))
}

if [ "$UPDATE" = "1" ]; then
  {
    echo "# Test-parity floors. Ported C# test cases may not fall below these."
    echo "# Regenerate with: tools/test-parity.sh --update"
    echo "# Format: <upstream-package> <csharp-project> <test-floor> <upstream-cases> <impl-loc-at-record>"
    for pair in $PAIRS; do
      pkg="${pair%%:*}"; proj="${pair##*:}"
      echo "$pkg $proj $(count_ported "$proj") $(count_upstream "$pkg") $(loc_ported "$proj")"
    done
  } > "$BUDGET"
  echo "wrote $BUDGET"
  exit 0
fi

fail=0
printf '%-12s %-16s %8s %8s %8s   %s\n' PACKAGE PROJECT UPSTREAM PORTED FLOOR STATUS
printf '%s\n' "----------------------------------------------------------------------------"

while read -r pkg proj floor _recorded recorded_loc; do
  case "$pkg" in ""|\#*) continue;; esac
  up=$(count_upstream "$pkg")
  got=$(count_ported "$proj")
  now_loc=$(loc_ported "$proj")
  recorded_loc=${recorded_loc:-0}
  pct=0
  [ "$up" -gt 0 ] && pct=$(( got * 100 / up ))

  # Implementation may not outgrow its tests. A floor alone cannot catch this:
  # shipping code with no tests stays above the floor indefinitely, which is
  # exactly how 1,212 untested lines landed green in commit 831da91.
  grown=$(( now_loc - recorded_loc ))
  if [ "$got" -lt "$floor" ]; then
    status="FAIL (below floor $floor)"
    fail=1
  elif [ "$grown" -gt 200 ] && [ "$got" -le "$floor" ]; then
    status="FAIL (+${grown} impl lines, no new tests)"
    fail=1
  else
    status="ok  ${pct}% of upstream"
  fi
  printf '%-12s %-16s %8s %8s %8s   %s\n' "$pkg" "$proj" "$up" "$got" "$floor" "$status"
done < "$BUDGET"

if [ "$fail" = "1" ]; then
  echo ""
  echo "::error::Test-parity ratchet failed."
  echo "Either the ported test count fell below its floor, or implementation grew by more"
  echo "than 200 lines without a single new test. Tests ship in the same PR as the code."
  echo "If a floor is genuinely wrong, change it in the same commit with a written justification."
  exit 1
fi

echo ""
echo "IMPLEMENTATION COMPLETENESS  (LOC; C# runs 1.2-1.5x more verbose, so <100% means under-ported)"
printf '%-12s %-16s %10s %10s %8s
' PACKAGE PROJECT UPSTREAM PORTED RATIO
printf '%s
' "----------------------------------------------------------------------------"
while read -r pkg proj _floor _recorded; do
  case "$pkg" in ""|\#*) continue;; esac
  u=$(loc_upstream "$pkg"); g=$(loc_ported "$proj")
  r=0; [ "$u" -gt 0 ] && r=$(( g * 100 / u ))
  flag=""
  [ "$r" -lt 40 ] && flag="   <-- substantially unported"
  printf '%-12s %-16s %10s %10s %7s%%%s
' "$pkg" "$proj" "$u" "$g" "$r" "$flag"
done < "$BUDGET"

echo ""
echo "All packages at or above their recorded test floors."
echo "Percentages are coverage against the upstream suite, which docs/differential-testing.md"
echo "treats as the specification. Low percentages are technical debt, not passing grades."
