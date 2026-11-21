#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet restore Khata.sln
dotnet build Khata.sln --no-restore -c Release
npm ci --prefix src/Khata.Web
npm run build --prefix src/Khata.Web
