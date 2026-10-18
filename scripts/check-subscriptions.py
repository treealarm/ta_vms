#!/usr/bin/env python3
"""Reports who is subscribed to what on the Dapr bus, from live state only.

Why this exists: a subscription can be missing without anything looking broken. Services
subscribe once, at startup (see VmsOnvif.CameraConfigSubscriber and friends), so anything that
drops the stream afterwards leaves the sidecar running, the app running and video playing — while
config changes silently reach nobody. Editing a component file under a running stack does exactly
that: Dapr hot-reloads the component, the streaming subscriptions go away, and only restarting the
apps brings them back.

Nothing here hardcodes who *should* subscribe to what: such a list rots, and a wrong one is worse
than none. It reports what the sidecars say they subscribe to, what the broker actually has, and
where the two disagree.

Usage:
    scripts/check-subscriptions.py            # uses dotnet/dapr.yaml and .env
    scripts/check-subscriptions.py --json     # machine-readable

Exits 1 if anything is off (a subscription with no queue, a queue with no consumer, an orphan
queue), so it can gate a smoke test.
"""

import argparse
import base64
import json
import sys
import urllib.error
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
RUN_FILE = REPO / "dotnet" / "dapr.yaml"
ENV_FILE = REPO / ".env"


def read_env():
    """Only the few keys needed here, without pulling in a dotenv dependency."""
    values = {}
    if ENV_FILE.exists():
        for line in ENV_FILE.read_text().splitlines():
            line = line.strip()
            if not line or line.startswith("#") or "=" not in line:
                continue
            key, _, value = line.partition("=")
            values[key.strip()] = value.strip().strip('"')
    return values


def read_apps():
    """[(app id, dapr http port)] from the multi-app run file.

    Hand-rolled rather than importing yaml: this should run from a bare checkout with nothing
    installed, and the two keys it needs are one line each.
    """
    apps, app_id, port = [], None, None
    for raw in RUN_FILE.read_text().splitlines():
        line = raw.strip()
        if line.startswith("- appID:"):
            if app_id and port:
                apps.append((app_id, port))
            app_id, port = line.split(":", 1)[1].strip(), None
        elif line.startswith("daprHTTPPort:") and app_id:
            port = int(line.split(":", 1)[1].strip())
    if app_id and port:
        apps.append((app_id, port))
    return apps


def sidecar_topics(port):
    """Topics this sidecar is subscribed to, or None when it is not answering."""
    try:
        with urllib.request.urlopen(f"http://localhost:{port}/v1.0/metadata", timeout=3) as resp:
            meta = json.load(resp)
    except (urllib.error.URLError, OSError, json.JSONDecodeError):
        return None
    return sorted(s.get("topic", "?") for s in (meta.get("subscriptions") or []))


def app_is_up(port):
    """Whether the sidecar's own app is running.

    A sidecar's /v1.0/healthz answers 204 only once its app is up and 500 while it is still
    waiting for one — the same signal the 'wait: stack ready' task uses. Without this a service
    that was never started and a service whose subscription was dropped look identical from the
    outside, and only one of the two is a fault.
    """
    try:
        with urllib.request.urlopen(f"http://localhost:{port}/v1.0/healthz", timeout=3) as resp:
            return resp.status in (200, 204)
    except urllib.error.HTTPError:
        return False
    except (urllib.error.URLError, OSError):
        return False


def broker_queues(env):
    """queue name -> {consumers, messages}, through the RabbitMQ management API."""
    port = env.get("RABBITMQ_MANAGEMENT_PORT", "15672")
    user = env.get("RABBITMQ_DEFAULT_USER", "vms")
    password = env.get("RABBITMQ_DEFAULT_PASS", "vms")
    request = urllib.request.Request(f"http://localhost:{port}/api/queues")
    token = base64.b64encode(f"{user}:{password}".encode()).decode()
    request.add_header("Authorization", f"Basic {token}")
    try:
        with urllib.request.urlopen(request, timeout=5) as resp:
            return {
                q["name"]: {"consumers": q.get("consumers", 0), "messages": q.get("messages", 0)}
                for q in json.load(resp)
            }
    except (urllib.error.URLError, OSError, json.JSONDecodeError, KeyError):
        return None


