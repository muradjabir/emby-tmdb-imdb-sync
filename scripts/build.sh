#!/usr/bin/env bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$PROJECT_ROOT"

mkdir -p dist

echo "Building and running tests in Docker (.NET 8.0 SDK)..."
docker run --rm \
  -v "$PROJECT_ROOT:/src" \
  -w /src \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  bash -c "dotnet test tests/Emby.TmdbImdbSync.Tests -c Release && \
           dotnet publish src/Emby.TmdbImdbSync/Emby.TmdbImdbSync.csproj -c Release -o /src/dist"

# Fix permissions if running non-root on host
if [ -n "${SUDO_UID:-}" ]; then
  chown -R "${SUDO_UID}:${SUDO_GID}" dist
elif [ "$(id -u)" != "0" ]; then
  # running as standard user
  docker run --rm -v "$PROJECT_ROOT:/src" alpine chown -R "$(id -u):$(id -g)" /src/dist 2>/dev/null || true
fi

echo ""
echo "Successfully built plugin:"
ls -lh dist/Emby.TmdbImdbSync.dll
