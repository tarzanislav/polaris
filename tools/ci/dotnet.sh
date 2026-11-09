#!/usr/bin/env bash
# CI helper: runs one dotnet command; when it fails, the compiler and format messages are collected into a single
# annotation, so the cause is visible on the summary page without opening the log.
# Usage: bash tools/ci/dotnet.sh build Polaris.slnx -c Release
set -uo pipefail
out=$(mktemp)
dotnet "$@" 2>&1 | tee "$out"
code=${PIPESTATUS[0]}
if [ "$code" -ne 0 ]; then
  found=$(grep -E ' (error|warning) [A-Z]{2,}[0-9]+| error [A-Z]+:' "$out" | sed -E 's# \[[^]]*\]$##; s#^.*/(src|tests|tools)/#\1/#' | sort -u | head -30)
  joined=$(printf '%s' "$found" | sed 's/%/%25/g' | sed ':a;N;$!ba;s/\n/%0A/g')
  echo "::error title=dotnet $1::${joined:-no compiler message, see the log}"
fi
exit "$code"
