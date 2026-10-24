#!/usr/bin/env bash
# DropWheel build entry point.
#   macOS / Linux : restore + build + test DropWheel.Core, DropWheel.Engines and their tests. Engines targets
#                   net10.0-windows but only needs Windows APIs at run time for things the tests do not touch,
#                   and Magick.NET ships macOS / Linux native libraries.
#   Windows       : build the whole DropWheel.slnx (WinUI 3 app, Shell, Engines need net10.0-windows).
# Usage: ./build.sh [-c Debug|Release] [--no-test]
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")"

CONFIG=Release
RUN_TESTS=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    -c|--configuration) CONFIG="$2"; shift 2 ;;
    --no-test) RUN_TESTS=0; shift ;;
    -h|--help) sed -n '2,5p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# Prefer a user-local SDK (~/.dotnet) when dotnet is not on PATH.
if ! command -v dotnet >/dev/null 2>&1 && [[ -x "$HOME/.dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$HOME/.dotnet"
  export PATH="$DOTNET_ROOT:$PATH"
fi
command -v dotnet >/dev/null 2>&1 || { echo "dotnet not found. Install the .NET 10 SDK (see scripts/dev-mac.md)." >&2; exit 1; }

is_windows() {
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*|Windows_NT) return 0 ;;
  esac
  [[ "${OS:-}" == "Windows_NT" ]]
}

CORE=src/DropWheel.Core/DropWheel.Core.csproj
TESTS=tests/DropWheel.Core.Tests/DropWheel.Core.Tests.csproj
ENGINE_TESTS=tests/DropWheel.Engines.Tests/DropWheel.Engines.Tests.csproj

echo "==> dotnet $(dotnet --version) · $(uname -s) · configuration=$CONFIG"

if is_windows; then
  echo "==> Windows: building DropWheel.slnx (x64)"
  dotnet restore DropWheel.slnx -p:Platform=x64
  dotnet build DropWheel.slnx -c "$CONFIG" -p:Platform=x64 --no-restore
  if [[ $RUN_TESTS -eq 1 ]]; then
    dotnet test "$TESTS" -c "$CONFIG" --no-restore
    dotnet test "$ENGINE_TESTS" -c "$CONFIG" -p:Platform=x64 --no-build
  fi
  echo "==> For the MSIX package run: pwsh ./scripts/build-windows.ps1"
else
  echo "==> $(uname -s): building Core, Engines + tests only (Shell and the WinUI app need Windows)"
  # The test host must match the machine, so the Engines platform follows the CPU.
  case "$(uname -m)" in arm64|aarch64) PLATFORM=ARM64 ;; *) PLATFORM=x64 ;; esac
  dotnet restore "$CORE"
  dotnet restore "$TESTS"
  dotnet build "$TESTS" -c "$CONFIG" --no-restore
  dotnet build "$ENGINE_TESTS" -c "$CONFIG" -p:Platform=$PLATFORM -p:EnableWindowsTargeting=true
  if [[ $RUN_TESTS -eq 1 ]]; then
    dotnet test "$TESTS" -c "$CONFIG" --no-build
    dotnet test "$ENGINE_TESTS" -c "$CONFIG" -p:Platform=$PLATFORM --no-build
  fi
fi

echo "==> done"
