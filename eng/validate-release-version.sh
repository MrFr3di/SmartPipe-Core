#!/usr/bin/env bash
set -euo pipefail

exec dotnet run --project eng/SmartPipe.RepositoryChecks -- \
  verify-release-version "$@"
