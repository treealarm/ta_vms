#!/usr/bin/env python3
"""Bring the dev stack up, down or back up again without VS Code.

Everything is read from the files F5 itself uses, so this cannot drift away from the IDE:
`dotnet/dapr.yaml` gives each sidecar its app id and ports, `.vscode/launch.json` gives the
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
    dev_stack.py wait-sidecars [seconds]
    dev_stack.py stop-strays
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
RUN_FILE = ROOT / "dotnet" / "dapr.yaml"
VITE_CONFIG = ROOT / "dotnet" / "web_srv.client" / "vite.config.ts"

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
    "vmsptztrackingsingleton",
    "vms",
    "web-vms",
]

# The second node (its own video_srv, VmsFs, VmsOnvif and actor host, pinned to server id
# "vms-2") used to be startable with --two-nodes. Its launch configurations were removed from
# .vscode/launch.json, so there is nothing left to start; bringing it back means adding those
# configurations and matching entries in dotnet/dapr.yaml, after which they appear here on their
# own. Sharding work needs it — a single node cannot show a camera handed from one to another.


def _load_jsonc(path: Path) -> dict:
    """Parses a .vscode JSON file, dropping whole-line // comments (never inline — URLs use //)."""
    text = re.sub(r"^\s*//.*$", "", path.read_text(), flags=re.M)
    return json.loads(text)


def _expand(value: str) -> str:
    return value.replace("${workspaceFolder}", str(ROOT))


def _run_file() -> tuple[list[dict], Path]:
    """The apps declared in dotnet/dapr.yaml, plus the resources path they share.

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

    The join key is the project directory: dapr.yaml's appDirPath is relative to dotnet/, and a
    launch configuration's cwd points at the same place. Nothing else ties the two files together
    — app ids do not appear in launch.json for the .NET services.
    """
    launch = _load_jsonc(ROOT / ".vscode" / "launch.json")
    apps, resources = _run_file()

    by_dir: dict[Path, dict] = {}
    for cfg in launch["configurations"]:
        # Two configurations share video_srv/: the Windows one only differs in how it is
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
        print("building ta_vms.sln ...")
        subprocess.run(["dotnet", "build", str(ROOT / "dotnet" / "ta_vms.sln"), "-v", "q",
                        "--nologo"], check=True)
    if any(not s["dotnet"] for s in services):
        print("building video_srv ...")
        subprocess.run(["cmake", "--build", str(ROOT / "video_srv" / "build"), "--target",
                        "vms"], check=True)


def status(services: dict[str, dict]) -> None:
    for key in _in_order(services):
        service = services[key]
        app = _pid(PID_DIR / f"{key}.app.pid")
        dapr = _pid(PID_DIR / f"{key}.pid")
        port = "listening" if _listening(service["app_port"]) else "closed"
        print(f"  {key:16} app={app or '-':<8} daprd={dapr or '-':<8} "
              f"{service['app_port']} {port}")


def wait_sidecars(timeout: float = 120.0) -> None:
    """Blocks until every sidecar in dapr.yaml answers on its metrics port.

    This is what F5's "wait: sidecars ready" runs, and waiting for *every* sidecar is the whole
    point of it. It used to curl one port -- web-vms on 9190 -- which is the first app in the run
    file and therefore almost always the first one up. The gate opened while the other twelve were
    still starting, the debugger launched the applications anyway, and whichever of them beat its
    own sidecar failed to reach Dapr. The symptom was a stack that "sometimes does not come up",
    and, for the service whose sidecar lost the race, a connection refused on its app port.

    The metrics port is the right thing to probe: daprd serves it itself, so it answers whether or
    not the application behind it has started -- and the applications are started after this, by
    the debugger.

    Ports come from dapr.yaml, so a service added there is waited for without touching this file.
    """
    apps, _ = _run_file()
    targets = [(a["appID"], int(a["metricsPort"])) for a in apps if a.get("metricsPort")]
    if not targets:
        sys.exit(f"no sidecars with a metricsPort found in {RUN_FILE}")

    deadline = time.monotonic() + timeout
    reported = 0.0
    while True:
        missing = [name for name, port in targets if not _listening(port)]
        if not missing:
            print(f"sidecars ready: {len(targets)}/{len(targets)}")
            return
        if time.monotonic() >= deadline:
            sys.exit(f"sidecars not ready after {timeout:.0f}s: {', '.join(missing)}\n"
                     f"run 'dapr stop -f .' in dotnet/ and try again, or check that terminal's log")
        # One line every few seconds instead of one per probe: the old curl retried sixty times
        # with --show-error and printed a failure line for each, which read like sixty errors
        # rather than one wait in progress.
        now = time.monotonic()
        if now - reported >= 5.0:
            reported = now
            print(f"waiting for {len(missing)}/{len(targets)}: {', '.join(missing)}", flush=True)
        time.sleep(0.5)


