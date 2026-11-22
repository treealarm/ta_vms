#!/usr/bin/env python3
"""The configuration primitives a world is made of, read from the two databases.

The GUI parity run needs a coverage gate, not a byte comparison: a hand-built world has random
ids, click-placed coordinates and fewer rows than the demo. This script reduces both worlds to a
set of descriptors - "what kind of thing, related how" - and checks that everything the demo
contains has at least one GUI-built equivalent.

    parity_inventory.py snapshot --label demo --out out/demo.json
    parity_inventory.py snapshot --label parity --prefix parity --out out/parity.json
    parity_inventory.py compare out/demo.json out/parity.json

Only SELECT statements are sent; the session is read-only. Runtime data (states, values, alarms,
events, tracks) is deliberately not part of the inventory.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[3]
OUT = Path(__file__).resolve().parent / "out"

PSQL = [
    "docker", "compose", "exec", "-T",
    "-e", "PGOPTIONS=-c default_transaction_read_only=on",
    "postgres", "psql", "-U", "postgres", "-At", "-F", "\t", "-v", "ON_ERROR_STOP=1",
]

MAP_SQL = """
SELECT 'space', json_build_object(
  'kind', s.kind,
  'root', o.parent_id IS NULL,
  'parent_kind', p.kind,
  'plan', sp.space_id IS NOT NULL,
  'footprint', EXISTS (SELECT 1 FROM geo_objects g WHERE g.id = s.object_id)
)::text
FROM spaces s
JOIN objects o ON o.id = s.object_id
LEFT JOIN spaces p ON p.object_id = o.parent_id
LEFT JOIN space_plan sp ON sp.space_id = s.object_id

UNION ALL
SELECT 'figure', json_build_object(
  'geom', GeometryType(g.figure),
  'canvas', CASE
    WHEN g.canvas_id IS NULL THEN 'none'
    WHEN cs.object_id IS NOT NULL THEN 'space'
    WHEN cd.id IS NOT NULL THEN 'diagram'
    ELSE 'other' END,
  'space', s.object_id IS NOT NULL,
  'placement', cp.object_id IS NOT NULL,
  'diagram', dg.id IS NOT NULL,
  'device_diagram', dd.object_id IS NOT NULL,
  'device_node', dn.object_id IS NOT NULL,
  'replica', o.owner_id IS NOT NULL,
  'image', v.image IS NOT NULL,
  'text', coalesce(v.text, '') <> '',
  'colour', coalesce(v.color, '') <> '',
  'radius', coalesce(g.radius, 0) > 0
)::text
FROM geo_objects g
JOIN objects o ON o.id = g.id
LEFT JOIN spaces cs ON cs.object_id = g.canvas_id
LEFT JOIN diagrams cd ON cd.id = g.canvas_id
LEFT JOIN spaces s ON s.object_id = g.id
LEFT JOIN camera_placement cp ON cp.object_id = g.id
LEFT JOIN diagrams dg ON dg.id = g.id
LEFT JOIN device_diagram dd ON dd.object_id = g.id
LEFT JOIN device_node dn ON dn.object_id = g.id
LEFT JOIN visual v ON v.object_id = g.id

UNION ALL
SELECT 'diagram', json_build_object(
  'own_canvas', d.geometry IS NOT NULL AND nullif(d.region_id, '') IS NULL,
  'in_region', nullif(d.region_id, '') IS NOT NULL,
  'parent_kind', CASE
    WHEN ps.object_id IS NOT NULL THEN 'space'
    WHEN pd.id IS NOT NULL THEN 'diagram'
    ELSE 'other' END
)::text
FROM diagrams d
JOIN objects o ON o.id = d.id
LEFT JOIN spaces ps ON ps.object_id = o.parent_id
LEFT JOIN diagrams pd ON pd.id = o.parent_id

UNION ALL
SELECT 'diagram_typed', json_build_object(
  'own_canvas', d.geometry IS NOT NULL AND nullif(d.region_id, '') IS NULL,
  'in_region', nullif(d.region_id, '') IS NOT NULL,
  'parent_kind', CASE
    WHEN ps.object_id IS NOT NULL THEN 'space'
    WHEN pd.id IS NOT NULL THEN 'diagram'
    ELSE 'other' END
)::text
FROM diagrams d
JOIN objects o ON o.id = d.id
LEFT JOIN spaces ps ON ps.object_id = o.parent_id
LEFT JOIN diagrams pd ON pd.id = o.parent_id
WHERE d.dgr_type IS NOT NULL

