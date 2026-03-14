# build-and-run.ps1

# Включаем BuildKit
$env:DOCKER_BUILDKIT = "1"

# Сохраняем корневую директорию проекта
$RootDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

Write-Host "=== Root directory: $RootDir ==="

# 1️⃣ Сборка базового образа vms-deps
Write-Host "=== Building vms-deps ==="
Set-Location "$RootDir\vms-deps"
docker build -t vms-deps .

# 2️⃣ Сборка проекта через docker-compose
Write-Host "=== Building vms_rec via docker-compose ==="
Set-Location $RootDir
docker compose build

# 3️⃣ Запуск контейнера
Write-Host "=== Starting vms_rec containers ==="
docker compose up