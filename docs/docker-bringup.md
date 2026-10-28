# TreeAlarm VMS Docker build and bring-up

Practical runbook from a full rebuild on the `roi` branch. Two modes:

| Mode | When | What you need |
|------|------|----------------|
| **Dev** (`ta_vms`) | development, debugging, build from source | sibling checkouts + Docker BuildKit |
| **Prod** (`ta_install`) | staging / install | prebuilt `treealarm/*` images from Hub only |

---

## 0. Repositories (siblings in one directory)

```
<workspace>/
  ta_vms/            # this repo — VMS services, Dockerfiles, ta-deps; roi-deps comes
                     #   from the roitrc/ta_roienc submodule (github treealarm/roi_transcode)
  ta_install/        # product compose (prebuilt images), .env, push/build scripts
  video_a/           # analytics-worker (FROM ta-deps)
```

Minimum for **build**: `ta_vms` + `video_a` (if analytics is needed).  
Minimum for **running the product**: `ta_install` only (after `push-images.sh`).

```bash
# example
cd /mnt/wdblue/tests
git clone <url>/ta_vms.git
git clone <url>/ta_install.git
git clone <url>/video_a.git
```

Branches must stay aligned (e.g. `roi` in `ta_vms` with matching changes in `ta_install`).

---

## 1. Docker network

Both the dev compose (`ta_vms/docker-compose.yml`) and the product compose (`ta_install`) run in
compose's own default network. Nothing has to exist beforehand, and no other project's stack has to
be up.

---

## 2. Build from source (dev / publish)

Requires **Docker BuildKit**:

```bash
export DOCKER_BUILDKIT=1
```

### 2.1 C++ base images (slow, rare)

Do **not** build `ta-deps` and `roi-deps` in parallel: both mount the same BuildKit cache at `/vcpkg/buildtrees` → `vcpkg-running.lock` / “another vcpkg may be running”.

```bash
cd ta_vms

# video_srv + video_a analytics-worker (~3–5 h from scratch: openvino)
docker build -t ta-deps ./ta-deps

# roitrc (~1–2 h): Ubuntu 24.04, ffmpeg[qsv,vaapi], libdrm-dev, python3.
# The Dockerfile belongs to the encoder and arrives with the submodule.
docker build -t roi-deps -f roitrc/ta_roienc/docker/intel/Dockerfile.deps roitrc/ta_roienc/docker/intel
```

[`../ta_install/scripts/build-images.sh`](../../ta_install/scripts/build-images.sh) gets both
through `build-deps-images.sh`, which keys them by content: the tag is the git tree hash of the
directory each one is built from ([`scripts/deps-image-tags.sh`](../scripts/deps-image-tags.sh)),
so an edit to `ta-deps/` or the encoder's `docker/intel/` gets a new base, and an existing
`treealarm/ta-deps:<hash>` is reused — or pulled, if someone already published it. Force a rebuild
of the current one with `docker rmi treealarm/ta-deps:<hash>` (the bare `ta-deps` is only an alias).

[`build-and-run.sh`](../build-and-run.sh) still skips a base whenever the bare tag exists,
whatever it was built from; `docker rmi ta-deps roi-deps` forces it there.

