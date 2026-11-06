# Общая device_placement вместо camera_placement

Найдено 2026-11-05 при обсуждении M2.4: `camera_placement` лежит в логической части (`ta_map`) и
содержит камеро-специфичные поля. Если у каждого типа устройства появится своя таблица размещения,
это плохо масштабируется. Пользователь предложил общий `device_placement` с типом устройства и
специфическими полями в JSON — «что-то более отделённое от конкретной железки».

## Как сейчас

- `ta_map.camera_placement` (`dotnet/VmsMap/Grpc/migrations/0002_spaces.sql:69`):
  `object_id PK`, `camera_id`, `is_reference`, `height_m`, `heading_deg`, `hfov_deg`, `range_m`.
- Геометрия точки — в `geo_objects`; `camera_placement` — дополнительные признаки камеры
  (высота, курс, угол обзора, дальность) и признак эталона. У камеры одна эталонная строка и
  сколько угодно строк-картинок у реплик.
- Потребители: `SpatialDb`, `VmsPtzTrackingSingleton` (наведение/калибровка), `api/spatial/placements`,
  демо-сидер.

Почему это в логической части: наведение и геометрия — свойства **логического представления** на
карте, а не железки. Но конкретные поля (`hfov_deg`, `range_m`) — камерные, поэтому таблица и
называется `camera_placement`.

## Варианты

1. **Оставить `camera_placement`; для других типов — свои таблицы.** Просто, типизировано, но
   каждый новый тип устройства добавляет таблицу и код размещения. Не масштабируется.
2. **Общий `device_placement` с `device_kind` + `props jsonb` (рекомендуется).**
   ```sql
   CREATE TABLE device_placement (
     object_id    uuid PRIMARY KEY REFERENCES objects(id) ON DELETE CASCADE,
     source_kind  varchar NOT NULL,   -- 'camera' | 'fire_panel' | ...
     source_id    uuid NOT NULL,      -- id объекта — GUID
     is_reference boolean NOT NULL DEFAULT true,
     height_m     double precision NOT NULL DEFAULT 0,
     heading_deg  double precision,   -- общий для наведения
     props        jsonb NOT NULL DEFAULT '{}' -- hfov_deg, range_m, зона, ...
   );
   ```
   Общие поля типизированы, специфические — в `props` с JSON-схемой на тип. `camera_placement`
   остаётся view/совместимым слоем на время миграции; `SpatialDb`/PTZ читают через типизированные
   аксессоры.
3. **Только `geo_objects` + properties, без side-таблицы.** Минимум схемы, но теряются типы,
   ограничения (одна эталонная строка), индексы по `hfov/range` и быстрые запросы для наведения.

## Что учесть

- Одна эталонная строка на устройство (`UNIQUE ... WHERE is_reference`) — это ограничение должно
  остаться.
- `VmsPtzTrackingSingleton` и `SpatialDb` читают `hfov_deg`/`range_m`/`heading_deg`; переход на
  JSON требует аксессоров и валидации, иначе ошибки всплывут в наведении.
- Для частей устройства (датчик/реле) размещение вообще не нужно: `geo_objects` достаточно, если
  часть рисуется; `device_placement` — для устройств, которым нужны признаки эталона/наведения.
- `camera_placement` в `ta_map` — это логическое представление, а не запись VmsCfg; общий
  `device_placement` не должен превращаться в копию железной конфигурации.

## Рекомендация

Вариант 2, но не в M2.4: сначала закрыть устройства/тревоги, затем мигрировать `camera_placement`
в `device_placement` с `props` и JSON-схемой, оставив view для совместимости. Пользовательский
формат `props` — только после второго типа устройства с реальными отличиями.

## Открытые вопросы

- Какие поля общие для всех устройств (height, heading) и какие уходят в `props`?
- Нужна ли JSON-схема/валидация на уровне БД или достаточно кода?
- Делать `device_placement` сразу или после появления второго типа устройства?
- Как мигрировать существующие `camera_placement` и не сломать PTZ-наведение?
