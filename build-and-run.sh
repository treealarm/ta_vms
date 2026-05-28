#!/usr/bin/env bash
set -euo pipefail

export DOCKER_BUILDKIT=1

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "=== Root directory: $ROOT_DIR ==="

# 1️⃣ Сборка базового образа vms-deps (если нет)
echo "=== Checking vms-deps image ==="

if ! docker image inspect vms-deps &>/dev/null; then
    echo "=== Building vms-deps ==="
    docker build -t vms-deps "$ROOT_DIR/vms-deps"
else
    echo "=== vms-deps already exists, skipping build ==="
fi

# 2️⃣ Сборка проекта через docker-compose
echo "=== Building vms_rec via docker-compose ==="
docker compose -f "$ROOT_DIR/docker-compose.yml" build

# 3️⃣ Запуск контейнеров
echo "=== Starting vms_rec containers ==="
docker compose -f "$ROOT_DIR/docker-compose.yml" up
