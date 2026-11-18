# Square → ta_vms: карта и события как слой ta_vms

> Рабочий план на много дней, по майлстоунам (решение 2026-10-28). Активные планы лежат в
> `docs/plans/` — в отличие от `docs/future_plans/` («нужно, но не сейчас»); по-русски, как и
> всякий план.
>
> **M0–M5 и все M2.x закрыты (M5 — 2026-11-18).** Их файлы — в [`archive/`](../archive/): что
> решено, что сделано и что осталось. Открытых майлстоунов нет. Живая модель описана
> по-английски: [`docs/maps-and-plans.md`](../../maps-and-plans.md) и файлы рядом с ним в
> [`docs/maps/`](../../maps/).
>
> | Файл | Что в нём |
> |---|---|
> | `README.md` | ссылки на живую модель (§4), список майлстоунов (§5), последствия и допущения (§6–§9). Снимок целевой архитектуры — в [`archive/target-architecture.md`](../archive/target-architecture.md); контекст и вердикт (§1–§3) — в [`archive/history-context-verdict.md`](../archive/history-context-verdict.md) |
> | [`archive/m5-docs-and-archive.md`](../archive/m5-docs-and-archive.md) | M5, документация и архив — закрыт |
> | `archive/` | закрытые майлстоуны, по файлу на каждый; там же история решения |

## 1–3. Контекст, вердикт и что берём из Square

История решения (2026-10-28): разведка, вердикт «объединять» и перечень переносимого из Square —
[`archive/history-context-verdict.md`](../archive/history-context-verdict.md). Всё, что там решено,
уже в коде и описано по-английски: модель и дерево — [`docs/maps-and-plans.md`](../../maps-and-plans.md),
состояния, журнал и права — [`docs/maps/states-and-alarms.md`](../../maps/states-and-alarms.md),
панель оператора — [`docs/maps/control.md`](../../maps/control.md).

## 4. Целевая архитектура

Как система устроена теперь, по-английски: модель, дерево и то, где что лежит, —
[`docs/maps-and-plans.md`](../../maps-and-plans.md); рядом `docs/maps/` — `devices.md`,
`control.md`, `states-and-alarms.md`, `demo.md`, `calibration.md`.

Целевая архитектура в том виде, в каком она была решена 2026-10-28 (сервис, единое дерево, слои
масштаба, адаптер состояний, живые обновления, файлы, права, операторский экран), — снимок в
[`archive/target-architecture.md`](../archive/target-architecture.md). Он сохранён как запись
решения, а не как описание текущего кода; ссылки «§4.N» ниже и в файлах майлстоунов ведут туда.

## 5. Майлстоуны

Каждый майлстоун заканчивается собирающейся системой с проходящими проверками, и у каждого есть
критерий «готово, когда» и отчёт о закрытии — они в файле майлстоуна, а не здесь. Порядок был:
M0 → M1 → (M2, M3 — на выбор, см. §8) → M4 → M2.1 → M2.2 → M2.3 → M5; M2.1 выполнялся после
вливания M4, потому что опирается на его фронтенд, M2.2 — после M2.1 и дерева узлов в админке,
M2.3 — после M2.2, потому что расширяет его демо. Каждый майлстоун шёл на своей ветке
`square-merge/<файл майлстоуна>` с тем же именем во всех затронутых репозиториях (`ta_install`, …).

| M | Название | Файл |
|---|---|---|
| M0 | Демонтаж интеграции Square | [archive/m0-decommission-square.md](../archive/m0-decommission-square.md) |
| M1 | Каркас `VmsMap`: ядро Square, PostGIS, запись | [archive/m1-vmsmap-core.md](../archive/m1-vmsmap-core.md) |
| M2 | Спатиальный модуль в `VmsMap` на едином дереве | [archive/m2-spatial-into-vmsmap.md](../archive/m2-spatial-into-vmsmap.md) |
| M3 | Состояния, события, права, треки, значения, мнемосхемы → web_srv | [archive/m3-states-events-rights.md](../archive/m3-states-events-rights.md) |
| M4 | Фронтенд: симбиоз карт | [archive/m4-frontend.md](../archive/m4-frontend.md) |
| M2.1 | Контейнеры на карте (зоны) и уровни детализации | [archive/m2.1-map-containers.md](../archive/m2.1-map-containers.md) |
| M2.2 | Демо-конфигурация «UMD Data Center» и визуальный редактор типов | [archive/m2.2-demo-configuration.md](../archive/m2.2-demo-configuration.md) |
| M2.3 | Живые параметры ONVIF: геолокация, реле, камера на машине | [archive/m2.3-onvif-live-parameters.md](../archive/m2.3-onvif-live-parameters.md) |
| M2.4 | Датчики, охрана, тревоги и маршрутизация событий (подпланы a–i) | [archive/m2.4-devices-alarms-automation.md](../archive/m2.4-devices-alarms-automation.md) |
| M2.5 | Демо: вторая пара master/slave, стена `UMD DC` и лендмарки | [archive/m2.5-demo-master-slave-and-monitor.md](../archive/m2.5-demo-master-slave-and-monitor.md) |
| M2.6 | Диаграмма устройства: шаблон модели и части в регионах | [archive/m2.6-device-diagrams.md](../archive/m2.6-device-diagrams.md) |
| M2.7 | Демо: дверь в стене и человек, выходящий к дороге | [archive/m2.7-demo-outdoor-walker.md](../archive/m2.7-demo-outdoor-walker.md) |
| M2.8 | Демо у заказчика ставится уже откалиброванным | [archive/m2.8-demo-ships-calibrated.md](../archive/m2.8-demo-ships-calibrated.md) |
| M2.9 | Демо-мир по OpenStreetMap: настоящая геометрия и карта на земле | [archive/m2.9-real-campus-world.md](../archive/m2.9-real-campus-world.md) |
| M2.10 | Свободные реплики частей камеры на планах | [archive/m2.10-part-replicas.md](../archive/m2.10-part-replicas.md) |
| M2.11 | Второе реле на карте и короткие имена демо | [archive/m2.11-alarm-button.md](../archive/m2.11-alarm-button.md) |
| M5 | Документация и архив | [archive/m5-docs-and-archive.md](../archive/m5-docs-and-archive.md) |

