#!/bin/bash
# Interactive local setup: input is not echoed or written into shell history.
set -eu
REPO_DIR=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$REPO_DIR"
read -r -p "Oxylabs API username: " oxy_username
read -r -s -p "Oxylabs API password (hidden): " oxy_password
printf '\n'
if [ -z "$oxy_username" ] || [ -z "$oxy_password" ]; then
    printf '%s\n' 'Both values are required. No configuration was changed.' >&2
    exit 1
fi
"$REPO_DIR/scripts/dotnet.sh" user-secrets set 'Oxylabs:Username' "$oxy_username" --project src/AmazonProductExplorer >/dev/null
"$REPO_DIR/scripts/dotnet.sh" user-secrets set 'Oxylabs:Password' "$oxy_password" --project src/AmazonProductExplorer >/dev/null
unset oxy_username oxy_password
printf '%s\n' 'Credentials saved in local .NET development user secrets. Restart the app with ./scripts/run.sh, then select Live Amazon.'
