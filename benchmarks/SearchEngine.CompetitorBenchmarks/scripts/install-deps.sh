#!/usr/bin/env bash
set -euo pipefail

export PATH="/opt/homebrew/bin:/usr/local/bin:/usr/local/share/dotnet:$HOME/.cargo/bin:$PATH"

if [[ "$(uname -s)" == "Darwin" ]]; then
  command -v brew >/dev/null && {
    brew list go >/dev/null 2>&1 || brew install go
    brew list node >/dev/null 2>&1 || brew install node
  }
else
  sudo apt-get update -qq
  sudo apt-get install -y dotnet-sdk-10.0 golang-go nodejs npm g++ nlohmann-json3-dev
fi

if ! command -v rustc >/dev/null; then
  curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh -s -- -y
fi
# shellcheck disable=SC1091
[[ -f "$HOME/.cargo/env" ]] && source "$HOME/.cargo/env"

dotnet --version
rustc --version
go version
node --version
g++ --version | head -1
