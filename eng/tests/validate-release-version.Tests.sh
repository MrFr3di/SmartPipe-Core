#!/usr/bin/env bash
set -euo pipefail

script_dir="${BASH_SOURCE[0]%/*}"
validator="$(cd "$script_dir/.." && pwd)/validate-release-version.sh"

"$BASH" "$validator" v2.1.2 2.1.2 2.1.2 2.1.2 >/dev/null
output="$("$BASH" "$validator" v2.1.2-rc.1 2.1.2 2.1.2 2.1.2)"
[[ "$output" == *$'PACKAGE_VERSION=2.1.2-rc.1'* ]]
[[ "$output" == *$'BASE_VERSION=2.1.2'* ]]

if "$BASH" "$validator" v2.1.2 2.1.2 2.1.1 2.1.2 >/dev/null 2>&1; then
  echo 'JSON version mismatch was accepted.' >&2
  exit 1
fi

echo 'validate-release-version script tests passed.'
