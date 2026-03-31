#!/usr/bin/env bash

set -e  # остановка при ошибке

# Включаем BuildKit
export DOCKER_BUILDKIT=1

# Корневая директория проекта (директория скрипта)
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "=== Root directory: $ROOT_DIR ==="

# 1️⃣ Сборка базового образа vms-deps
echo "=== Building vms-deps ==="
cd "$ROOT_DIR/vms-deps"
docker build -t vms-deps .

# 2️⃣ Сборка проекта через docker-compose
echo "=== Building vms_rec via docker-compose ==="
cd "$ROOT_DIR"
docker compose build

# 3️⃣ Запуск контейнеров
echo "=== Starting vms_rec containers ==="
docker compose up