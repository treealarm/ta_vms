# M1 — каркас `VmsMap`: ядро Square и PostGIS (объём; детали — при старте)

> Часть плана [Square → ta_vms](README.md); ссылки «§N» ведут в его разделы.
> Файлы — ориентиры, не полный перечень.

1. `dotnet/VmsMap` (один проект): из Square — `Domain` (без `Integro/`, `Actions/`), `DbLayer`
   (без `IntegroService`, `ActionsService`, `GroupService`), сервисы `DataChangeLayer`, слитые с
   соответствующими сервисами `Db/`, `IntegrationServerLib/{GeometryProtoConvert,ObjectIdGenerator}`,
   `LeafletAlarms/KMeans`; пространства имён `VmsMap.*`; публикация через Dapr как в остальных
   сервисах. Рефакторинг по §3.1 — в этом же майлстоуне, пока код свежий в голове. Подключение к БД по
   контракту §4.1.
2. `proto/vms_map.proto` — `MapService` (package `vms.map`), пишется заново в стиле остальных
   proto ta_vms; в M1 — запись: объекты, фигуры, свойства, состояния, события, значения,
   треки, мнемосхемы, права, уровни. Чтение добавляется по майлстоунам.
3. `DbMigrator` → `dotnet/VmsDb`; миграция `0001_square_schema.sql` из
   `multitenant_admin/mt_admin/init_files/initdb/sql_scripts/` без `integro*`, `action_*`,
   `groups`, `map_cash` (+ PostGIS, PK `events`, FK `diagrams → diagram_types`).
4. PostGIS: `docker-compose.yml`, `../ta_install/docker-compose.yml`,
   `../ta_install/docker-compose.distributed.yml` → `postgis/postgis:latest`;
   `../ta_install/windows/versions.json` — zip bundle PostGIS 3.6.2 для pg18 по URL + хэш;
   `TaVmsHost` раскладывает его в каталог PostgreSQL при установке. Миграция сама делает
   `CREATE EXTENSION` (пользователь — владелец/суперпользователь).
5. Новый сервис по чек-листу CLAUDE.md: `.env` (`POSTGRES_MAP_DB`, `DAPR_MAP_APP_ID`, порты),
   `../ta_install/.env` + compose + `scripts/push-images.sh` + `scripts/build-images.sh`,
   корневой `docker-compose.yml`, `dotnet/dapr.yaml`, `.vscode/launch.json`, `.vscode/tasks.json`
   (`prepare:vmsmap`, `dapr:vmsmap:*`), `ta_vms.sln`, `dotnet/VmsMap/Dockerfile` + матрица в
   `.github/workflows/images.yml`, `../ta_install/windows/config/services.json`
   (`roles: standalone, core`), `../ta_install/windows/config/windows.env` (путь к PostGIS, если
   он отличается), `docker-compose.distributed.yml` (core), `multitenant_admin` — имя БД для
   штамповки. Попутно: чек-лист CLAUDE.md называет `dotnet/.vscode/launch.json` и
   `video_srv/.vscode/launch.json`, которых нет — поправить чек-лист (есть только корневой).
6. Тесты: `dotnet/VmsMap.Tests` — агрегация тревог по дереву (SQL-вариант `BlinkService`),
   реплики (`GetOwnersAndViewsAsync`), `UpdateHierarchyAsync` после починки.

Проверка: сервис стартует, миграция создаёт схему с PostGIS; `grpcurl` через Dapr
`UpdateObjects`/`UpdateStates`/`AddEvents` пишут в `ta_map`; тревога у листа даёт
`children_alarms` у всех предков, снятие — обратно; `VmsMap.Tests` зелёные.
