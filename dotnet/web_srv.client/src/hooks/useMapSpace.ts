import { useEffect, useMemo, useRef } from 'react'
import type { SpaceDto } from '../dto/dtos'
import type { MapBox, MapFigure, MapMarker, MapWatchMessage } from '../dto/mapDtos'
import { mapLiveUrl } from '../api/mapApi'
import { useAppDispatch, useAppSelector } from '../store'
import { childrenOf, dropNodes, loadChildren, loadNodes } from '../store/objectsSlice'
import { loadLevels } from '../store/levelsSlice'
import { loadVisualStates } from '../store/statesSlice'
import { useMapLive } from './useMapLive'

export type MapSpaceData = {
  /** The direct children of the open space — what the canvas and the tree draw. */
  markers: MapMarker[]
  /** The same nodes that have a shape; a node without geometry draws nothing. */
  figures: MapFigure[]
}

/**
 * Keeps the nodes of one space loaded and watched: its children with their figures and states,
 * a box watch for what appears, moves or disappears in it, and an id watch for everybody the
 * client already knows — the tree as well as the canvas, because the alarm points come from the
 * same states.
 */
export function useMapSpace(space: SpaceDto | null): MapSpaceData {
  const dispatch = useAppDispatch()
  const markersById = useAppSelector((s) => s.objects.markers)
  const figuresById = useAppSelector((s) => s.objects.figures)
  const childrenByParent = useAppSelector((s) => s.objects.childrenByParent)
  const levelsLoaded = useAppSelector((s) => s.levels.loaded)

  const spaceId = space?.id ?? null
  const children = useMemo(
    () => (spaceId ? childrenOf(markersById, childrenByParent, spaceId) : []),
    [spaceId, markersById, childrenByParent],
  )

  useEffect(() => {
    if (spaceId && !childrenByParent[spaceId]?.loaded) {
      void dispatch(loadChildren({ parentId: spaceId }))
    }
  }, [spaceId, childrenByParent, dispatch])

  useEffect(() => {
    if (!levelsLoaded) void dispatch(loadLevels())
  }, [levelsLoaded, dispatch])

  // New children (from the initial read, a page or a watch update that joined the page) get
  // their figures and states; ids are remembered so a node without either is not asked again.
  const childIds = useMemo(
    () => (spaceId ? childrenByParent[spaceId]?.ids ?? [] : []),
    [spaceId, childrenByParent],
  )
  const loadedIds = useRef(new Set<string>())
  useEffect(() => {
    const missing = childIds.filter((id) => !loadedIds.current.has(id))
    if (missing.length === 0) return

    for (const id of missing) loadedIds.current.add(id)
    void dispatch(loadNodes(missing))
    void dispatch(loadVisualStates(missing))
  }, [childIds, dispatch])

  const box = useMemo<MapBox | undefined>(() => (space ? {
    wn: { lat: space.view.northEast.lat, lon: space.view.southWest.lon },
    es: { lat: space.view.southWest.lat, lon: space.view.northEast.lon },
  } : undefined), [space])
  const boxUrl = useMemo(() => (box ? mapLiveUrl({ box }) : null), [box])

  // The id set is a sorted string so a marker whose name or state changed does not reopen the
  // stream: only a node appearing or disappearing does.
  const idsKey = useMemo(() => Object.keys(markersById).sort().join(','), [markersById])
  const idsUrl = useMemo(() => (idsKey ? mapLiveUrl({ ids: idsKey.split(',') }) : null), [idsKey])

  const handle = (message: MapWatchMessage) => {
    const ids = (message.items ?? [])
      .map((item) => item.objectId)
      .filter((id): id is string => !!id)
    if (ids.length === 0) return

    switch (message.action) {
      case 'set_ids2update':
        void dispatch(loadNodes(ids))
        void dispatch(loadVisualStates(ids))
        break
      case 'set_visual_states':
        void dispatch(loadVisualStates(ids))
        break
      case 'set_ids2delete':
        dispatch(dropNodes(ids))
        break
      default:
        break
    }
  }

  useMapLive(boxUrl, handle)
  useMapLive(idsUrl, handle)

  const figures = children
    .map((marker) => figuresById[marker.id])
    .filter((figure): figure is MapFigure => !!figure)

  return { markers: children, figures }
}
