#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

: "${KHATA_API_BASE_URL:?Set KHATA_API_BASE_URL to the running Functions host URL}"
dotnet test tests/Khata.EndToEndTests/Khata.EndToEndTests.csproj -c Release
