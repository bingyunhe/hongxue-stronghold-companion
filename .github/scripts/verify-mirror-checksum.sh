#!/usr/bin/env bash
set -euo pipefail

awk -v package="$1" '{ sub(/\r$/, "") } $2 == package { print }' SHA256SUMS.txt |
  sha256sum --check --strict -
