# ta_cfg: id объектов — GUID (решено 2026-11-05)

Найдено 2026-11-05 при обсуждении M2.4: у объектов в `ta_cfg` (VmsCfg) зачастую текстовые id.
**Решение пользователя: `camera_id` и id объектов — UUID. Релиза ещё не было, поэтому миграции не
пишем: базовая схема правится на месте, БД пересоздаётся.**

## Что меняем

- `camera_setup.camera_id` — `uuid PRIMARY KEY`; человекочитаемое имя остаётся в `camera_setup.name`.
- `storage.storage_id` — `uuid PRIMARY KEY`; `root_path` остаётся естественной подписью, отдельного
  `name` не заводим.
- Все FK на эти ключи (`camera_stream`, `camera_storage`, `camera_schedule`, `camera_states`,
  `ptz_*`, `analytics_watch`, `onvif_*`, `monitor_camera`, `monitor_slot`, `camera_acl_setting` и
  т.д.) — `uuid`.
- `camera_stream.stream_name` остаётся **именем** (text): это не id, а имя потока.
- `ta_map.camera_placement.camera_id` — `uuid`; `device_node.source_id` — `uuid`.
- `VmsFs`: `retention_events.camera_id`, `clips.camera_id`, `source_outages.camera_id` и другие
  `camera_id` — `uuid`.
- `ta_map` object ids уже UUID; `CameraObjectId.For(camera_id)` продолжает хешировать строку GUID,
  поэтому узлы стабильны в пределах одной установки. При пересоздании БД они просто новые.
- `video_srv`, `VmsOnvif`, `web_srv` на проводе по-прежнему видят `camera_id` строкой: UUID
  форматируется в текст, proto/DTO не меняются. C++-модели не трогаем.
- Демо и стенд: `sim-master`, `sim-ptz` и т.п. остаются **именами** камер; `camera_id` — GUID.
  Демо использует детерминированные GUID (`DemoIds.Id("camera/sim-master")`), поэтому
  `DemoPlacement`, `DemoTelemetry`, провижининг и `check_demo.py` сходятся без поиска по имени и
  без требования уникальности имён.

## Как делаем

- Релиза не было; **сворачиваем все миграции каждого сервиса в один `0000_init.sql`**, как будто
  база создаётся с нуля:
  - `dotnet/VmsCfg/Grpc/migrations/` (0001..0009) → `0000_init.sql`;
  - `dotnet/VmsFs/Grpc/migrations/` (0001) → `0000_init.sql`;
  - `dotnet/VmsMap/Grpc/migrations/` (0001..0007) → `0000_init.sql`, `camera_node` удалён,
    `device_node` создаётся сразу (см. M2.4);
  - `dotnet/VmsAnalyticsSingleton/Grpc/migrations/` (0001..0002) → `0000_init.sql`.
- В baseline — `uuid` для id и FK; `camera_stream.stream_name` остаётся text; `camera_placement`/
  `device_node`/`retention_events`/`detections` используют UUID.
- Старые файлы миграций удаляются; `schema_migrations` в существующих БД не совпадёт, поэтому
  данные не переносим — Dev/стенд/`../ta_install` удаляют БД (или тома) и поднимают заново; Windows
  — переустановить/очистить БД. Пока релиза нет, это допустимо.
- Новые таблицы M2.4 дописываются в тот же `0000_init.sql`; отдельных миграций до релиза нет.
- Обновить код, где `camera_id`/`storage_id` типизированы как строка в БД-слое (EF-модели,
  Npgsql-параметры), и тесты/демо, где id сравнивается с `sim-*`.
- Внешние producer'ы должны присылать GUID; старые текстовые id не поддерживаем.

## Что не делаем

- Не пишем `ALTER TABLE ... USING` и backfill: данных, которые надо сохранить, нет.
- Не оставляем текстовый `camera_id` «для совместимости»: релиза не было.
- Не трогаем `event_id`/`stream_name`/`token`: это имена/логические id, а не id объектов.
- Не меняем proto/DTO/JSON-контракты: на проводе id остаётся строкой (UUID).

## Риски и проверка

- `CameraObjectId.For` и все узлы карты: при пересоздании БД они новые; старые ссылки в демо/тестах
  надо перевести на резолв по имени.
- `VmsFs` retention/clips: GUID в `camera_id`; `ListRetentionEvents`, `RetainEvent`, очистка клипов
  и `source_outages` должны использовать uuid-параметры.
- `video_srv`: конфиг приходит JSON-ом; id — строка GUID, менять C++ не нужно, но проверить
  сериализацию/логирование.
- `../ta_install`/Windows: чистые БД; обновить демо/примеры, где camera id был `sim-*`.
- Проверка: сборка всех сервисов, `dotnet test`, миграции на чистой БД, dev-стек с нуля, демо/стенд.

## Открытые вопросы

- Нужны ли пользовательские/редактируемые id (slug) вдобавок к GUID? Пока имя — `camera_setup.name`.
- UUID v4 или v7 (сортируемый по времени)?
- Как быть с внешними источниками, которые уже используют текстовые id (OnvifSim — свой мир, не ta_cfg)?
