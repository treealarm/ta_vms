import { useEffect, useMemo, useRef } from 'react'
import type { MapFigure, MapMarker } from '../dto/mapDtos'
import { useAppDispatch, useAppSelector } from '../store'
import { childrenOf, loadChildren, loadNodes } from '../store/objectsSlice'

export type MapChildren = {
  /** The space's direct children — the nodes a canvas draws. */
  markers: MapMarker[]
  /** The same nodes that have a shape; a node without one draws nothing. */
  figures: MapFigure[]
}

/**
 * The nodes of one space for the map editor: its direct children with their shapes, read once and
 * refreshed as edits land. No watch stream — the administrator is the one changing what is drawn,
 * and a live diff would fight the drags.
 */
export function useMapChildren(spaceId: string | null): MapChildren {
  const dispatch = useAppDispatch()
  const markersById = useAppSelector((s) => s.objects.markers)
  const figuresById = useAppSelector((s) => s.objects.figures)
  const childrenByParent = useAppSelector((s) => s.objects.childrenByParent)

  const children = useMemo(
    () => (spaceId ? childrenOf(markersById, childrenByParent, spaceId) : []),
    [spaceId, markersById, childrenByParent],
  )

  useEffect(() => {
    if (spaceId && !childrenByParent[spaceId]?.loaded) {
      void dispatch(loadChildren({ parentId: spaceId }))
    }
  }, [spaceId, childrenByParent, dispatch])

  // Shapes are read once per id; a node that changes is reloaded by whoever wrote it.
  const childIds = useMemo(
    () => (spaceId ? childrenByParent[spaceId]?.ids ?? [] : []),
    [spaceId, childrenByParent],
  )
  const requested = useRef(new Set<string>())
  useEffect(() => {
    const missing = childIds.filter((id) => !requested.current.has(id))
    if (missing.length === 0) return

    for (const id of missing) requested.current.add(id)
    void dispatch(loadNodes(missing))
  }, [childIds, dispatch])

  const figures = children
    .map((marker) => figuresById[marker.id])
    .filter((figure): figure is MapFigure => !!figure)

  return { markers: children, figures }
}
