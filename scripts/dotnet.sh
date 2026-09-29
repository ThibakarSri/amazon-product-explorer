#!/bin/sh
set -eu
REPO_DIR=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
export DOTNET_CLI_TELEMETRY_OPTOUT=1
if command -v dotnet >/dev/null 2>&1; then
    exec dotnet "$@"
elif [ -x "$REPO_DIR/.tools/dotnet/dotnet" ]; then
    export DOTNET_ROOT="$REPO_DIR/.tools/dotnet"
    export DOTNET_CLI_HOME="$REPO_DIR/.tools/dotnet-home"
    exec "$REPO_DIR/.tools/dotnet/dotnet" "$@"
else
    printf '%s\n' 'Install the .NET 10 SDK from https://dotnet.microsoft.com/en-us/download/dotnet/10.0, reopen your terminal, and try again.' >&2
    exit 1
fi
