#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 3 ]]; then
  echo "usage: eng/validate-release-version.sh <tag> <core-version> <extensions-version>" >&2
  exit 2
fi

tag="$1"
core_version="$2"
extensions_version="$3"

if [[ ! "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$ ]]; then
  echo "Invalid release tag: $tag" >&2
  echo "Expected examples: v2.1.1, v2.1.1-rc.1, v2.1.1-preview.1" >&2
  exit 1
fi

package_version="${tag#v}"
base_version="${package_version%%-*}"

if [[ "$core_version" != "$base_version" ]]; then
  echo "Core project version '$core_version' does not match tag base version '$base_version'." >&2
  exit 1
fi

if [[ "$extensions_version" != "$base_version" ]]; then
  echo "Extensions project version '$extensions_version' does not match tag base version '$base_version'." >&2
  exit 1
fi

echo "PACKAGE_VERSION=$package_version"
echo "BASE_VERSION=$base_version"
