# M5 — документация и архив

> Часть плана [Square → ta_vms](README.md); ссылки «§N» ведут в его разделы.
> Файлы — ориентиры, не полный перечень.

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

## Осталось

- **Архивация Square.** Репозитории `../Square` и `treealarm/SquareIntegrationClient` не
  заархивированы; схема Square всё ещё лежит в `../multitenant_admin/mt_admin/init_files/initdb/`.
- **Сверка §4 README с кодом.** Целевая архитектура плана и `docs/maps/*.md` теперь описывают
  одно и то же; §4 — единственное место, где ещё дублируется модель. Решить: оставить как снимок
  решения или сократить до ссылки.
