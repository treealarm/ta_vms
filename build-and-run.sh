#!/usr/bin/env bash
set -euo pipefail

export DOCKER_BUILDKIT=1

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "=== Root directory: $ROOT_DIR ==="

# 1️⃣ Сборка базового образа ta-deps (если нет)
echo "=== Checking ta-deps image ==="

if ! docker image inspect ta-deps &>/dev/null; then
    echo "=== Building ta-deps ==="
    docker build -t ta-deps "$ROOT_DIR/ta-deps"
else
    echo "=== ta-deps already exists, skipping build ==="
fi

# 1️⃣.5 Base image for roi_transcode. Separate from ta-deps because it is the only one that
# needs qsv/vaapi, and ta-deps is the only one that needs openvino.
echo "=== Checking roi-deps image ==="

if ! docker image inspect roi-deps &>/dev/null; then
    echo "=== Building roi-deps ==="
    docker build -t roi-deps "$ROOT_DIR/roi-deps"
else
    echo "=== roi-deps already exists, skipping build ==="
fi

# 2️⃣ Сборка проекта через docker-compose
echo "=== Building ta_vms via docker-compose ==="
docker compose -f "$ROOT_DIR/docker-compose.yml" --profile app build

# 3️⃣ Запуск контейнеров
echo "=== Starting ta_vms containers ==="
docker compose -f "$ROOT_DIR/docker-compose.yml" --profile app up
