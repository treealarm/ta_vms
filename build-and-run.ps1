# build-and-run.ps1

# Turn on BuildKit
$env:DOCKER_BUILDKIT = "1"

# Remember the project's root directory
$RootDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

Write-Host "=== Root directory: $RootDir ==="

# 1. Build the ta-deps base image (if missing)
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

# 1⃣.5 Base image for roitrc. Separate from ta-deps because it is the only one that needs
# qsv/vaapi, and ta-deps is the only one that needs openvino. Its Dockerfile belongs to the
# encoder and comes in with the submodule, so `git submodule update --init` first.
Write-Host "=== Checking roi-deps image ==="

$exists = docker image inspect roi-deps 2>$null

if (-not $?) {
    Write-Host "=== Building roi-deps ==="
    Set-Location "$RootDir\roitrc\ta_roienc\docker\intel"
    docker build -t roi-deps -f Dockerfile.deps .
    if ($LASTEXITCODE -ne 0) {
        Write-Error "=== Failed to build roi-deps. Aborting. ==="
        exit $LASTEXITCODE
    }
} else {
    Write-Host "=== roi-deps already exists, skipping build ==="
}

# 2. Build the project with docker-compose
Write-Host "=== Building ta_vms via docker-compose ==="
Set-Location $RootDir
docker compose --profile app build

# 3. Start the containers
Write-Host "=== Starting ta_vms containers ==="
docker compose --profile app up