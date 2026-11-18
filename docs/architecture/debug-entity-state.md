# Debug "entity state" presence registry

> Moved out of `CLAUDE.md`. This is the full statement; the rule itself lives there.

## Debug "entity state" presence registry

For runtime debugging, any service can publish a periodic snapshot of an entity's in-memory
state (e.g. "camera X stream Y is currently recording into file Z") to the `debug-entity-state`
topic on the existing `orderpubsub` pub/sub component. This is a **best-effort, non-persistent**
mechanism — no delivery guarantees, no history, nothing is written to a database.

**Envelope** (raw JSON, one message per entity per tick):
```json
{ "service": "vms", "entity_type": "stream", "entity_id": "cam1/main", "state": { "...": "..." } }
```
- `service` — publisher's `APP_ID`.
- `entity_type` / `entity_id` — free-form identifiers chosen by the publisher.
- `state` — arbitrary JSON object, no fixed schema; each entity publishes whatever fields are
  relevant to it.

**VmsSingleton** owns the in-memory registry: it subscribes to `debug-entity-state`, keys entries by
`(service, entity_type, entity_id)`, and tracks `last_seen`. `stale` is computed on read
(`now - last_seen > 30s`); a periodic sweep removes entries not seen for `> 5min`. The registry is
exposed via the `ListEntityStates` gRPC (consumed by `web_srv.Server`'s `/api/entitystate`
endpoint and the "Entity State" debug tab in the frontend).

**Publishing:**
- C# services: call `builder.AddVmsDebugState()` (from `VmsLog`), then inject
  `EntityStatePublisher` and call `Publish(entityType, entityId, state)`.
- C++ services: use `entity_state_publisher` (in `video_srv/grpc_layer`) which wraps
  `dapr_publisher::publish_json`.
