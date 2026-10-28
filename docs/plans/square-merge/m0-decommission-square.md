# M0 — демонтаж интеграции Square (детальный план)

> Часть плана [Square → ta_vms](README.md); ссылки «§N» ведут в его разделы.
> Файлы — ориентиры, не полный перечень.

1. ✅ План разложен по файлам `docs/plans/square-merge/` (2026-10-28). Темы из §7 — файлами в
   `docs/future_plans/` (со строками в его README), когда по ходу работы станет ясно, что именно
   отложено.
2. ✅ Удалить `dotnet/VmsSquareSingleton`, `dotnet/VmsSquareSingleton.Tests`, сабмодуль
   `dotnet/SquareIntegrationClient` (`.gitmodules`), `proto/vms_square.proto`, запись в
   `dotnet/ta_vms.sln`, `dotnet/dapr.yaml`, `docker-compose.yml`, `.vscode/launch.json`,
   `.vscode/tasks.json`, `.github/workflows/images.yml`.
3. ✅ Env во всех местах: `SQUARE_INTEGRATION_SINK_APP_ID`, `SQUARE_INTEGRATION_PRODUCER_ID`,
   `SQUARE_INTEGRATION_DEFAULT_LAT/LON`, `DAPR_SQUARE_SINGLETON_APP_ID`, `SQUARE_FRONTEND_ORIGIN`,
   `SQUARE_SINGLETON_VMS_PORT` — `.env`, `../ta_install/.env`, `../ta_install/docker-compose.yml`
   (сервис `vmssquaresingleton` + sidecar), `../ta_install/scripts/push-images.sh`, launch.json.
4. ✅ web_srv: `Grpc/SquareLinkProbe.cs`, поля `SquareFrontendOrigin`/`SquareIntegrated` в
   `FrontendConfigController`, CORS-политика `SquareFrontend` в `Program.cs`; клиент:
   `frontendConfigSlice`, кнопка «Open in Square» в `CameraUnifiedCard.tsx`, deep-link `?monitorId=`
   в `MonitorsView.tsx`, строки i18n. Устаревшие комментарии в `VmsFs/Grpc/FsServiceImpl.cs` и
   `VmsAnalyticsSingleton/Program.cs`. Топик `retention-event-created` **остаётся** — его возьмёт адаптер.
5. ✅ `docker-compose.yml`: сеть `common_app_network external: true` → своя сеть; dev-стек поднимается
   сам. (В dev `KEYCLOAK_URL` по умолчанию пуст; dev с логином — `keycloak` из
   `../ta_install/init_files/keycloak/realm.json` отдельным шагом, если понадобится.)
6. ✅ Документация (в этом же майлстоуне, чтобы правила не расходились с кодом): раздел «Square
   integration» в `CLAUDE.md` заменить на «Карта — слой ta_vms» (ссылка на план), в
   «Maps & plans» убрать пункты про экспорт; `docs/square-integration-plan.md`
   удалить; в `docs/maps-and-plans.md` убрать «Ownership with Square» и «Export to Square»;
   `docs/docker-bringup.md` — без чужой сети.
7. Square: README с пометкой «архив, функциональность переехала в ta_vms» (отдельный репозиторий,
   отдельный коммит, после M4).

Проверка: `dotnet build dotnet/ta_vms.sln`, `npm run build && npm run lint` в `web_srv.client`,
`docker compose config`, `docker compose up` без multitenant_admin; `grep -ri square` по репозиторию
оставляет только исторические упоминания в docs.

## Ход работы

Ветка `square-merge/m0-decommission` в ta_vms и в ta_install, от `master` 37c6bc6 и `main`
соответственно. Шаги 1–6 сделаны 2026-10-28, не закоммичены. Шаг 7 — после M4.

Что сделано сверх списка и что решено по ходу:
- CORS-политика web_srv удалена целиком: она существовала только для вкладки «Monitor» Square.
- Deep-link `?monitorId=` в `MonitorsView` остался: им пользуются и переходы из событий.
- `retention-event-created` по-прежнему публикуется VmsFs, хотя подписчика пока нет; его возьмёт
  адаптер карты в M3. Комментарий в `FsServiceImpl` говорит это.
- В ta_install убраны `vmssquaresingleton` из compose, `push-images.sh`, пустые `SQUARE_*` из
  пакета узла (`distributed.sh`) и `windows.env`, шаг `Build-Stage.ps1`, проверка в
  `StackConfigTests`.
- Не тронуто сознательно: история решений про Redis в `init_files/dapr_components/*.yaml`,
  описания соглашений «как у Square» (`owner_id`, `__image_rotate`) в `vms_cfg.proto`,
  `0002_spatial.sql` (применённая миграция) и `maps-and-plans.md` — они уйдут вместе с разделом в M2.
- В dev compose остановленный контейнер `dapr-placement` удалён: он был привязан к старой сети;
  compose создаёт его заново при запуске профиля `app`.

Проверки, пройденные 2026-10-28:
- `dotnet build dotnet/ta_vms.sln` — 0 предупреждений, 0 ошибок; `dotnet test` — все проекты
  зелёные (VmsFs 5, VmsCfg 26, VmsAnalyticsSingleton 24, VmsPtzTrackingSingleton 96).
- `npm run build` — собирается; `npm run lint` — 27 замечаний, все были до M0 (в изменённых файлах
  только два старых предупреждения `exhaustive-deps` в `CameraUnifiedCard.tsx`).
- `docker compose config` (с профилем `app` и без) — валиден; `docker compose up -d
  --force-recreate` без multitenant_admin — postgres, rabbitmq, mediamtx в сети `ta_vms_default`,
  `db-init` завершился с 0; стек остановлен.
- ta_install: `docker compose config` — валиден; `dotnet test windows/TaVmsHost.Tests` — 64 из 64.
- Не проверено: полный dev-стек F5 и профиль `app` с образами — их пересборка не входила в M0.

