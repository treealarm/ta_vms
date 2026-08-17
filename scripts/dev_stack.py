#!/usr/bin/env python3
"""Bring the dev stack up, down or back up again without VS Code.

Everything is read from the files F5 itself uses, so this cannot drift away from the IDE:
`web_vms/dapr.yaml` gives each sidecar its app id and ports, `.vscode/launch.json` gives the
application its program, cwd and environment. The two are joined on the project directory —
dapr.yaml's `appDirPath` and the launch configuration's `cwd` resolve to the same path.

It used to take the daprd flags out of the `dapr:<app>:start` tasks in tasks.json instead. That
stopped working when those tasks became `dapr run` (they no longer spell out a daprd command
line), which left this script seeing zero services and failing with a KeyError. dapr.yaml is the
better source anyway: it is now the one file that lists every sidecar.

Sidecars started here are plain daprd processes with their own pid files, not `dapr run -f`, so
they can be started and stopped one service at a time — which is the point of this script. F5
starts the same sidecars through the Dapr CLI, so do not run both at once: they would fight over
the same ports.

Infrastructure (postgres, rabbitmq, mediamtx, dapr placement) is not managed here; it lives in docker
compose and is expected to be up already — including `db-init`, which creates the service
databases and which a bare `docker compose up` runs on its own.

Usage:
    dev_stack.py up [--build] [service ...]
    dev_stack.py down [service ...]
    dev_stack.py restart [--build] service [service ...]
    dev_stack.py status
"""

from __future__ import annotations

import json
import os
import re
import signal
import socket
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PID_DIR = Path("/tmp/web_vms-dapr")
RUN_FILE = ROOT / "web_vms" / "dapr.yaml"

# Start order, by app id: config first (everyone resolves cameras and storages through it), then
# the leaf services, then the media server, then the gateway that fans out to all of them.
# Anything present in dapr.yaml but missing here still works, it just starts last.
ORDER = [
    "vmscfg",
    "vmslogger",
    "vmsutils",
    "vmssingleton",
    "vmssquaresingleton",
    "vmsonvif",
    "vmsonvif-actors",
    "vmsfs",
    "vmsanalyticssingleton",
    "vms",
    "web-vms",
]

# The second node (its own media_server, VmsFs, VmsOnvif and actor host, pinned to server id
# "vms-2") used to be startable with --two-nodes. Its launch configurations were removed from
# .vscode/launch.json, so there is nothing left to start; bringing it back means adding those
# configurations and matching entries in web_vms/dapr.yaml, after which they appear here on their
# own. Sharding work needs it — a single node cannot show a camera handed from one to another.


def _load_jsonc(path: Path) -> dict:
    """Parses a .vscode JSON file, dropping whole-line // comments (never inline — URLs use //)."""
    text = re.sub(r"^\s*//.*$", "", path.read_text(), flags=re.M)
    return json.loads(text)


def _expand(value: str) -> str:
    return value.replace("${workspaceFolder}", str(ROOT))


def _run_file() -> tuple[list[dict], Path]:
    """The apps declared in web_vms/dapr.yaml, plus the resources path they share.

    Parsed by hand rather than with PyYAML: the script has no dependencies today and the shape it
    needs is a flat list of `key: value` under `- appID:`.
    """
    apps: list[dict] = []
    resources = RUN_FILE.parent / "../init_files/dapr_components"
    current: dict | None = None
    in_common = False
    for raw in RUN_FILE.read_text().splitlines():
        line = raw.split("#", 1)[0].rstrip()
        if not line.strip():
            continue
        if not line.startswith((" ", "\t")):
            in_common = line.startswith("common:")
            if line.startswith("apps:"):
                current = None
            continue
        stripped = line.strip()
        if stripped.startswith("- appID:"):
            in_common = False
            current = {"appID": stripped.split(":", 1)[1].strip()}
            apps.append(current)
            continue
        if ":" not in stripped:
            continue
        key, value = (part.strip() for part in stripped.split(":", 1))
        if in_common and key == "resourcesPath":
            resources = RUN_FILE.parent / value
        elif current is not None:
            current[key] = value
    return apps, resources.resolve()


