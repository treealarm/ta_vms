# Standalone deployment (independent of Square / multitenant_admin)

This directory is a self-contained docker-compose stack: its own `redis`, its own
(isolated, default) docker network, no `profiles:` gating — every service starts on a
plain `docker compose up`. Unlike the repo-root `docker-compose.yml`, it does **not**
need the `Square`/`multitenant_admin` stack to be running anywhere.

It pulls prebuilt images (`treealarm/...:latest`) from Docker Hub rather than building
from source, so it reflects whatever was last published by `../push-images.sh` (or
`../push-images.ps1`) — not necessarily your current local changes. Run that script from
the repo root first if you want the images refreshed before starting.

## Run it

```sh
cd dockerhub
docker compose --env-file .env up -d
```

That's it — Postgres, Redis, MediaMTX, Dapr sidecars and all `vms*`/`web_vms` services
come up together. First start takes a bit longer while images are pulled.

## What you get

- Web UI: `http://localhost:5134` (`WEB_VMS_PORT` in `.env`)
- pgAdmin: `http://localhost:5050` (`admin@admin.com` / `admin123` by default —
  see `PGADMIN_EMAIL`/`PGADMIN_PASSWORD` in `docker-compose.yml` if you want to override)
- MediaMTX: RTSP `8554`, WebRTC `8889`, HLS `8000`

Square integration is **off by default** — `KEYCLOAK_URL` and `SQUARE_*` vars are empty
in `.env`, so the UI runs with no login screen and no push to Square (see
`docs/square-integration-plan.md` in the repo root for what those vars do when set).

## Adding a camera

No camera is pre-configured. Open the web UI and add one the normal way (ONVIF discovery
or a manual RTSP URL) — same flow as in the integrated deployment.

## Stopping

```sh
docker compose --env-file .env down
```

Recorded data and Postgres state live under `./data` on the host and are **not** removed
by `down` (add `-v` only if you want to wipe everything and start fresh).