UNION ALL
SELECT 'diagram_type', json_build_object(
  'background', t.file_id IS NOT NULL,
  'regions', EXISTS (SELECT 1 FROM diagram_type_regions r WHERE r.diagram_type_id = t.id),
  'bindings', EXISTS (SELECT 1 FROM diagram_type_parts p WHERE p.diagram_type_id = t.id),
  'source_camera', coalesce(t.source_kind = 'camera', false),
  'auto_attach', t.auto_attach
)::text
FROM diagram_types t

UNION ALL
SELECT 'placement', json_build_object(
  'reference', cp.is_reference,
  'space_kind', s.kind,
  'view', cp.hfov_deg > 0 AND cp.range_m > 0
)::text
FROM camera_placement cp
JOIN objects o ON o.id = cp.object_id
JOIN spaces s ON s.object_id = o.parent_id

UNION ALL
SELECT 'landmark', json_build_object('space_kind', s.kind)::text
FROM landmark l
JOIN objects o ON o.id = l.object_id
JOIN spaces s ON s.object_id = o.parent_id

UNION ALL
SELECT 'ground_observation', '{}'::text FROM ground_observation
UNION ALL
SELECT 'ptz_observation', '{}'::text FROM ptz_geo_observation
UNION ALL
SELECT 'ptz_zoom', '{}'::text FROM ptz_geo_observation WHERE zoom IS NOT NULL

UNION ALL
SELECT 'device_node', json_build_object('part_kind', dn.part_kind)::text
FROM device_node dn

UNION ALL
SELECT 'device_diagram', json_build_object(
  'source_kind', dd.source_kind,
  'typed', dd.diagram_type_id IS NOT NULL,
  'bound_parts', EXISTS (
    SELECT 1 FROM device_diagram_parts p WHERE p.diagram_object_id = dd.object_id)
)::text
FROM device_diagram dd

UNION ALL
SELECT 'device_object_settings', json_build_object(
  'arm_mode', s.arm_mode,
  'trigger_on', s.trigger_on,
  'alarm', s.alarm_enabled
)::text
FROM device_object_settings s

UNION ALL
SELECT 'right', json_build_object(
  'action', r.action,
  'object_kind', s.kind,
  'root', o.parent_id IS NULL
)::text
FROM rights r
JOIN objects o ON o.id = r.object_id
LEFT JOIN spaces s ON s.object_id = r.object_id
"""

CFG_SQL = """
SELECT 'camera', json_build_object(
  'telemetry', c.has_telemetry,
  'geo_from_device', c.geo_from_device
)::text
FROM camera_setup c

UNION ALL
SELECT 'stream', json_build_object(
  'name', s.stream_name,
  'web_rtc', s.web_rtc,
  'recordable', s.recordable
)::text
FROM camera_stream s

UNION ALL
SELECT 'device_settings', json_build_object(
  'kind', d.kind,
  'named', d.name <> '',
  'arm_mode', d.arm_mode,
  'trigger_on', d.trigger_on,
  'alarm', d.alarm_enabled
)::text
FROM device_settings d

UNION ALL
SELECT 'monitor', json_build_object(
  'kind', m.kind,
  'layout', regexp_replace(m.layout, '[0-9]+', 'x', 'g'),
  'slots', EXISTS (SELECT 1 FROM monitor_slot s WHERE s.monitor_id = m.id)
)::text
FROM monitor m

UNION ALL
SELECT 'subscription', json_build_object(
  'event', e.event_name,
  'armed', e.armed_condition,
  'filter_keys', coalesce((SELECT string_agg(k, ',' ORDER BY k)
                           FROM jsonb_object_keys(e.source_filter) k), ''),
  'actions', coalesce((SELECT string_agg(a.kind, ',' ORDER BY a.position)
                       FROM subscription_actions a WHERE a.subscription_id = e.id), '')
)::text
FROM event_subscriptions e

UNION ALL
SELECT 'ptz_binding', json_build_object('method', b.method)::text
FROM ptz_slave_binding b