def _daprd_args(app: dict, resources: Path) -> list[str]:
    """The daprd command line matching one entry of the run file."""
    args = [
        "-app-id", app["appID"],
        "-app-port", app["appPort"],
        "-app-protocol", app.get("appProtocol", "http"),
        "-dapr-http-port", app["daprHTTPPort"],
        "-dapr-grpc-port", app["daprGRPCPort"],
        "-resources-path", str(resources),
    ]
    for flag, field in (("-metrics-port", "metricsPort"),
                        ("-dapr-internal-grpc-port", "daprInternalGRPCPort")):
        if field in app:
            args += [flag, app[field]]
    if "placementHostAddress" in app:
        # The run file gives a bare hostname on purpose (the CLI appends each platform's default
        # port); daprd itself does not, so the Linux default goes in here.
        host = app["placementHostAddress"]
        args += ["-placement-host-address", host if ":" in host else f"{host}:50005"]
    return args


def _services() -> dict[str, dict]:
    """Builds the service table by joining dapr.yaml (sidecar) with launch.json (application).

    The join key is the project directory: dapr.yaml's appDirPath is relative to web_vms/, and a
    launch configuration's cwd points at the same place. Nothing else ties the two files together
    — app ids do not appear in launch.json for the .NET services.
    """
    launch = _load_jsonc(ROOT / ".vscode" / "launch.json")
    apps, resources = _run_file()

    by_dir: dict[Path, dict] = {}
    for cfg in launch["configurations"]:
        # Two configurations share media_server/: the Windows one only differs in how it is
        # debugged, and this script runs the binary itself.
        if not cfg.get("cwd") or cfg["name"].endswith("[Windows]"):
            continue
        by_dir[Path(_expand(cfg["cwd"])).resolve()] = cfg

    services: dict[str, dict] = {}
    for app in apps:
        app_dir = (RUN_FILE.parent / app.get("appDirPath", ".")).resolve()
        cfg = by_dir.get(app_dir)
        if cfg is None:
            continue

        if cfg["type"] == "cppdbg":
            env = {e["name"]: _expand(e["value"]) for e in cfg.get("environment", [])}
            command = [_expand(cfg["program"])]
        else:
            env = {k: _expand(v) for k, v in (cfg.get("env") or {}).items()}
            command = ["dotnet", _expand(cfg["program"])]

        key = app["appID"]
        services[key] = {
            "key": key,
            "name": cfg["name"],
            "command": command,
            "cwd": _expand(cfg["cwd"]),
            "env": env,
            "dapr": _daprd_args(app, resources),
            "app_port": int(app["appPort"]),
            "dotnet": cfg["type"] != "cppdbg",
        }
    return services


def _in_order(services: dict[str, dict]) -> list[str]:
    """Known services in start order, with anything not named in ORDER appended."""
    known = [k for k in ORDER if k in services]
    return known + [k for k in services if k not in known]


def _pid(path: Path) -> int | None:
    """Returns the pid recorded in `path` if that process is still alive."""
    try:
        pid = int(path.read_text().strip())
        os.kill(pid, 0)
        return pid
    except (OSError, ValueError):
        return None


def _listening(port: int) -> bool:
    with socket.socket() as sock:
        sock.settimeout(0.3)
        return sock.connect_ex(("127.0.0.1", port)) == 0


def _stop_pid_file(path: Path, timeout: float = 10.0) -> bool:
    """SIGTERM, wait, then SIGKILL. Waiting matters: daprd holds its ports until it is really gone,
    and the next start dies with "could not listen on any endpoint" if we do not."""
    pid = _pid(path)
    path.unlink(missing_ok=True)
    if pid is None:
        return False
    os.kill(pid, signal.SIGTERM)
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if _pid_dead(pid):
            return True
        time.sleep(0.1)
    try:
        os.kill(pid, signal.SIGKILL)
    except OSError:
        pass
    return True


def _pid_dead(pid: int) -> bool:
    try:
        os.kill(pid, 0)
    except OSError:
        return True
    # A child we spawned ourselves stays a zombie until reaped; that still counts as gone.
    try:
        state = Path(f"/proc/{pid}/stat").read_text().split(") ", 1)[1][0]
        return state == "Z"
    except (OSError, IndexError):
        return True


def _daprd_bin() -> str:
    from shutil import which

    found = which("daprd")
    if found:
        return found
    fallback = Path.home() / ".dapr" / "bin" / "daprd"
    if fallback.is_file():
        return str(fallback)
    sys.exit("daprd not found in PATH or ~/.dapr/bin")


