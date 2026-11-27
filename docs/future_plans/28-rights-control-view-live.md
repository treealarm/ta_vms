# Семантика `control` и `view_live`: пустой Control у роли `user`

UX-аудит 2026-11-27 (P1, [27-ux-audit-web-frontend.md](27-ux-audit-web-frontend.md)) нашёл на
демо-мире `umd-datacenter` оператора с единственной ролью `user`, у которого пуст и пульт
управления, и ячейки стены.

**Что наблюдается.** Демо-мир выдаёт роли `user` два права: `view` на `map1` и `control` на
`plan1` (`ta_map.rights`). Control фильтрует список объектов по `view_live`
(`dotnet/web_srv.client/src/api/controlApi.ts`, комментарий к `listControlObjects`), а `view_live`
роли не выдан. При выбранном `plan1` панель охраны пространства открывается, но список пуст:
«Nothing to control in this space. 0 shown». Стена `UMD DC` у того же пользователя показывает
девять пустых ячеек. То же правило записано в [docs/maps/control.md](../maps/control.md): строки
фильтруются по `view_live` до нарезки страницы, `can_control` — для действий.

**Почему это не исправление вёрстки.** Возможны два прочтения, и они дают разные модели прав:

- `control` подразумевает наблюдение: кто может управлять объектом, может и видеть его живое
  состояние, поэтому `view_live` — следствие `control`;
- `control` и `view_live` независимы: управлять можно и без права смотреть, а демо-мир просто не
  выдал роли то, что ей нужно для его же сценария.

Выдать `view_live` роли `user`, не решив этого, значит скрыть несоответствие: следующая роль и
следующий экран снова упрутся в тот же вопрос. Аудит поэтому фиксирует факт, а не вывод.

**Что сделать.** Решить и записать модель: что означает `control`, включает ли он `view_live`, и
что именно требуется ячейке стены — наблюдение или отдельное право. Затем привести в соответствие
одно из двух: иерархию прав в VmsMap (`Rights.cs`/`RightsService`) или демо-сид
`ta_map.rights`; обновить [docs/maps/control.md](../maps/control.md) и, если меняется модель,
[docs/architecture/login.md](../architecture/login.md). После решения повторить сценарий O6
аудита и проход стены под ролью `user`.

Связано: [27-ux-audit-web-frontend.md](27-ux-audit-web-frontend.md) — исходное наблюдение;
[docs/plans/archive/ux-p1-audit.md](../plans/archive/ux-p1-audit.md) — план P1/P2, где этот пункт явно не входит;
[docs/plans/archive/m2.4g-control-panel.md](../plans/archive/m2.4g-control-panel.md) — как пульт
устроен сейчас.
