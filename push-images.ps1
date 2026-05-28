# ==============================
# CONFIG
# ==============================

$Namespace = "treealarm"

$Registry  = "docker.io"
$BasePath  = "$Namespace"

$DockerUser = "treealarm"


$Token = "dckr_pat_s2RDYFP0FD3SR-GKpi1ybx9oXL8"

Write-Host "Token length: $($Token.Length)"

# ==============================
# LOGIN
# ==============================
docker login $Registry -u $DockerUser -p $Token

if ($LASTEXITCODE -ne 0) {
    Write-Error "Docker login failed"
    exit 1
}

# ==============================
# IMAGES  local → remote
# ==============================
$images = @(
    @{ local="vms_rec-vmslogger:latest";       remote="$BasePath/vmslogger:latest" },
    @{ local="vms_rec-vmscfg:latest";          remote="$BasePath/vmscfg:latest" },
    @{ local="vms_rec-vmsonvif:latest";        remote="$BasePath/vmsonvif:latest" },
    @{ local="vms_rec-vmsonvif-actors:latest"; remote="$BasePath/vmsonvif-actors:latest" },
    @{ local="vms_rec-vmsfs:latest";           remote="$BasePath/vmsfs:latest" },
    @{ local="vms_rec-vms:latest";             remote="$BasePath/vms:latest" },
    @{ local="vms_rec-web_vms:latest";         remote="$BasePath/web_vms:latest" }
)

# ==============================
# TAG + PUSH
# ==============================
foreach ($img in $images) {
    Write-Host "=== Processing $($img.local) ==="

    docker tag $img.local $img.remote
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Tag failed for $($img.local)"
        exit 1
    }

    docker push $img.remote
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Push failed for $($img.remote)"
        exit 1
    }
}

Write-Host "=== Done ==="