def start(service: dict, wait: float = 90.0) -> None:
    key = service["key"]
    PID_DIR.mkdir(parents=True, exist_ok=True)
    app_pid_file = PID_DIR / f"{key}.app.pid"
    dapr_pid_file = PID_DIR / f"{key}.pid"

    if _pid(app_pid_file) and _pid(dapr_pid_file):
        print(f"  {key:16} already running")
        return
    if _listening(service["app_port"]):
        sys.exit(f"port {service['app_port']} is taken but {key} is not ours — stop it first")

    if not _pid(dapr_pid_file):
        _stop_pid_file(dapr_pid_file)
        with (PID_DIR / f"{key}.log").open("wb") as log:
            proc = subprocess.Popen(
                [_daprd_bin(), *service["dapr"]], stdout=log, stderr=subprocess.STDOUT,
                start_new_session=True)
        dapr_pid_file.write_text(str(proc.pid))

    env = {**os.environ, **service["env"]}
    with (PID_DIR / f"{key}.app.log").open("wb") as log:
        proc = subprocess.Popen(
            service["command"], cwd=service["cwd"], env=env, stdout=log,
            stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, start_new_session=True)
    app_pid_file.write_text(str(proc.pid))

    deadline = time.monotonic() + wait
    while time.monotonic() < deadline:
        if _listening(service["app_port"]):
            print(f"  {key:16} up on {service['app_port']} (pid {proc.pid})")
            return
        if _pid_dead(proc.pid):
            tail = (PID_DIR / f"{key}.app.log").read_text().splitlines()[-15:]
            sys.exit(f"{key} died on startup:\n  " + "\n  ".join(tail))
        time.sleep(0.3)
    sys.exit(f"{key} did not open port {service['app_port']} within {wait:.0f}s")


def stop(service: dict) -> None:
    key = service["key"]
    stopped = _stop_pid_file(PID_DIR / f"{key}.app.pid")
    stopped |= _stop_pid_file(PID_DIR / f"{key}.pid")
    print(f"  {key:16} {'stopped' if stopped else 'was not running'}")


def build(services: list[dict]) -> None:
    if any(s["dotnet"] for s in services):
        print("building web_vms.sln ...")
        subprocess.run(["dotnet", "build", str(ROOT / "web_vms" / "web_vms.sln"), "-v", "q",
                        "--nologo"], check=True)
    if any(not s["dotnet"] for s in services):
        print("building media_server ...")
        subprocess.run(["cmake", "--build", str(ROOT / "media_server" / "build"), "--target",
                        "vms"], check=True)


def status(services: dict[str, dict]) -> None:
    for key in _in_order(services):
        service = services[key]
        app = _pid(PID_DIR / f"{key}.app.pid")
        dapr = _pid(PID_DIR / f"{key}.pid")
        port = "listening" if _listening(service["app_port"]) else "closed"
        print(f"  {key:16} app={app or '-':<8} daprd={dapr or '-':<8} "
              f"{service['app_port']} {port}")


def main() -> None:
    argv = sys.argv[1:]
    if not argv:
        sys.exit(__doc__)
    action, argv = argv[0], argv[1:]
    do_build = "--build" in argv
    names = [a for a in argv if not a.startswith("-")]

    services = _services()
    if not services:
        sys.exit(f"no services found — {RUN_FILE.name} and .vscode/launch.json do not line up "
                 f"on any project directory")
    unknown = [n for n in names if n not in services]
    if unknown:
        sys.exit(f"unknown service(s): {', '.join(unknown)}\nknown: {', '.join(services)}")
    chosen = [services[k] for k in (names or _in_order(services))]

    if action == "status":
        status(services)
    elif action == "up":
        if do_build:
            build(chosen)
        print("starting:")
        for service in chosen:
            start(service)
    elif action == "down":
        print("stopping:")
        for service in reversed(chosen):
            stop(service)
    elif action == "restart":
        if not names:
            sys.exit("restart needs at least one service name")
        print("stopping:")
        for service in reversed(chosen):
            stop(service)
        if do_build:
            build(chosen)
        print("starting:")
        for service in chosen:
            start(service)
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()
