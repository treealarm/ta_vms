import { useEffect, useMemo, useRef } from 'react'
import type { SpaceDto } from '../dto/dtos'
import type { MapFigure, MapMarker, MapWatchMessage } from '../dto/mapDtos'
import { mapLiveUrl } from '../api/mapApi'
import { useAppDispatch, useAppSelector } from '../store'
import { canvasAdd, canvasNodes, canvasRemove, dropNodes, loadCanvas, loadNodes } from '../store/objectsSlice'
import { loadLevels } from '../store/levelsSlice'
import { loadVisualStates } from '../store/statesSlice'
import { useMapLive } from './useMapLive'

export type MapSpaceData = {
  /** Everything drawn on the open space's canvas, at any depth of the tree. */
  markers: MapMarker[]
  /** The same nodes that have a shape; a node without geometry draws nothing. */
  figures: MapFigure[]
}

/**
 * Keeps one canvas loaded and watched: every figure drawn in the space's coordinates, a box watch
 * for what joins, moves or leaves it, and an id watch for everybody the client already knows --
 * the tree as well as the canvas, because the alarm points come from the same states.
 */
export function useMapSpace(space: SpaceDto | null): MapSpaceData {
  const dispatch = useAppDispatch()
  const markersById = useAppSelector((s) => s.objects.markers)
  const figuresById = useAppSelector((s) => s.objects.figures)
  const canvases = useAppSelector((s) => s.objects.canvases)
  const levelsLoaded = useAppSelector((s) => s.levels.loaded)

  const canvasId = space?.id ?? null
  const page = canvasId ? canvases[canvasId] : undefined

  // Opened or opened again: a page that stays in the store is a page that can have lost a node
  // while nobody was watching it -- another plan was open, or the stream was down. The read
  // replaces the page's ids, which is what a watch gap cannot do.
  useEffect(() => {
    if (canvasId) void dispatch(loadCanvas(canvasId))
  }, [canvasId, dispatch])

  useEffect(() => {
    if (!levelsLoaded) void dispatch(loadLevels())
  }, [levelsLoaded, dispatch])

  const markers = useMemo(
    () => (canvasId ? canvasNodes(markersById, canvases, canvasId) : []),
    [canvasId, markersById, canvases],
  )

  // New canvas nodes (from the read, a watch update that joined the page) get their states; ids
  // are remembered so a node is not asked again.
  const ids = useMemo(() => page?.ids ?? [], [page])
  const loadedIds = useRef(new Set<string>())
  useEffect(() => {
    const missing = ids.filter((id) => !loadedIds.current.has(id))
    if (missing.length === 0) return

    for (const id of missing) loadedIds.current.add(id)
    void dispatch(loadVisualStates(missing))
  }, [ids, dispatch])

  /** The canvas scope itself: what is drawn there, and what joins or leaves it. */
  const handleCanvas = (message: MapWatchMessage) => {
    if (!canvasId) return
    const changed = (message.items ?? [])
      .map((item) => item.objectId)
      .filter((id): id is string => !!id)
    if (changed.length === 0) return

    switch (message.action) {
      case 'set_ids2update':
        dispatch(canvasAdd({ canvasId, ids: changed }))
        void dispatch(loadNodes(changed))
        void dispatch(loadVisualStates(changed))
        break
      case 'set_visual_states':
        void dispatch(loadVisualStates(changed))
        break
      case 'set_ids2delete':
        dispatch(canvasRemove({ canvasId, ids: changed }))
        break
      default:
        break
    }
  }

  /** The known nodes: states and alarms of the tree as well, which the canvas does not list. */
  const handleNodes = (message: MapWatchMessage) => {
    const changed = (message.items ?? [])
      .map((item) => item.objectId)
      .filter((id): id is string => !!id)
    if (changed.length === 0) return

    switch (message.action) {
      case 'set_ids2update':
        void dispatch(loadNodes(changed))
        void dispatch(loadVisualStates(changed))
        break
      case 'set_visual_states':
        void dispatch(loadVisualStates(changed))
        break
      case 'set_ids2delete':
        dispatch(dropNodes(changed))
        break
      default:
        break
    }
  }

  const boxUrl = useMemo(
    () => (canvasId ? mapLiveUrl({ box: { canvasId } }) : null),
    [canvasId],
  )

  // The id set is a sorted string so a marker whose name or state changed does not reopen the
  // stream: only a node appearing or disappearing does.
  const idsKey = useMemo(() => Object.keys(markersById).sort().join(','), [markersById])
  const idsUrl = useMemo(() => (idsKey ? mapLiveUrl({ ids: idsKey.split(',') }) : null), [idsKey])

  useMapLive(boxUrl, handleCanvas, () => {
    // A reconnect starts a watch with nothing sent yet: a node deleted during the gap is never
    // in its delete list, so the page is read again rather than trusted.
    if (canvasId) void dispatch(loadCanvas(canvasId))
  })
  useMapLive(idsUrl, handleNodes)

  const figures = markers
    .map((marker) => figuresById[marker.id])
    .filter((figure): figure is MapFigure => !!figure)

  return { markers, figures }
}
