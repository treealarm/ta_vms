import { useCallback, useEffect, useMemo, useState } from 'react'
import type { MapMarker, MapVisual } from '../../dto/mapDtos'
import { useAppDispatch, useAppSelector } from '../../store'
import { attachDeviceDiagram } from '../../store/mapEditsSlice'
import { childrenOf, loadChildren, loadObjProps } from '../../store/objectsSlice'
import { useEditorState } from './editorState'

/**
 * The device's diagram, two ways of reaching it.
 *
 * `openDeviceDiagram` keeps it with the camera and opens it: the node hangs under the camera's
 * picture on the plan, nothing is drawn there, and the map offers a link into it while the camera is
 * selected. `placeDeviceDiagram` also draws it on the canvas in front of the reader — the
 * administrator asking for a permanent picture beside the camera.
 *
 * The server keeps one diagram per device, so asking again returns the node it has rather than a
 * second one. Answers whether it happened; a failure is the store's to say, and the caller decides
 * where to show it.
 */
export function useDeviceDiagram(spaceId: string | null | undefined) {
  const dispatch = useAppDispatch()
  const { setSelection, openDiagram } = useEditorState()
  const [busy, setBusy] = useState(false)

  const attach = useCallback(async (cameraId: string, canvas: string | undefined) => {
    setBusy(true)
    try {
      const marker = await dispatch(attachDeviceDiagram({ cameraId, spaceId: canvas })).unwrap()
      if (!marker.id) return false
      setSelection({ kind: 'node', id: marker.id })
      openDiagram(marker.id)
      return true
    } catch {
      return false
    } finally {
      setBusy(false)
    }
  }, [dispatch, setSelection, openDiagram])

  const openDeviceDiagram = useCallback(
    (cameraId: string) => attach(cameraId, undefined), [attach])

  const placeDeviceDiagram = useCallback(
    (cameraId: string) => attach(cameraId, spaceId ?? undefined), [attach, spaceId])

  return { openDeviceDiagram, placeDeviceDiagram, busy }
}

/**
 * The diagram of the camera under a node, when it has one: read from the camera's own children, and
 * its thumbnail from the node's own properties. What the map offers as a link beside a selected
 * camera — a device without a diagram gets no link, and nothing else is read.
 */
export function useDeviceDiagramLink(node: MapMarker | null | undefined) {
  const dispatch = useAppDispatch()
  const markers = useAppSelector((s) => s.objects.markers)
  const childrenByParent = useAppSelector((s) => s.objects.childrenByParent)
  const props = useAppSelector((s) => s.objects.props)

  // A camera's picture is what the plan selects; anything else has no diagram to offer.
  const id = node?.cameraId ? node.id : undefined

  useEffect(() => {
    if (id && !childrenByParent[id]?.loaded) void dispatch(loadChildren({ parentId: id }))
  }, [id, childrenByParent, dispatch])

  const diagram = useMemo(
    () => (id
      ? childrenOf(markers, childrenByParent, id).find((child) => child.nodeKind === 'diagram')
      : undefined),
    [id, markers, childrenByParent],
  )

  const diagramId = diagram?.id
  useEffect(() => {
    if (diagramId && !props[diagramId]) void dispatch(loadObjProps(diagramId))
  }, [diagramId, props, dispatch])

  return diagram ? { diagram, visual: props[diagram.id]?.visual as MapVisual | undefined } : null
}
