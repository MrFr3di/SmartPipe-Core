#!/usr/bin/env bash
set -euo pipefail

script_dir="${BASH_SOURCE[0]%/*}"
validator="$(cd "$script_dir/.." && pwd)/validate-release-version.sh"
temp_dir="$(mktemp -d)"
trap 'rm -rf -- "$temp_dir"' EXIT
cat >"$temp_dir/dotnet" <<'EOF'
#!/usr/bin/env bash
printf '%s\n' "$*"
EOF
chmod +x "$temp_dir/dotnet"
output="$(PATH="$temp_dir:$PATH" "$BASH" "$validator" --tag v2.2.0 --mode current --package-directory artifacts/packages)"
[[ "$output" == 'run --project eng/SmartPipe.RepositoryChecks -- verify-release-version --tag v2.2.0 --mode current --package-directory artifacts/packages' ]]
[[ "$(grep -c 'verify-release-version' "$validator")" -eq 1 ]]

echo 'validate-release-version script tests passed.'
