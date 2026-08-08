#!/usr/bin/env python3
"""Bring the dev stack up, down or back up again without VS Code.

Ports, environment and daprd flags are read from `.vscode/launch.json` and `.vscode/tasks.json` —
the very files F5 uses — so this script cannot drift away from the IDE. It also shares their pid
files and process names, which is what lets the two be mixed: a stack started here can be stopped
by F5's "kill stale" tasks and the other way round.

Infrastructure (postgres, redis, mediamtx, dapr placement) is not managed here; it lives in docker
compose and is expected to be up already.

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
import shlex
import signal
import socket
import subprocess
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PID_DIR = Path("/tmp/web_vms-dapr")

# Start order: config first (everyone resolves cameras and storages through it), then the leaf
# services, then the media server, then the gateway that fans out to all of them.
ORDER = [
    "vmscfg",
    "vmslogger",
    "vmsutils",
    "vmsonvif",
    "vmsonvif-actors",
    "vmsfs",
    "vmsanalytics",
    "vms",
    "web_vms",
]


def _load_jsonc(path: Path) -> dict:
    """Parses a .vscode JSON file, dropping whole-line // comments (never inline — URLs use //)."""
    text = re.sub(r"^\s*//.*$", "", path.read_text(), flags=re.M)
    return json.loads(text)


def _expand(value: str) -> str:
    return value.replace("${workspaceFolder}", str(ROOT))


def _services() -> dict[str, dict]:
    """Builds the service table out of the launch configurations and their dapr tasks."""
    launch = _load_jsonc(ROOT / ".vscode" / "launch.json")
    tasks = _load_jsonc(ROOT / ".vscode" / "tasks.json")

    dapr_args: dict[str, list[str]] = {}
    for task in tasks["tasks"]:
        match = re.fullmatch(r"dapr:(.+):start", task.get("label", ""))
        if not match:
            continue
        script = task["args"][1]
        args = re.search(r'nohup "\$daprd_bin" (.*?) > "\$log_file"', script)
        if args:
            dapr_args[match.group(1)] = [_expand(a) for a in shlex.split(args.group(1))]

    services: dict[str, dict] = {}
    for cfg in launch["configurations"]:
        prepare = cfg.get("preLaunchTask", "")
        if not prepare.startswith("prepare:"):
            continue
        key = prepare[len("prepare:"):]
        if key not in dapr_args:
            continue

        if cfg["type"] == "cppdbg":
            env = {e["name"]: _expand(e["value"]) for e in cfg.get("environment", [])}
            command = [_expand(cfg["program"])]
        else:
            env = {k: _expand(v) for k, v in (cfg.get("env") or {}).items()}
            command = ["dotnet", _expand(cfg["program"])]

        args = dapr_args[key]
        services[key] = {
            "key": key,
            "name": cfg["name"],
            "command": command,
            "cwd": _expand(cfg["cwd"]),
            "env": env,
            "dapr": args,
            "app_port": int(args[args.index("-app-port") + 1]),
            "dotnet": cfg["type"] != "cppdbg",
        }
    return services


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
    for key in ORDER:
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
    unknown = [n for n in names if n not in services]
    if unknown:
        sys.exit(f"unknown service(s): {', '.join(unknown)}\nknown: {', '.join(services)}")
    selected = names or ORDER
    chosen = [services[k] for k in selected]

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
