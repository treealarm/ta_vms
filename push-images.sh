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
# IMAGES  local → remote
# ==============================
declare -a PAIRS=(
    "vms_rec-vmslogger:latest      $BASE_PATH/vmslogger:latest"
    "vms_rec-vmscfg:latest         $BASE_PATH/vmscfg:latest"
    "vms_rec-vmsonvif:latest       $BASE_PATH/vmsonvif:latest"
    "vms_rec-vmsonvif-actors:latest $BASE_PATH/vmsonvif-actors:latest"
    "vms_rec-vmsfs:latest          $BASE_PATH/vmsfs:latest"
    "vms_rec-vms:latest            $BASE_PATH/vms:latest"
    "vms_rec-web_vms:latest        $BASE_PATH/web_vms:latest"
)

# ==============================
# TAG + PUSH
# ==============================
for pair in "${PAIRS[@]}"; do
    local_img=$(echo "$pair" | awk '{print $1}')
    remote_img=$(echo "$pair" | awk '{print $2}')

    echo "=== Processing $local_img ==="
    docker tag "$local_img" "$remote_img"
    docker push "$remote_img"
done

echo "=== Done ==="
