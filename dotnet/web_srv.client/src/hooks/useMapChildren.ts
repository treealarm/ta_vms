import { useEffect, useMemo } from 'react'
import type { MapFigure, MapMarker } from '../dto/mapDtos'
import { useAppDispatch, useAppSelector } from '../store'
import { canvasNodes, loadCanvas } from '../store/objectsSlice'

export type MapChildren = {
  /** Everything drawn on the space's canvas, at any depth of the tree. */
  markers: MapMarker[]
  /** The same nodes that have a shape; a node without one draws nothing. */
  figures: MapFigure[]
}

/**
 * One canvas for the map editor: every figure drawn in the space's coordinates, read once and
 * refreshed as edits land. No watch stream — the administrator is the one changing what is drawn,
 * and a live diff would fight the drags.
 */
export function useMapChildren(spaceId: string | null): MapChildren {
  const dispatch = useAppDispatch()
  const markersById = useAppSelector((s) => s.objects.markers)
  const figuresById = useAppSelector((s) => s.objects.figures)
  const canvases = useAppSelector((s) => s.objects.canvases)

  useEffect(() => {
    if (spaceId && !canvases[spaceId]?.loaded) {
      void dispatch(loadCanvas(spaceId))
    }
  }, [spaceId, canvases, dispatch])

  const markers = useMemo(
    () => (spaceId ? canvasNodes(markersById, canvases, spaceId) : []),
    [spaceId, markersById, canvases],
  )

  const figures = markers
    .map((marker) => figuresById[marker.id])
    .filter((figure): figure is MapFigure => !!figure)

  return { markers, figures }
}
