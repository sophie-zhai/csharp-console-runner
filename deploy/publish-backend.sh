#!/usr/bin/env bash
set -euo pipefail

dotnet publish backend/Runner.Api/Runner.Api.csproj -c Release -o publish/backend

echo "Published to publish/backend"
echo "Copy that directory to your Linux runner host, e.g. /opt/csharp-runner"
