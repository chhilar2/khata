#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet publish src/Khata.Api/Khata.Api.csproj -c Release -o artifacts/api
npm ci --prefix src/Khata.Web
npm run build --prefix src/Khata.Web
