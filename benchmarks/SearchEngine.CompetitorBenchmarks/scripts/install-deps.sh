#!/usr/bin/env bash
set -euo pipefail

export PATH="/opt/homebrew/bin:/usr/local/bin:/usr/local/share/dotnet:$PATH"

if ! command -v dotnet >/dev/null; then
  echo "dotnet SDK required (https://dotnet.microsoft.com/download)"
  exit 1
fi

dotnet --version
