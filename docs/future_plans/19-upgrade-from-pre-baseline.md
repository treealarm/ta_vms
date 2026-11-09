# Обновление установки старше 2026-11-06 останавливает VmsFs и аналитику

Найдено 2026-11-09 прогоном CI (job `core-upgrade`: релиз v0.1.7, затем установщик текущей сборки) и
подтверждено логами job `upgrade`.

`VmsFs` и `VmsAnalyticsSingleton` после обновления падают в цикле:

```
Unhandled exception. System.InvalidOperationException: VmsFs: database 'ta_fs' has tables but no
'0000_init.sql' baseline — its schema predates the baseline, and the baseline cannot be applied over
it. Recreate the database (drop it and run the preparation step again); no data is carried over from
a pre-baseline schema.
```

Это следствие свёртки схемы в один baseline (`ta_vms eab147e`, 2026-11-06) и защиты мигратора
(`bb5df8f`, «Refuse a pre-baseline schema instead of failing on CREATE TABLE»): схема, созданная до
неё, сознательно не мигрируется. В dev-стенде это лечится пересозданием баз, поэтому там не
заметно; на установке v0.1.7 и старше базы пересоздать нельзя без потери конфигурации.

Заметить это трудно: web поднимается, вход `admin/admin` работает, `TaVmsHost status` показывает
сервисы живыми, а VmsFs и аналитика перезапускаются каждые несколько секунд. Job `upgrade` в
`.github/workflows/windows-installer.yml` поэтому проходит, хотя записи и аналитики после обновления
нет; job `core-upgrade` (добавлен тогда же) проверяет вход, токен и TLS, но не то, что VmsFs
действительно работает.

**Why:** реальная установка v0.1.7/0.1.8-candidate, обновлённая текущим установщиком, останется без
записи на диск и без аналитики, и снаружи это выглядит как исправная система.

**How to apply:** решить и записать одно из двух:
- Установщик на ядре/standalone при обновлении находит базы без `0000_init.sql` и пересоздаёт их
  (с явным предупреждением в логе установки, что конфигурация камер и записи в базе теряются) —
  тогда обновление v0.1.7 → 0.1.9 работает из коробки.
- Либо объявить установки до baseline непереходящими и добавить это в README и в установщик
  (понятная ошибка с инструкцией), а из CI убрать обновление с v0.1.7 или перевести его на
  пересозданные базы.

Заодно усилить CI: после обновления дождаться, что `vmsfs` и `vmsanalyticssingleton` в
`TaVmsHost status` не в перезапуске (для `upgrade` и `core-upgrade`), иначе такие поломки снова
пройдут незамеченными.
