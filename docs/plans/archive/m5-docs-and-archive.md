# M5 — документация и архив

> Часть плана [Square → ta_vms](../square-merge/README.md); ссылки «§N» ведут в его разделы.
> Файлы — ориентиры, не полный перечень.
> **Закрыт 2026-11-18.**

## Сделано (2026-11-18)

- **`docs/maps-and-plans.md` разбит по темам.** В нём остались модель, где что лежит, плитки и
  события (`map-config-changed`) — и оглавление остального; рядом `docs/maps/`: `devices.md`,
  `control.md`, `states-and-alarms.md`, `demo.md`, `calibration.md`. Раздел «Next stages» ушёл в
  `docs/future_plans/25-ptz-mounts-and-self-guidance.md`. Текст перенесён дословно.
- **`docs/map-objects-and-events.md` отдельным файлом не нужен:** модель — в `maps-and-plans.md`,
  состояния, журнал и права — в `docs/maps/states-and-alarms.md` (там же то, что раньше было
  разделом `CLAUDE.md`).
- **`CLAUDE.md` урезан до правил, которые действуют всегда:** 52,6 → 23 КБ. Обоснования и
  частности (ta-deps, граница `roitrc/ta_roienc`, многомашинная раскладка, Keycloak, реестр entity state,
  создание БД) — в `docs/architecture/`; инварианты карты из `CLAUDE.md` убраны как дословно
  повторяющие `maps-and-plans.md`. Добавлен `docs/README.md` — индекс документации.
- **`README.md` плана урезан:** 68 → 40 КБ. §1–§3 (контекст, вердикт, что берём из Square) —
  `docs/plans/archive/history-context-verdict.md`; таблица майлстоунов — ссылки, без критериев.
- **Закрытые милстоуны и планы сессии 24** — в `docs/plans/archive/`.
- **§4 README — снимок решения, а не описание кода.** §4 переехал целиком в
  [`archive/target-architecture.md`](target-architecture.md), README вместо него ссылается на
  `docs/maps-and-plans.md` и `docs/maps/*.md` (README — 12,5 КБ). Сверка с кодом нашла, где код
  пошёл дальше снимка 2026-10-28:
  - схема мигрирует файлом `0000_init.sql`, а не «миграцией 0001»;
  - общий мигратор вынесен в `dotnet/VmsDb` — §4.1 предполагал это только как повод, теперь это
    проект, который подключают четыре сервиса;
  - `CONNECTED`/`DISCONNECTED` берутся из открытых обрывов источника в VmsFs
    (`VmsFsCameraConnectionSource`), а не из путей MediaMTX через VmsUtils;
  - поллер `Watch` диффует по `xmin`, а не по `Version` (§4.5);
  - модель обросла тем, чего в §4 не было: `device_node`, `device_info`, `device_diagram*`,
    `rights_mode`, `alarms`, `map_outbox`, `demo_*`; они описаны в `docs/maps/*.md`.
- **Архивация Square.** README обоих репозиториев получил пометку «архив» — коммиты `a5300e2`
  (`Square`) и `9f5c974` (`SquareIntegrationClient`); GitHub-архив включён 2026-11-18. Схема
  Square удалена из `../multitenant_admin`: `mt_admin/init_files/initdb/` (`init-all.sh` и 15
  SQL-файлов), копирование в `DbAdmin.csproj` и `ApplySqlScriptsAsync`; база тенанта создаётся
  пустой, а `ta_map` мигрирует сам `VmsMap` — коммит `66fff82`, ветка
  `square-merge/m5-docs-and-archive` (от `main`). `dotnet build` DbAdmin и mt_admin.Server
  проходит.