## 6. Последствия для других репозиториев

- `../ta_install`: образ `postgis/postgis`, PostGIS в `versions.json` + `TaVmsHost`, сервис
  `vmsmap` (compose ×2, `.env`, `push-images.sh`, `services.json`, `windows.env` при отличиях),
  удаление `vmssquaresingleton`; release notes про отсутствие миграции пространств.
- `../multitenant_admin`: перестал быть источником схемы Square — скрипты удалены, база тенанта
  создаётся пустой; схему `ta_map` мигрирует сам `VmsMap`. Остаётся Keycloak/realm.
- `../Square` и `treealarm/SquareIntegrationClient`: заархивированы на GitHub 2026-11-18.

## 7. В `docs/future_plans` (по-английски, как остальные файлы папки)

- `map-external-sources.md` — внешние источники объектов/состояний/событий через rpc `VmsMap`
  и команды для не-камерных объектов (то, чем в Square были продьюсеры и actions); демо-сид для
  разработки.
- `map-routing-and-zones.md` — маршруты (Valhalla), триггеры по зонам и счётчики треков
  (`LogicMicroService`), «объекты в зоне» через PostGIS.
- `map-tracks-sources.md` — источники треков и значений в ta_vms: треки из детекций аналитики
  (связь с `15-distributed-analytics.md`), телеметрия камер.
- `07-camera-connection-state.md` — событие «поток пропал/появился» от `video_srv` вместо опроса
  VmsUtils адаптером карты.
- `plan-regions.md` — регионы со значениями на планах (если понадобится помимо мнемосхем).
- `dev-keycloak.md` — свой Keycloak в dev compose вместо multitenant_admin.

## 8. Допущения, которые стоит подтвердить по ходу

1. ~~Неразмещённая камера — узел под узлом своего сервера~~ — после решения о единых правах
   (§4.7) это требование, не допущение: у камеры без узла негде лежать правам.
2. **Без миграции данных пространств** из VmsCfg в VmsMap (установки v0.1.x тестовые). Альтернатива:
   одноразовый импорт при первом старте `VmsMap` через gRPC VmsCfg — две релизных итерации.
3. **Агрегация тревог в SQL-транзакции** вместо акторов. Альтернатива — перенести `BlinkService` с
   акторами и починить известные рассинхроны; выбор в M1 по результату теста агрегации.
4. **Файлы в БД** (планы, фоны, картинки событий). Альтернатива — общий том, но сервис
   реплицируемый и правило «никаких локальных файлов» у web_srv.
5. **Редактирование только в Admin → Maps**, не на операторском экране (у Square — на главном
   экране по роли).
6. Порядок M2 и M3 можно поменять местами, если состояния/события нужны раньше переезда
   спатиального модуля: M3 не зависит от M2 (адаптер вешает камеры под узел сервера, пока
   пространств в `VmsMap` нет).

## 9. Сквозная проверка после M4

1. Dev-стек поднимается без multitenant_admin (`docker compose up`), `VmsMap` мигрирует `ta_map` с PostGIS.
2. Стенд `tests/onvifsim`: калибровки и наведение через `VmsMap` — как в `docs/maps-and-plans.md`.
3. Тревога камеры → красная камера, этаж, здание, строка дерева; SSE без поллинга `api/fs/alarms`;
   по окончании — бледное кольцо и `NORM`.
4. Журнал событий: фильтры, галерея, автообновление; событие ведёт к архиву камеры.
5. Мнемосхема «вид сбоку» с репликой камеры и регионом со значением (значение — через
   `UpdateValues` по `grpcurl`).
6. Права: пользователь без доступа к камере не видит её ни в дереве, ни в событиях; объект с
   `rights` для роли виден только ей; право, выданное на здание, наследуется его камерами;
   live/архив/PTZ/экспорт ведут себя как до объединения.
7. Windows: инсталлятор ставит PostgreSQL + PostGIS, `vmsmap` в манифесте, `Build-Stage.ps1`
   находит exe.