def _vite_port() -> int | None:
    """The dev server's port, read from vite.config.ts so the two cannot drift apart."""
    match = re.search(r"^\s*port:\s*(\d+)", VITE_CONFIG.read_text(), re.M)
    return int(match.group(1)) if match else None


def _listen_inodes() -> dict[int, int]:
    """TCP port -> socket inode, for every listening socket on the machine."""
    ports: dict[int, int] = {}
    for family in ("tcp", "tcp6"):
        try:
            rows = Path(f"/proc/net/{family}").read_text().splitlines()[1:]
        except OSError:
            continue
        for row in rows:
            fields = row.split()
            # st 0A is TCP_LISTEN; local_address is host:port, both hexadecimal.
            if len(fields) > 9 and fields[3] == "0A":
                ports[int(fields[1].split(":")[1], 16)] = int(fields[9])
    return ports


def _pids_by_inode() -> dict[int, set[int]]:
    """Socket inode -> the pids holding it. A set, because a forked child shares its parent's
    descriptors and the port stays open while either of them lives."""
    holders: dict[int, set[int]] = {}
    for entry in Path("/proc").iterdir():
        if not entry.name.isdigit():
            continue
        try:
            for fd in (entry / "fd").iterdir():
                link = os.readlink(fd)
                if link.startswith("socket:["):
                    holders.setdefault(int(link[8:-1]), set()).add(int(entry.name))
        except OSError:
            continue  # exited between iterdir and readlink, or belongs to another user
    return holders


def _cmdline(pid: int) -> str:
    try:
        raw = Path(f"/proc/{pid}/cmdline").read_bytes().decode(errors="replace")
    except OSError:
        return ""
    return " ".join(part for part in raw.split("\0") if part)


def _is_ours(pid: int) -> bool:
    """Whether this process belongs to the checkout.

    Everything F5 starts names the workspace on its command line: `dotnet <root>/dotnet/.../X.dll`,
    the two C++ binaries by absolute path, vite through <root>/.../node_modules, and daprd through
    its -resources-path. The working directory is the fallback for anything that does not.
    """
    root = str(ROOT)
    if root in _cmdline(pid):
        return True
    try:
        return os.readlink(f"/proc/{pid}/cwd").startswith(root)
    except OSError:
        return False


def _await_gone(busy: dict[int, int], wanted: dict[int, str], pids: list[int],
                timeout: float = 15.0) -> None:
    """Blocks until every port in `busy` is free and every process in `pids` is gone, escalating to
    SIGKILL once before giving up.

    Waiting is the whole point: a dying daprd holds its listeners until it is really gone, and the
    step after this opens exactly those ports.
    """
    def remaining() -> list[str]:
        listening = _listen_inodes()
        return ([f"{port} ({wanted[port]})" for port in busy if port in listening]
                + [f"pid {pid}" for pid in pids if not _pid_dead(pid)])

    deadline = time.monotonic() + timeout
    left = remaining()
    while left and time.monotonic() < deadline:
        time.sleep(0.2)
        left = remaining()
    if not left:
        return

    for pid in pids:
        if not _pid_dead(pid):
            try:
                os.kill(pid, signal.SIGKILL)
            except OSError:
                pass
    time.sleep(1.0)
    if (left := remaining()):
        sys.exit("stop-strays: still there after SIGKILL: " + ", ".join(left))
    print("  (needed SIGKILL)")


def _stray_dapr_cli() -> list[int]:
    """Leftover multi-app `dapr run` processes belonging to this checkout.

    Looked up by identity rather than by port, because it holds none -- which is exactly why it
    survives everything else. VS Code's Stop ends the debug sessions, each one's postDebugTask runs
    `dapr stop --app-id X`, the sidecars die one by one, and their supervisor is left alive with
    nothing to supervise. VS Code still counts 'dapr: sidecars up' as a running background task, so
    the next F5 never starts a new one and the stack simply does not come up -- with every port
    free and nothing to point at. Observed.
    """
    found: list[int] = []
    root = str(ROOT)
    for entry in Path("/proc").iterdir():
        if not entry.name.isdigit():
            continue
        pid = int(entry.name)
        try:
            if Path(os.readlink(f"/proc/{pid}/exe")).name != "dapr":
                continue
            if "run" not in _cmdline(pid).split():
                continue
            if not os.readlink(f"/proc/{pid}/cwd").startswith(root):
                continue
        except OSError:
            continue
        found.append(pid)
    return found


