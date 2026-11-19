#!/usr/bin/env bash
set -euo pipefail

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$PROJECT_ROOT"

EMBY_CONTAINER_NAME="${EMBY_CONTAINER_NAME:-embyserver}"
PLUGIN_DLL="$PROJECT_ROOT/dist/Emby.TmdbImdbSync.dll"

if [ ! -f "$PLUGIN_DLL" ]; then
  echo "Error: $PLUGIN_DLL not found. Running build.sh first..."
  bash "$PROJECT_ROOT/scripts/build.sh"
fi

echo "Deploying Emby.TmdbImdbSync.dll to container '$EMBY_CONTAINER_NAME'..."

# Copy DLL directly into container /config/plugins/
docker cp "$PLUGIN_DLL" "${EMBY_CONTAINER_NAME}:/config/plugins/Emby.TmdbImdbSync.dll"

# Match ownership to existing plugin files (user 1000:users)
docker exec "$EMBY_CONTAINER_NAME" chown 1000:users /config/plugins/Emby.TmdbImdbSync.dll 2>/dev/null || true
docker exec "$EMBY_CONTAINER_NAME" chmod 644 /config/plugins/Emby.TmdbImdbSync.dll 2>/dev/null || true

echo "Restarting Emby Server container '$EMBY_CONTAINER_NAME'..."
docker restart "$EMBY_CONTAINER_NAME"

echo "Waiting for Emby Server to initialize (10s)..."
sleep 10

echo "Checking Emby logs for plugin loading..."
docker exec "$EMBY_CONTAINER_NAME" sh -c "tail -n 100 /config/logs/embyserver.txt | grep -iE 'TmdbImdbSync|Loaded plugin' || tail -n 40 /config/logs/embyserver.txt"

echo ""
echo "Deployment complete! Visit your Emby Dashboard -> Plugins to configure TMDb & IMDb Sync."
