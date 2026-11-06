# Future plans

Findings that need doing, but not next: each file is one topic, written when it was found, with
what was established at the time and what is still open. They are not commitments and not a
backlog in order; they keep what was learnt from being lost between sessions.

When work on a topic starts, its plan moves into the work itself (a plan file, a PR) and the file
here is deleted or cut down to what is still left.

| File | Topic |
|---|---|
| [camera-connection-state.md](camera-connection-state.md) | `CONNECTED`/`DISCONNECTED` is polled from VmsFs outages; a transition event from video_srv and the producer's `recording_expected` are still open |
| [demo-gui-parity.md](demo-gui-parity.md) | Каждый примитив конфигурации собирается в GUI; демо — проверка этого; состояния и значения — данные владельцев |
| [dev-stack-port-collision.md](dev-stack-port-collision.md) | A fresh `dev-up` can collide with a sidecar left by an earlier run: only app ports are checked, and a failed start leaves its `daprd` behind |
| [devices-and-nodes.md](devices-and-nodes.md) | Канонический узел устройства под сервером, эталон и вид — у реплики; части устройства; что показывать в списке Nodes |
| [device-integrations.md](device-integrations.md) | Камера — единственный тип устройства; радар и другие типы должны интегрироваться «по аналогии», плагинный адаптер — после второго типа |
| [distributed-analytics.md](distributed-analytics.md) | Re-identification and archive search in the database (pgvector), then analytics per node |
| [distributed-hardening.md](distributed-hardening.md) | What a multi-machine deployment still leaves open on the network |
| [event-subscription-signal.md](event-subscription-signal.md) | Правка подписки иногда не доходит до роутера: ни сигнал, ни ключ версии не сработали за 82 с (найдено 2026-11-06) |
| [event-routing-followups.md](event-routing-followups.md) | Что осталось за M2.4: панельная логика охраны, ISA-18.2 сверх ack/close, окна и выдержки подписок, уведомления, корреляция по re-id |
| [fire-alarm-integration.md](fire-alarm-integration.md) | Пожарная сигнализация — не ещё один цифровой вход: всегда на охране, защёлка/reset, зоны и неисправности, cause-and-effect на панели; класс устройства и адаптер панели — после M2.4 |
| [generic-device-placement.md](generic-device-placement.md) | Общая `device_placement` вместо камеро-специфичной `camera_placement`: общие поля типизированы, специфические — в `props jsonb` |
| [log-volume-leftovers.md](log-volume-leftovers.md) | What still fills the logs after the 2026-10-25 fix: a dead camera re-resolved per call, reconnect errors, web_srv outside VmsLog |
| [onvif-live-parameters.md](onvif-live-parameters.md) | Several devices per camera and cross-camera wiring, the UI poll interval as a setting, a fuller match between the demo world and OnvifSim's `Maryland`, the follower's first write after a restart |
| [plan-or-diagram.md](plan-or-diagram.md) | План и диаграмма: настоящая ось — географическая рамка, а не тип сущности; свободные объекты на диаграммах, затем необязательная рамка |
| [review-2026-10-27-leftovers.md](review-2026-10-27-leftovers.md) | What the 2026-10-27 review left open: how an edge-import replay ends, two live checks |
| [scale-bench-100k.md](scale-bench-100k.md) | Полный стенд на 100 000 камер — распределённое развёртывание; что проверяется локально и что осталось |
| [windows-hardware-encoders.md](windows-hardware-encoders.md) | A hardware encoder on Windows without an NVIDIA card: QSV through D3D11 checked, several streams left, AMF, Media Foundation |
