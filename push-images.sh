#!/usr/bin/env bash
set -euo pipefail

# ==============================
# CONFIG
# ==============================

NAMESPACE="treealarm"
REGISTRY="docker.io"
BASE_PATH="$NAMESPACE"
DOCKER_USER="treealarm"
TOKEN="dckr_pat_s2RDYFP0FD3SR-GKpi1ybx9oXL8"

echo "Token length: ${#TOKEN}"

# ==============================
# LOGIN
# ==============================
echo "$TOKEN" | docker login "$REGISTRY" -u "$DOCKER_USER" --password-stdin

# ==============================
# IMAGES
# ==============================
declare -A LOCAL
declare -A REMOTE

LOCAL[0]="vms_rec-vms:latest"
REMOTE[0]="$BASE_PATH/vms:latest"

LOCAL[1]="vms_rec-web_vms:latest"
REMOTE[1]="$BASE_PATH/web_vms:latest"

# ==============================
# TAG + PUSH
# ==============================
for i in 0 1; do
    echo "=== Processing ${LOCAL[$i]} ==="

    docker tag "${LOCAL[$i]}" "${REMOTE[$i]}"
    docker push "${REMOTE[$i]}"
done

echo "=== Done ==="