CI cannot build either base (ta-deps alone outlasts a hosted runner's job limit), so after changing
one, publish it before pushing the change:
`../ta_install/scripts/push-deps-images.sh`. Otherwise the C++ jobs of
[`images.yml`](../.github/workflows/images.yml) fail and say so.

**roi-deps layers (not vcpkg alone):**

1. **apt in the builder:** `libdrm-dev`, `python3`, toolchain  
2. **vcpkg:** protobuf, grpc, nlohmann-json, `ffmpeg[...,qsv,vaapi]`  
3. **runtime in [`roitrc/Dockerfile`](../roitrc/Dockerfile):** `intel-media-va-driver-non-free`, `libmfx-gen1.2`, vcpkg `libva` next to the binary, `vainfo` / `onevpl-tools`

### 2.2 ta_vms services

One at a time (lower peak RAM), same pattern as `build-images.sh`:

```bash
cd ta_vms
BUILDABLE=$(docker compose --profile app config | python3 -c "
import sys, yaml
d = yaml.safe_load(sys.stdin)
for name, svc in d.get('services', {}).items():
    if 'build' in svc: print(name)
")
for svc in $BUILDABLE; do
  echo "--- $svc ---"
  docker compose --profile app build "$svc"
done
```

Expected tags: `ta_vms-vms`, `ta_vms-roitrc`, `ta_vms-web_vms`, `ta_vms-vmscfg`, … (prefix from `name: ta_vms` in compose).

| Image | FROM |
|-------|------|
| `ta_vms-vms` | `ta-deps` → [`Dockerfile`](../Dockerfile) |
| `ta_vms-roitrc` | `roi-deps` → [`roitrc/Dockerfile`](../roitrc/Dockerfile) |
| others | .NET SDK/aspnet in their own Dockerfiles |

### 2.3 analytics-worker

```bash
docker build -t analytics-worker ../video_a
```

### 2.4 Publish (prod pipeline)

```bash
cd ../ta_install
./scripts/build-images.sh    # bases + ta_vms + analytics-worker
./scripts/push-images.sh     # ta_vms-* → treealarm/*:latest
```

Requires `docker login` (or `DOCKERHUB_USERNAME` + `DOCKERHUB_TOKEN`).

---

## 3. Run

### 3.1 Dev — full ta_vms stack

Before `up`, free ports if other containers are running (typical clash: old `mediamtx` on 8189/udp, foreign postgres on 5432):

```bash
docker ps --format '{{.Names}}\t{{.Ports}}' | grep -E '8189|8554|5432'
# if needed: docker stop mediamtx <other-postgres>
```

```bash
cd ta_vms
mkdir -p data
docker compose --profile app up -d
```

First start: postgres runs `initdb` — `db-init` may miss the 60 s window. Retry:

```bash
docker compose run --rm db-init
docker compose --profile app up -d
```

Verify:

```bash
docker compose --profile app ps -a
docker compose logs roitrc --tail 20   # listening on 0.0.0.0:5142
curl -s -o /dev/null -w '%{http_code}\n' http://localhost:5134/
```

UI: **http://localhost:5134** (`WEB_VMS_PORT` in `.env`).

**GPU for roitrc** (native Linux + Intel iGPU; **not** Docker Desktop):

```bash
cp ../ta_install/docker-compose.gpu.yml.example docker-compose.override.yml
# group_add: "$(stat -c '%g' /dev/dri/renderD128)"  — numeric GID, not the name "render"
docker compose --profile app up -d roitrc
docker compose exec roitrc ls -l /dev/dri
docker compose exec roitrc vainfo
docker compose exec roitrc vpl-inspect
```

Without the override the stack still runs on CPU (`libx264`).

### 3.2 Prod — ta_install

```bash
cd ta_install
# set secrets in .env and mirror them in init_files/dapr_components/
docker compose --env-file .env up -d
```

See [`../ta_install/README.md`](../../ta_install/README.md).

---

## 4. Expected “Up” set

| Group | Services |
|-------|----------|
| Infra | `postgres`, `rabbitmq` (healthy), `mediamtx`, `db-init` (Exited 0) |
| App | `vms`, `roitrc`, `vmscfg`, `vmsfs`, `vmsutils`, `vmslogger`, `vmsonvif`, `vmsonvif-actors`, `vmssingleton`, `vmsanalyticssingleton`, `web_vms` |
| Sidecars | `*-dapr` + `dapr-placement` |

Short-lived Dapr “connection refused” at startup is a normal sidecar/app race; within a few seconds RPCs should succeed (e.g. `ListCameras` → 200).

---

## 5. Common failures

| Symptom | Cause | Fix |
|---------|-------|-----|
| `Bind … 8189 failed` | foreign MediaMTX | `docker stop mediamtx` |
| `postgres did not become ready in 60s` | first initdb | `compose run --rm db-init` then `up -d` |
| `vcpkg-running.lock` / Device busy | parallel base builds | build `ta-deps` and `roi-deps` **sequentially** |
| `roitrc` Created, not Up | waits on `db-init` / rabbitmq | fix db-init, then `up -d` |
| QSV silently falls to vaapi | missing `libmfx-gen1.2` in runtime | image from current `roitrc/Dockerfile` |
| GPU devices: no such file | Docker Desktop VM | CPU fallback; GPU only on native Linux |

---

## 6. Checklist — from zero to a working UI

1. [ ] Clone `ta_vms`, `ta_install`, and `video_a` if needed
2. [ ] Build `ta-deps`, then `roi-deps` (not in parallel)
3. [ ] `docker compose --profile app build` for all services (or `ta_install/scripts/build-images.sh`)
4. [ ] (Optional) `push-images.sh` → Hub
5. [ ] Ports 5432 / 8554 / 8889 / 8189 are free
6. [ ] `docker compose --profile app up -d` (or prod compose)
7. [ ] `db-init` Exit 0, UI responds on `:5134`
8. [ ] (Optional) GPU override for `roitrc`

---

## 7. Quick commands (images already built)

```bash
cd ta_vms
export DOCKER_BUILDKIT=1
docker compose --profile app up -d
docker compose run --rm db-init   # if the first up failed on postgres
docker compose --profile app up -d
docker compose --profile app ps -a
```

Full base rebuild (hours):

```bash
docker rmi ta-deps roi-deps 2>/dev/null || true
docker build -t ta-deps ./ta-deps
docker build -t roi-deps -f roitrc/ta_roienc/docker/intel/Dockerfile.deps roitrc/ta_roienc/docker/intel
# then rebuild services + up again
```
