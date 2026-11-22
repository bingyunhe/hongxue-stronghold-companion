#!/usr/bin/env bash
set -euo pipefail

awk -v package="$1" '$2 == package { print }' SHA256SUMS.txt |
  sha256sum --check --strict -
