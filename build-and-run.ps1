# build-and-run.ps1

# Включаем BuildKit
$env:DOCKER_BUILDKIT = "1"

# Сохраняем корневую директорию проекта
$RootDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

Write-Host "=== Root directory: $RootDir ==="

# 1️⃣ Сборка базового образа ta-deps (если нет)
Write-Host "=== Checking ta-deps image ==="

$exists = docker image inspect ta-deps 2>$null

if (-not $?) {
    Write-Host "=== Building ta-deps ==="
    Set-Location "$RootDir\ta-deps"
    docker build -t ta-deps .
    if ($LASTEXITCODE -ne 0) {
        Write-Error "=== Failed to build ta-deps. Aborting. ==="
        exit $LASTEXITCODE
    }
} else {
    Write-Host "=== ta-deps already exists, skipping build ==="
}

# 2️⃣ Сборка проекта через docker-compose
Write-Host "=== Building ta_vms via docker-compose ==="
Set-Location $RootDir
docker compose --profile app build

# 3️⃣ Запуск контейнеров
Write-Host "=== Starting ta_vms containers ==="
docker compose --profile app up