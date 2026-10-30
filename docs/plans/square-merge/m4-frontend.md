# M4 — фронтенд: симбиоз карт (объём; детали — при старте)

> Часть плана [Square → ta_vms](README.md); ссылки «§N» ведут в его разделы.
> Файлы — ориентиры, не полный перечень.

Новое в `web_srv.client`:
- `src/components/objects/`: дерево узлов (`ObjectTree`, точки тревог, чекбоксы), панель объекта
  (`ObjectPanel`: свойства по `visual_type`, состояния, события), редактор свойств.
- `src/map/`: слой произвольных фигур (`FiguresLayer`: точка/круг, полилиния, полигон, иконка и
  поворот из `visual`, слои масштаба), редактирование вершин (админка), перетаскивание
  узла/реплики на холст, цвет по состоянию.
- `src/components/diagrams/`: просмотр мнемосхем с вложением (глубина 0–3), редактор мнемосхем и
  типов (регионы в долях, фон, положение/размер/поворот) — в Admin → Maps.
- `src/components/events/EventsJournal.tsx` из `RetentionEventsTab.tsx`; `StatesTable`.
- `src/components/maps/MapsOperatorPanel.tsx` — раскладка §4.8; `MapsAdminPanel.tsx` — новые
  инструменты по правилу «кнопка рядом с тем, на что действует» (`listActions`, `DetailToolbar`).
- Redux: `objectsSlice`, `statesSlice`, `eventsSlice`, `diagramsSlice`, `rightsSlice`;
  `hooks/useMapLive.ts` (SSE) вместо `useAlarms`.
- Справочник состояний и уровни масштаба — Admin.

Проверка: `npm run build && npm run lint`; сценарии руками по §4.8: тревога подсвечивает камеру,
этаж, здание и строку дерева; журнал фильтрует и автообновляется; оператор открывает PTZ/охрану
из панели камеры на карте; мнемосхема вида сбоку с репликой камеры краснеет вместе с ней.