UNION ALL
SELECT 'tracking', json_build_object(
  'enabled', p.enabled,
  'kinds', p.kinds,
  'sources', p.sources
)::text
FROM ptz_tracking_policy p
"""

EXTRA_SQL = """
SELECT 'free_text_figure', to_json(count(*) > 0)::text
FROM geo_objects g
JOIN spaces cs ON cs.object_id = g.canvas_id
JOIN visual v ON v.object_id = g.id
LEFT JOIN camera_placement cp ON cp.object_id = g.id
LEFT JOIN diagrams d ON d.id = g.id
LEFT JOIN device_node dn ON dn.object_id = g.id
LEFT JOIN objects o ON o.id = g.id
WHERE coalesce(v.text, '') <> '' AND o.owner_id IS NULL
  AND cp.object_id IS NULL AND d.id IS NULL AND dn.object_id IS NULL
  AND NOT EXISTS (SELECT 1 FROM spaces s WHERE s.object_id = g.id);

SELECT 'free_picture_figure', to_json(count(*) > 0)::text
FROM geo_objects g
JOIN spaces cs ON cs.object_id = g.canvas_id
JOIN visual v ON v.object_id = g.id
LEFT JOIN camera_placement cp ON cp.object_id = g.id
LEFT JOIN diagrams d ON d.id = g.id
LEFT JOIN device_node dn ON dn.object_id = g.id
LEFT JOIN objects o ON o.id = g.id
WHERE v.image IS NOT NULL AND o.owner_id IS NULL
  AND cp.object_id IS NULL AND d.id IS NULL AND dn.object_id IS NULL
  AND NOT EXISTS (SELECT 1 FROM spaces s WHERE s.object_id = g.id);

SELECT 'custom_level_assigned', to_json(count(*) > 0)::text
FROM geo_objects g
JOIN levels l ON l.zoom_level = g.zoom_level
WHERE l.zoom_level LIKE :'prefix' || '-%';
"""


def psql(database: str, sql: str) -> list[tuple[str, dict]]:
    result = subprocess.run(
        [*PSQL, "-d", database, "-c", sql],
        cwd=REPO, capture_output=True, text=True,
    )
    if result.returncode != 0:
        sys.exit(f"reading {database} failed:\n{result.stderr.strip()}")
    rows = []
    for line in result.stdout.splitlines():
        if not line.strip():
            continue
        kind, _, payload = line.partition("\t")
        rows.append((kind, json.loads(payload or "{}")))
    return rows


def descriptor(kind: str, attrs: dict) -> str:
    return json.dumps({"entity": kind, **attrs}, sort_keys=True, separators=(",", ":"))


def snapshot(label: str, prefix: str) -> dict:
    descriptors = set()
    for kind, attrs in psql("ta_map", MAP_SQL):
        descriptors.add(descriptor(kind, attrs))
    for kind, attrs in psql("ta_cfg", CFG_SQL):
        descriptors.add(descriptor(kind, attrs))

    extras = {"free_text_figure": False, "free_picture_figure": False,
              "custom_level_assigned": False}
    extra_sql = EXTRA_SQL.replace(":'prefix'", "'" + prefix.replace("'", "''") + "'")
    for kind, value in psql("ta_map", extra_sql):
        if kind in extras:
            extras[kind] = bool(value)

    return {"label": label, "prefix": prefix,
            "descriptors": sorted(descriptors), "extras": extras}


def compare(demo_path: Path, parity_path: Path) -> int:
    demo = json.loads(demo_path.read_text())
    parity = json.loads(parity_path.read_text())
    missing = sorted(set(demo["descriptors"]) - set(parity["descriptors"]))
    extras = parity.get("extras", {})
    failed_extra = [key for key in
                    ("free_text_figure", "free_picture_figure", "custom_level_assigned")
                    if not extras.get(key)]

    if not missing and not failed_extra:
        print(f"parity covers the demo's {len(demo['descriptors'])} descriptors")
        return 0

    for item in missing:
        print("missing:", item)
    for key in failed_extra:
        print("not proved by the parity world:", key)
    return 1


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    snap = sub.add_parser("snapshot")
    snap.add_argument("--label", required=True)
    snap.add_argument("--prefix", default="parity")
    snap.add_argument("--out", type=Path)
    snap.add_argument("--print", action="store_true")

    cmp = sub.add_parser("compare")
    cmp.add_argument("demo", type=Path)
    cmp.add_argument("parity", type=Path)

    args = parser.parse_args()
    if args.command == "snapshot":
        data = snapshot(args.label, args.prefix)
        out = args.out or OUT / f"{args.label}-inventory.json"
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(json.dumps(data, indent=2) + "\n")
        print(f"{len(data['descriptors'])} descriptors -> {out}")
        if args.print:
            for item in data["descriptors"]:
                print(item)
        return

    sys.exit(compare(args.demo, args.parity))


if __name__ == "__main__":
    main()
