#!/usr/bin/env bash
set -euo pipefail

export DOCKER_BUILDKIT=1

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "=== Root directory: $ROOT_DIR ==="

# 1. Build the ta-deps base image (if missing)
echo "=== Checking ta-deps image ==="

if ! docker image inspect ta-deps &>/dev/null; then
    echo "=== Building ta-deps ==="
    docker build -t ta-deps "$ROOT_DIR/ta-deps"
else
    echo "=== ta-deps already exists, skipping build ==="
fi

# 1.5 Base image for roitrc. Separate from ta-deps because it is the only one that needs
# qsv/vaapi, and ta-deps is the only one that needs openvino. Its Dockerfile belongs to the
# encoder and comes in with the submodule, so `git submodule update --init` first.
echo "=== Checking roi-deps image ==="

if ! docker image inspect roi-deps &>/dev/null; then
    echo "=== Building roi-deps ==="
    docker build -t roi-deps -f "$ROOT_DIR/roitrc/ta_roienc/docker/intel/Dockerfile.deps" \
        "$ROOT_DIR/roitrc/ta_roienc/docker/intel"
else
    echo "=== roi-deps already exists, skipping build ==="
fi

# 2. Build the project with docker-compose
echo "=== Building ta_vms via docker-compose ==="
docker compose -f "$ROOT_DIR/docker-compose.yml" --profile app build

# 3. Start the containers
echo "=== Starting ta_vms containers ==="
docker compose -f "$ROOT_DIR/docker-compose.yml" --profile app up