def collect():
    """Live state plus the disagreements between the two halves of it.

    A queue with no consumer is only a problem when its service is actually there: the queues are
    declared with deletedWhenUnused=false precisely so that they survive a stopped service and
    keep its events, so a retained queue during a restart is the feature working, not a fault.
    What is a fault is a sidecar that answers and does not claim a queue that exists for it —
    that is the shape of a subscription dropped after startup.
    """
    env = read_env()
    queues = broker_queues(env)
    known = dict(read_apps())
    apps, problems, retained = [], [], []

    for app_id, port in known.items():
        topics = sidecar_topics(port)
        app_up = topics is not None and app_is_up(port)
        entry = {"app_id": app_id, "port": port, "topics": topics,
                 "app_running": app_up, "queues": {}}
        if topics is None:
            entry["state"] = "sidecar down"
        elif not app_up:
            entry["state"] = "app not running"
        else:
            entry["state"] = "subscribed" if topics else "no subscriptions"
        for topic in topics or []:
            # Dapr names the queue <consumerID>-<topic>, and consumerID defaults to the app id.
            name = f"{app_id}-{topic}"
            queue = None if queues is None else queues.get(name)
            entry["queues"][name] = queue
            if queues is None:
                continue
            if queue is None:
                problems.append(f"{app_id}: subscribed to '{topic}', but queue '{name}' does not exist")
            elif queue["consumers"] == 0:
                problems.append(f"{app_id}: queue '{name}' has no consumer — messages will pile up")
        apps.append(entry)

    for name, queue in sorted((queues or {}).items()):
        # Not a partition on the first dash: app ids contain dashes too (web-vms,
        # vmsonvif-actors), so the longest matching app id wins.
        app_id = max((a for a in known if name.startswith(f"{a}-")), key=len, default=None)
        if app_id is None:
            problems.append(f"queue '{name}' belongs to no app id in {RUN_FILE.name}")
            continue
        topic = name[len(app_id) + 1:]
        claimed = any(name in entry["queues"] for entry in apps)
        if claimed:
            continue
        owner = next(e for e in apps if e["app_id"] == app_id)
        if owner["app_running"]:
            problems.append(
                f"{app_id}: queue '{name}' exists but the sidecar does not subscribe to '{topic}' "
                f"— dropped subscription, restart the app ({queue['messages']} message(s) waiting)")
        else:
            retained.append((name, queue))

    return apps, queues, problems, retained


def main():
    parser = argparse.ArgumentParser(description="Report Dapr pub/sub subscriptions and queues.")
    parser.add_argument("--json", action="store_true", help="machine-readable output")
    args = parser.parse_args()

    apps, queues, problems, retained = collect()

    if args.json:
        print(json.dumps(
            {"apps": apps, "queues": queues, "problems": problems,
             "retained": [{"queue": n, **q} for n, q in retained]}, indent=2))
        return 1 if problems else 0

    if queues is None:
        print("! RabbitMQ management API unreachable — reporting sidecars only\n")

    print(f"{'app-id':24} {'port':6} subscriptions")
    print("-" * 78)
    for entry in apps:
        if entry["topics"] is None:
            detail = "[sidecar down]"
        elif not entry["app_running"]:
            detail = "[app not running]"
        elif not entry["topics"]:
            detail = "- none -"
        else:
            parts = []
            for topic in entry["topics"]:
                queue = entry["queues"].get(f"{entry['app_id']}-{topic}")
                parts.append(topic if queue is None else f"{topic} ({queue['consumers']} consumer)")
            detail = ", ".join(parts)
        print(f"{entry['app_id']:24} {entry['port']:<6} {detail}")

    if retained:
        print("\nqueues retained for services that are down (this is what durable queues are for):")
        for name, queue in retained:
            print(f"  {name:52} {queue['messages']} message(s) waiting")

    if problems:
        print("\nproblems:")
        for problem in problems:
            print(f"  ! {problem}")
        return 1

    print("\nNo problems found.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