def stop_strays() -> None:
    """Frees every port the stack is about to need, and refuses to start if it cannot.

    `dapr stop -f .` ends the sidecars, and nothing ends the applications: VS Code does not stop a
    debug session when a new one starts, and a session that was interrupted rather than stopped
    leaves its processes running. Either way the next F5 launches a service whose port is already
    taken -- which surfaces much later as a service that "did not come up", not as a port message.

    A port is only cleared when its holder is recognisably ours. Anything else is reported and
    fails the run: silently killing a stranger's process because it sits on 5432 or 5173 would be
    a far worse outcome than not starting.
    """
    if not Path("/proc/net/tcp").exists():
        print("stop-strays: needs /proc, so nothing was swept -- close the old run's terminals by hand")
        return

    apps, _ = _run_file()
    wanted: dict[int, str] = {}
    for app in apps:
        app_id = app["appID"]
        for field, role in (("appPort", "app"), ("daprHTTPPort", "sidecar http"),
                            ("daprGRPCPort", "sidecar grpc"), ("metricsPort", "sidecar metrics"),
                            ("daprInternalGRPCPort", "sidecar internal")):
            if field in app:
                wanted[int(app[field])] = f"{app_id} {role}"
    vite = _vite_port()
    if vite:
        wanted[vite] = "frontend (vite)"

    def survey() -> tuple[dict[int, int], dict[int, list[str]], list[tuple[int, set[int]]]]:
        """(busy ports, pids to signal with what they hold, ports whose holder is not ours)."""
        inodes = _listen_inodes()
        busy = {port: inodes[port] for port in wanted if port in inodes}
        holders = _pids_by_inode()
        victims: dict[int, list[str]] = {}
        unknown: list[tuple[int, set[int]]] = []
        for port, inode in sorted(busy.items()):
            pids = holders.get(inode, set())
            mine = {pid for pid in pids if _is_ours(pid)}
            if not mine:
                unknown.append((port, pids))
            for pid in mine:
                victims.setdefault(pid, []).append(f"{port} ({wanted[port]})")
        return busy, victims, unknown

    # The whole picture is read before anything is signalled, and this ordering is the point: the
    # refusal below has to leave a running stack exactly as it found it. The first version of this
    # ended the leftover `dapr run` up front -- so a foreign port made it take every sidecar down
    # on its way to announcing that it had changed nothing.
    clis = _stray_dapr_cli()
    busy, victims, unknown = survey()
    if unknown:
        # A holder that cannot be named is usually one that is on its way out: /proc/net/tcp is
        # read before the descriptor scan, so a socket closed in between looks exactly like a
        # stranger's. Ask once more before refusing to start over it.
        time.sleep(0.5)
        busy, victims, unknown = survey()
    if unknown:
        print("stop-strays: refusing to start, these ports are held by processes that are not ours:")
        for port, pids in unknown:
            who = ", ".join(f"pid {pid}: {_cmdline(pid)[:80]}" for pid in pids)
            print(f"  {port} ({wanted[port]}) -- {who or 'not visible to this user'}")
        sys.exit(1)

    # The supervisor first: ending it is the graceful way to take its sidecars down with it, and
    # whatever it fails to take down is in `victims` anyway.
    for pid in clis:
        print(f"  ending leftover `dapr run` (pid {pid}) -- holds no port, but keeps the task alive")
        try:
            os.kill(pid, signal.SIGTERM)
        except OSError:
            pass
    for pid, ports in sorted(victims.items()):
        print(f"  killing pid {pid} on {', '.join(ports)}: {_cmdline(pid)[:70]}")
        try:
            os.kill(pid, signal.SIGTERM)
        except OSError:
            pass

    _await_gone(busy, wanted, [*victims, *clis])
    if clis:
        # Gone from /proc is not yet gone for VS Code: it still counts 'dapr: sidecars up' as
        # running until it has handled the exit, and a new instance started before that has been
        # seen to be terminated along with the old one's terminal. A moment costs nothing here.
        time.sleep(2.0)
    if busy or clis:
        print(f"stop-strays: freed {len(busy)} port(s)")
    else:
        print(f"stop-strays: all {len(wanted)} ports free")


def main() -> None:
    argv = sys.argv[1:]
    if not argv:
        sys.exit(__doc__)
    action, argv = argv[0], argv[1:]

    # Answered from dapr.yaml alone: this must work for sidecars that have no launch
    # configuration (VmsSingleton, VmsSquareSingleton), which the join below would drop.
    if action == "wait-sidecars":
        wait_sidecars(float(argv[0]) if argv else 120.0)
        return
    if action == "stop-strays":
        stop_strays()
        return

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
