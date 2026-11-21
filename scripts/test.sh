#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

dotnet test tests/Khata.UnitTests/Khata.UnitTests.csproj -c Release
