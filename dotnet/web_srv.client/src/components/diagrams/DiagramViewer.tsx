import { useEffect, useMemo, useRef, useState } from 'react'
import {
  Box, Breadcrumbs, Button, CircularProgress, IconButton, Stack, ToggleButton, ToggleButtonGroup,
  Tooltip, Typography,
} from '@mui/material'
import ArrowUpwardIcon from '@mui/icons-material/ArrowUpward'
import PentagonIcon from '@mui/icons-material/Pentagon'
import RestartAltIcon from '@mui/icons-material/RestartAlt'
import TextFieldsIcon from '@mui/icons-material/TextFields'
import TimelineIcon from '@mui/icons-material/Timeline'
import ZoomInIcon from '@mui/icons-material/ZoomIn'
import ZoomOutIcon from '@mui/icons-material/ZoomOut'
import { useTranslation } from 'react-i18next'
import type {
  MapDiagramCoordinate, MapDiagramTypeRegion, MapFigure, MapGeometry, MapMarker, MapWatchMessage,
} from '../../dto/mapDtos'
import { mapLiveUrl } from '../../api/mapApi'
import { useAppDispatch, useAppSelector } from '../../store'
import { loadDiagramContent, loadDiagramValues, setDiagramDepth } from '../../store/diagramsSlice'
import { loadVisualStates } from '../../store/statesSlice'
import { loadCanvas } from '../../store/objectsSlice'
import {
  createDiagramNode, createReplicaOnDiagram, deleteNode, saveDiagramPlacement, saveFigure,
  saveNodeDetails,
} from '../../store/mapEditsSlice'
import CanvasTruncatedNotice from '../../map/CanvasTruncatedNotice'
import { useMapLive } from '../../hooks/useMapLive'
import { useMapFileImage } from '../../hooks/useMapFileImage'
import { NODE_DRAG_TYPE } from '../../map/dragTypes'
import { diagramPaper, figureCoordinate, fitScale, moveGeometry, regionAtPoint, type Rect } from './diagramGeometry'
import DiagramElement from './DiagramElement'
import DiagramFigures from './DiagramFigures'
import MapDiagramTypesDialog from './MapDiagramTypesDialog'

type Props = {
  diagramId: string
  selectedId: string | null
  /** An element was picked: the panel shows it and the canvas stays on this diagram. */
  onPick: (marker: MapMarker) => void
  /** A breadcrumb or a nested diagram: open that canvas — a space is the map, a diagram a dive. */
  onOpen: (marker: MapMarker) => void
  /** The administrator's canvas: elements move, nodes can be dropped on the paper, diagrams are made. */
  editing?: boolean
  /** A diagram was just made: the editor selects it, which opens it. */
  onCreated?: (marker: MapMarker) => void
  /** The open diagram was deleted; its parent is where the editor should go. */
  onDeleted?: () => void
  /**
   * A camera whose diagram the administrator asked to show on this canvas: the next click on the
   * paper stands its panel in the region under it or at a rectangle of its own. The host places it
   * and answers whether it worked — the message about a taken region belongs beside its action.
   */
  armedCameraId?: string | null
  armedCameraName?: string
  onPlaceArmed?: (place: { regionId?: string; geometry?: MapDiagramCoordinate }) => Promise<boolean>
  onCancelArmed?: () => void
}

const ZOOM_STEP = 1.25
const MAX_FACTOR = 8
const MIN_FACTOR = 0.25
/** The rectangle a panel takes where no region of the canvas holds the click. */
const PANEL_WIDTH = 320
const PANEL_HEIGHT = 200
/** On a paper narrower than the panel, the panel is this share of the paper instead. */
const PANEL_SHARE = 0.4

/**
 * A mnemoscheme instead of the middle canvas: the diagram's type picture as the paper, its
 * children placed by region or by their own coordinates, values written into the regions, and
 * the breadcrumb chain out of it. In the editor a pick selects the element — it is dragged and
 * edited where it stands — and a nested diagram is entered by its "Open diagram" button; the
 * operator, who has no editor panel, enters by picking. Both leave by the breadcrumb, the same
 * movement the map uses for nested plans.
 */
export default function DiagramViewer({
  diagramId, selectedId, onPick, onOpen, editing, onCreated, onDeleted,
  armedCameraId, armedCameraName, onPlaceArmed, onCancelArmed,
}: Props) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const content = useAppSelector((s) => s.diagrams.content[diagramId])
  const depth = useAppSelector((s) => s.diagrams.depth)
  const values = useAppSelector((s) => s.diagrams.values)
  const loading = useAppSelector((s) => s.diagrams.loading[diagramId]) ?? false
  const error = useAppSelector((s) => s.diagrams.error[diagramId])
  const states = useAppSelector((s) => s.states.states)
  const alarms = useAppSelector((s) => s.states.alarms)
  const descriptions = useAppSelector((s) => s.states.descriptions)
  const canvasIds = useAppSelector((s) => s.objects.canvases[diagramId]?.ids)
  const figuresById = useAppSelector((s) => s.objects.figures)

  const [factor, setFactor] = useState(1)
  const [size, setSize] = useState({ width: 0, height: 0 })
  const [typesOpen, setTypesOpen] = useState(false)
  // The free-object tool armed on the paper, and the points of the shape being drawn with it.
  const [tool, setTool] = useState<'text' | 'line' | 'polygon' | null>(null)
  const [draft, setDraft] = useState<{ x: number; y: number }[]>([])
  // The element under the pointer while it is dragged, at the rectangle it will be saved at.
  const [dragging, setDragging] = useState<{ id: string; rect: Rect } | null>(null)
  const dragRef = useRef<{ id: string; rect: Rect } | null>(null)
  const paperRef = useRef<HTMLDivElement | null>(null)
  const containerRef = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    void dispatch(loadDiagramContent({ id: diagramId, depth }))
  }, [diagramId, depth, dispatch])

  const reload = () => { void dispatch(loadDiagramContent({ id: diagramId, depth })) }

  // The paper carries ordinary figures as well: captions, zones, lines, icons. They are read per
  // canvas, with the same call a space makes -- the canvas is the diagram, as it is a room.
  useEffect(() => { void dispatch(loadCanvas(diagramId)) }, [diagramId, dispatch])

  const figures = useMemo(
    () => (canvasIds ?? [])
      .map((id) => figuresById[id])
      .filter((figure): figure is MapFigure => !!figure),
    [canvasIds, figuresById],
  )

  // The answer for another diagram may still be in the store for a render or two.
  const shown = content ?? null

  const nodeIds = useMemo(
    () => (shown ? [diagramId, ...(shown.children ?? []).map((child) => child.id)] : []),
    [shown, diagramId],
  )

  // States, alarms and values of every node on the paper: a replica answers with its owner's.
  useEffect(() => {
    if (nodeIds.length === 0) return
    void dispatch(loadVisualStates(nodeIds))
    void dispatch(loadDiagramValues(nodeIds))
  }, [nodeIds, dispatch])

  // Values are asked for too: a region has nothing else to show but the value that changed.
  const idsUrl = useMemo(
    () => (nodeIds.length > 0 ? mapLiveUrl({ ids: nodeIds }, true) : null),
    [nodeIds],
  )

  // The paper follows the same watch the map does: a camera's alarm paints its replica at once.
  useMapLive(idsUrl, (message: MapWatchMessage) => {
    const changed = (message.items ?? [])
      .map((item) => item.objectId)
      .filter((id): id is string => !!id)
    if (changed.length === 0) return

    if (message.action === 'set_visual_states') {
      void dispatch(loadVisualStates(changed))
    } else if (message.action === 'set_ids2update') {
      void dispatch(loadVisualStates(changed))
      void dispatch(loadDiagramValues(nodeIds))
    } else if (message.action === 'on_values_changed') {
      void dispatch(loadDiagramValues(nodeIds))
    }
  })

  // The observer's first callback is the initial size, so nothing is measured inside the effect.
  useEffect(() => {
    const element = containerRef.current
    if (!element) return

    const observer = new ResizeObserver(() => {
      setSize({ width: element.clientWidth, height: element.clientHeight })
    })
    observer.observe(element)
    return () => observer.disconnect()
  }, [])

  const root = shown?.content?.find((candidate) => candidate.id === diagramId)
  const rootMarker = shown?.parents?.find((candidate) => candidate.id === diagramId) ?? { id: diagramId }
  const openType = shown?.dgrTypes?.find((candidate) => candidate.id === root?.dgrType)
  // A representation opens on the content of the diagram it shows: that content is read here and
  // edited where the diagram itself is. Its own place is still the administrator's to drag.
  const readOnly = root?.isRepresentation === true
  // A camera armed for this canvas: the next click on the paper is a place, not an edit. A
  // representation's paper is the diagram it shows, not a canvas of its own, so it takes no place.
  const armed = !!armedCameraId && !!onPlaceArmed && !readOnly

  // A diagram placed on a map has no size of its own to store: the sheet is the picture its type
  // draws it on, so the picture is measured. A diagram with a stored rectangle never asks. The
  // measurement carries the URL it was taken at, so the previous diagram's picture is never taken
  // for this one's while the new one loads.
  const backgroundUrl = useMapFileImage(openType?.fileId)
  const [picture, setPicture] = useState<{ url: string; width: number; height: number } | null>(null)
  useEffect(() => {
    if (!backgroundUrl) return

    let cancelled = false
    const image = new Image()
    image.onload = () => {
      if (!cancelled) {
        setPicture({ url: backgroundUrl, width: image.naturalWidth, height: image.naturalHeight })
      }
    }
    image.src = backgroundUrl
    return () => { cancelled = true }
  }, [backgroundUrl])

  const sheet = backgroundUrl && picture?.url === backgroundUrl ? picture : null
  // The paper is the diagram's own canvas; where it stands in its parent is the parent's business.
  const paper = diagramPaper(root, sheet)
  const scale = fitScale(size, paper) * factor
  const shownDepth = shown?.depth ?? depth


  const pick = (marker: MapMarker) => {
    // In the editor a pick only selects: the element is dragged by its body and edited in the panel,
    // and entering a nested diagram on a click would take the paper away from the hand moving it.
    // The panel's "Open diagram" is the way in. The operator has no panel, so a pick is the way in.
    if (!editing && marker.nodeKind === 'diagram') onOpen(marker)
    else onPick(marker)
  }

  /** The region of the open diagram's type a point of its paper falls in. */
  const regionAt = (x: number, y: number): MapDiagramTypeRegion | undefined =>
    regionAtPoint(openType?.regions, x / paper.width, y / paper.height)

  /**
   * The click that shows the armed camera's diagram here: the region under the pointer, or a
   * rectangle of its own centred on it. A click outside every region is a place too — the panel
   * stands free on the paper, and the administrator drags it afterwards like any other element.
   */
  const placeArmed = async (event: React.MouseEvent) => {
    if (!armed || !onPlaceArmed) return

    const at = paperPoint(event)
    const region = regionAt(at.x, at.y)
    if (region?.regionKey) {
      if (await onPlaceArmed({ regionId: region.regionKey })) reload()
      return
    }

    const width = Math.min(PANEL_WIDTH, Math.max(1, Math.round(paper.width * PANEL_SHARE)))
    const height = Math.min(PANEL_HEIGHT, Math.max(1, Math.round(paper.height * PANEL_SHARE)))
    const clamp = (value: number, limit: number) => Math.max(0, Math.min(limit, value))
    const geometry = {
      left: clamp(at.x - width / 2, paper.width - width),
      top: clamp(at.y - height / 2, paper.height - height),
      width,
      height,
      rotation: 0,
    }
    if (await onPlaceArmed({ geometry })) reload()
  }

  /** Saves where an element was put: the region its middle fell in, or its own rectangle. */
  const place = async (id: string, ownerId: string | undefined, name: string | undefined, rect: Rect) => {
    const element = shown?.content?.find((candidate) => candidate.id === id)
    const region = regionAt(rect.left + rect.width / 2, rect.top + rect.height / 2)
    const copy = element?.isRepresentation === true
    // A representation's `geometry` is the paper of the diagram it shows, not its place: its
    // place is what `placement` carried, and writing the paper back would move the copy to it.
    const own = copy ? element?.placement : element?.geometry

    await dispatch(saveDiagramPlacement({
      id,
      parentId: diagramId,
      ownerId,
      name,
      dgrType: copy ? undefined : element?.dgrType || undefined,
      regionId: region?.regionKey,
      // A region says where the element stands, not what it opens on: its geometry stays its own
      // canvas. Dragged outside every region, the rectangle it was dropped at is that geometry.
      geometry: region ? own : {
        top: rect.top, left: rect.left, width: rect.width, height: rect.height, rotation: rect.rotation,
      },
      fileId: copy ? undefined : element?.fileId || undefined,
    })).unwrap()

    reload()
  }

  const beginDrag = (marker: MapMarker, rect: Rect, event: React.PointerEvent) => {
    if (!editing || readOnly || armed) return
    event.stopPropagation()

    const startX = event.clientX
    const startY = event.clientY
    const move = (moved: PointerEvent) => {
      const next = {
        id: marker.id,
        rect: {
          ...rect,
          left: rect.left + (moved.clientX - startX) / scale,
          top: rect.top + (moved.clientY - startY) / scale,
        },
      }
      dragRef.current = next
      setDragging(next)
    }
    const up = () => {
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', up)
      const final = dragRef.current
      dragRef.current = null
      setDragging(null)
      if (final) void place(marker.id, marker.ownerId, marker.name, final.rect).catch(() => undefined)
    }

    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', up)
  }

  /** A node dragged out of the editor's lists: a replica of it where it was dropped. */
  const dropNode = async (event: React.DragEvent) => {
    if (!editing || readOnly || armed) return

    const nodeId = event.dataTransfer.getData(NODE_DRAG_TYPE)
    if (!nodeId) return
    event.preventDefault()

    const bounds = paperRef.current?.getBoundingClientRect()
    if (!bounds) return

    const x = (event.clientX - bounds.left) / scale
    const y = (event.clientY - bounds.top) / scale
    const size = 80

    try {
      const marker = await dispatch(createReplicaOnDiagram({ ownerId: nodeId, parentId: diagramId })).unwrap()
      await place(marker.id, nodeId, marker.name, {
        left: x - size / 2, top: y - size / 2, width: size, height: size, rotation: 0,
      })
      onPick(marker)
    } catch {
      // The store carries the message; the paper stays as it was.
    }
  }

  const createChild = async () => {
    if (readOnly || armed) return
    try {
      const created = await dispatch(createDiagramNode({
        parentId: diagramId,
        name: `${t('diagramEditor.diagram')} ${(shown?.children?.length ?? 0) + 1}`,
        geometry: {
          top: 20,
          left: 20,
          width: Math.max(200, Math.round(paper.width / 3)),
          height: Math.max(160, Math.round(paper.height / 3)),
          rotation: 0,
        },
      })).unwrap()

      reload()
      onCreated?.({ id: created.id, parentId: diagramId, name: created.name })
    } catch {
      // The store carries the message.
    }
  }

  /** A point of the paper under the pointer, in the diagram's own pixels. */
  const paperPoint = (event: { clientX: number; clientY: number }) => {
    const bounds = paperRef.current?.getBoundingClientRect()
    if (!bounds) return { x: 0, y: 0 }
    return { x: (event.clientX - bounds.left) / scale, y: (event.clientY - bounds.top) / scale }
  }

  /** A free object of the paper: a caption, a line or a zone. It is an ordinary figure, drawn in
   *  the canvas's pixels and edited by the node editor like any other. */
  const createFigure = async (kind: 'text' | 'line' | 'polygon', points: { x: number; y: number }[]) => {
    const id = crypto.randomUUID()
    const coord = points.map((point) => figureCoordinate(point.x, point.y))
    const geometry: MapGeometry = kind === 'polygon'
      ? { type: 'Polygon', coord: [...coord, coord[0]] }
      : kind === 'line'
        ? { type: 'LineString', coord }
        : { type: 'Point', coord: coord.slice(0, 1) }

    const nameKey = kind === 'text'
      ? 'diagramEditor.textObject'
      : kind === 'line' ? 'diagramEditor.lineObject' : 'diagramEditor.zoneObject'
    const name = t(nameKey)

    try {
      await dispatch(saveNodeDetails({
        node: { id, parentId: diagramId, name },
        properties: [],
        visual: {
          objectId: id,
          color: '#ffd54f',
          text: kind === 'text' ? t('diagramEditor.textSample') : undefined,
        },
        figure: { id, parentId: diagramId, name, geometry },
      })).unwrap()
      void dispatch(loadCanvas(diagramId))
    } catch {
      // The store carries the message; the paper stays as it was.
    }

    setTool(null)
    setDraft([])
  }

  /** A click on the paper while a drawing tool is armed. */
  const paperClick = (event: React.MouseEvent) => {
    if (!editing || readOnly || armed || !tool) return
    const at = paperPoint(event)

    if (tool === 'text') { void createFigure('text', [at]); return }
    if (tool === 'line') {
      if (draft.length === 0) setDraft([at])
      else void createFigure('line', [draft[0], at])
      return
    }

    setDraft((current) => [...current, at])
  }

  /** A figure was dragged: its shape travels with it, the canvas is re-read. */
  const movedFigure = (figure: MapFigure, dx: number, dy: number) => {
    void dispatch(saveFigure({
      figure: { ...figure, geometry: moveGeometry(figure.geometry, dx, dy) },
      canvasId: diagramId,
    })).unwrap()
      .then(() => dispatch(loadCanvas(diagramId)))
      .catch(() => undefined)
  }

  const cancelTool = () => { setTool(null); setDraft([]) }

  // The breadcrumb chain ends with the open diagram itself: one step out is the node before it —
  // the space, the enclosing diagram, or the camera's picture this one hangs under.
  const upParent = shown?.parents?.[shown.parents.length - 2]

  // A device diagram is named "<device> / <panel>", and the chain already carries its parent, so
  // the crumb keeps only what the crumb before it does not say.
  const crumbName = (parent: MapMarker, index: number) => {
    const owner = index > 0 ? shown?.parents?.[index - 1]?.name : undefined
    const name = parent.name || parent.id
    return owner && name.startsWith(`${owner} / `) ? name.slice(owner.length + 3) : name
  }

  // The path of a deeply nested node is wider than this toolbar: it stays one line and is scrolled
  // to its end, where the open diagram is.
  const crumbsRef = useRef<HTMLDivElement>(null)
  const crumbPath = (shown?.parents ?? []).map((parent) => parent.id).join('/')
  useEffect(() => {
    const element = crumbsRef.current
    if (element) element.scrollLeft = element.scrollWidth
  }, [crumbPath])

  const deleteDiagram = async () => {
    const name = shown?.parents?.[shown.parents.length - 1]?.name ?? diagramId
    if (!window.confirm(t('maps.editor.confirmDelete', { name }))) return

    try {
      await dispatch(deleteNode(diagramId)).unwrap()
      onDeleted?.()
    } catch {
      // The store carries the message.
    }
  }

  return (
    <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', bgcolor: '#2b2b2b' }}>
      <Stack
        direction="row"
        spacing={1}
        alignItems="center"
        sx={{ px: 1, py: 0.25, borderBottom: 1, borderColor: 'divider', flexShrink: 0 }}
      >
        <Breadcrumbs
          ref={crumbsRef}
          sx={{
            flex: 1,
            minWidth: 0,
            overflowX: 'auto',
            scrollbarWidth: 'thin',
            '&::-webkit-scrollbar': { height: 4 },
            '&::-webkit-scrollbar-thumb': { bgcolor: 'divider', borderRadius: 2 },
            '& .MuiBreadcrumbs-ol': { flexWrap: 'nowrap' },
            '& .MuiBreadcrumbs-separator': { mx: 0.5 },
          }}
        >
          {(shown?.parents ?? []).map((parent, index) => parent.id === diagramId ? (
            <Typography key={parent.id} variant="body2" noWrap sx={{ flexShrink: 0 }}>
              {crumbName(parent, index)}
            </Typography>
          ) : (
            <Button
              key={parent.id}
              size="small"
              onClick={() => onOpen(parent)}
              sx={{ minWidth: 0, flexShrink: 0, whiteSpace: 'nowrap', textTransform: 'none', py: 0 }}
            >
              {crumbName(parent, index)}
            </Button>
          ))}
          {!shown?.parents?.length ? <Typography variant="body2" noWrap>{diagramId}</Typography> : null}
        </Breadcrumbs>

        {/* The breadcrumb already leads out; this is the one click a reader looks for instead of
            picking the second-to-last crumb. */}
        {upParent ? (
          <Tooltip title={t('maps.diagram.up')}>
            <IconButton
              size="small"
              aria-label={t('maps.diagram.up')}
              onClick={() => onOpen(upParent)}
            >
              <ArrowUpwardIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        ) : null}

        <ToggleButtonGroup
          size="small"
          exclusive
          title={t('maps.diagram.depth')}
          value={depth}
          onChange={(_, value: number | null) => { if (value !== null) void dispatch(setDiagramDepth(value)) }}
        >
          {[0, 1, 2, 3].map((level) => (
            <ToggleButton key={level} value={level} sx={{ px: 1, py: 0.25 }}>{level}</ToggleButton>
          ))}
        </ToggleButtonGroup>

        <IconButton
          size="small"
          title={t('maps.diagram.zoomOut')}
          onClick={() => setFactor((value) => Math.max(MIN_FACTOR, value / ZOOM_STEP))}
        >
          <ZoomOutIcon fontSize="small" />
        </IconButton>
        <IconButton size="small" title={t('maps.diagram.reset')} onClick={() => setFactor(1)}>
          <RestartAltIcon fontSize="small" />
        </IconButton>
        <IconButton
          size="small"
          title={t('maps.diagram.zoomIn')}
          onClick={() => setFactor((value) => Math.min(MAX_FACTOR, value * ZOOM_STEP))}
        >
          <ZoomInIcon fontSize="small" />
        </IconButton>

        {editing ? (
          <>
            {armed ? (
              <>
                <Typography variant="caption" color="info.main" noWrap role="status" aria-live="polite">
                  {t('diagramEditor.placeOnCanvasHint', { name: armedCameraName || armedCameraId })}
                </Typography>
                <Button size="small" onClick={() => onCancelArmed?.()}>{t('common.cancel')}</Button>
              </>
            ) : null}
            {readOnly ? (
              <Typography variant="caption" color="text.secondary" noWrap>
                {t('diagramEditor.representationHint')}
              </Typography>
            ) : (
              <>
                <ToggleButtonGroup
                  size="small"
                  exclusive
                  title={t('diagramEditor.freeObjects')}
                  value={tool}
                  onChange={(_, value: 'text' | 'line' | 'polygon' | null) => { setTool(value); setDraft([]) }}
                >
                  <ToggleButton value="text" title={t('diagramEditor.textObject')} sx={{ px: 1, py: 0.25 }}>
                    <TextFieldsIcon fontSize="small" />
                  </ToggleButton>
                  <ToggleButton value="line" title={t('diagramEditor.lineObject')} sx={{ px: 1, py: 0.25 }}>
                    <TimelineIcon fontSize="small" />
                  </ToggleButton>
                  <ToggleButton value="polygon" title={t('diagramEditor.zoneObject')} sx={{ px: 1, py: 0.25 }}>
                    <PentagonIcon fontSize="small" />
                  </ToggleButton>
                </ToggleButtonGroup>
                {tool === 'polygon' && draft.length >= 3 ? (
                  <Button size="small" variant="outlined" onClick={() => void createFigure('polygon', draft)}>
                    {t('maps.editor.finish')}
                  </Button>
                ) : null}
                {tool ? (
                  <Button size="small" onClick={cancelTool}>{t('common.cancel')}</Button>
                ) : null}
                {tool ? (
                  <Typography variant="caption" color="info.main" noWrap role="status" aria-live="polite">
                    {t(tool === 'text'
                      ? 'diagramEditor.textHint'
                      : tool === 'line' ? 'diagramEditor.lineHint' : 'diagramEditor.polygonHint')}
                  </Typography>
                ) : null}
                <Button size="small" variant="outlined" onClick={() => void createChild()}>
                  {t('diagramEditor.newDiagram')}
                </Button>
              </>
            )}
            <Button size="small" onClick={() => setTypesOpen(true)}>{t('diagramEditor.types')}</Button>
            <Button size="small" color="error" onClick={() => void deleteDiagram()}>
              {t('common.delete')}
            </Button>
          </>
        ) : null}
      </Stack>

      <CanvasTruncatedNotice canvasId={diagramId} />

      <Box ref={containerRef} sx={{ flex: 1, minHeight: 0, overflow: 'auto', position: 'relative' }}>
        {loading && !shown ? (
          <CircularProgress size={24} sx={{ position: 'absolute', top: '50%', left: '50%', ml: '-12px', mt: '-12px' }} />
        ) : null}
        {error ? <Typography color="error" sx={{ p: 2 }}>{error}</Typography> : null}
        {!loading && !error && !shown ? (
          <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>{t('maps.diagram.empty')}</Typography>
        ) : null}
        {shown ? (
          <Box sx={{ display: 'flex', justifyContent: 'center', p: 2, minWidth: 'min-content' }}>
            <Box
              ref={paperRef}
              data-testid="diagram-paper"
              onDragOver={(event) => {
                if (editing && !readOnly && !armed && event.dataTransfer.types.includes(NODE_DRAG_TYPE)) {
                  event.preventDefault()
                }
              }}
              onDrop={readOnly || armed ? undefined : (event) => void dropNode(event)}
              // The root element covers the paper and stops its own clicks, so a drawing tool
              // listens in the capture phase: while one is armed, the click is for the tool.
              onClickCapture={(event) => {
                if (!editing || readOnly) return
                if (armed) {
                  // The armed click is the place, and it wins over whatever is under it.
                  event.stopPropagation()
                  void placeArmed(event)
                  return
                }
                if (!tool) return
                event.stopPropagation()
                paperClick(event)
              }}
              sx={{
                width: paper.width * scale,
                height: paper.height * scale,
                position: 'relative',
                flexShrink: 0,
                border: 1,
                borderColor: 'divider',
                bgcolor: '#1f1f1f',
                cursor: editing && !readOnly && (armed || tool) ? 'crosshair' : 'default',
              }}
            >
              <Box
                sx={{
                  width: paper.width,
                  height: paper.height,
                  position: 'relative',
                  transform: `scale(${scale})`,
                  transformOrigin: 'top left',
                }}
              >
                {/* The diagram itself — its picture and the elements standing on it — is one
                    stacking context under the free shapes drawn on the paper: the root element
                    lifts itself when it is selected, and without this the zones a reader drew
                    would sink below the picture. */}
                <Box sx={{ position: 'absolute', inset: 0, zIndex: 0 }}>
                  <DiagramElement
                    marker={rootMarker}
                    rect={{ ...paper, left: 0, top: 0 }}
                    content={shown}
                    values={values}
                    states={states}
                    alarms={alarms}
                    descriptions={descriptions}
                    depth={shownDepth}
                    selectedId={selectedId}
                    onPick={pick}
                    onMove={editing && !readOnly ? beginDrag : undefined}
                    movable={false}
                    dragged={dragging}
                    showRegions={editing && !readOnly}
                  />
                </Box>

                <DiagramFigures
                  figures={figures}
                  paper={paper}
                  scale={scale}
                  selectedId={selectedId}
                  editing={editing}
                  onPick={pick}
                  onMoved={editing ? movedFigure : undefined}
                />

                {draft.length > 0 ? (
                  <svg
                    style={{
                      position: 'absolute', inset: 0, width: '100%', height: '100%',
                      pointerEvents: 'none', zIndex: 4,
                    }}
                    viewBox={`0 0 ${paper.width} ${paper.height}`}
                    preserveAspectRatio="none"
                  >
                    <polyline
                      points={draft.map((point) => `${point.x},${point.y}`).join(' ')}
                      fill="none"
                      stroke="#ffd54f"
                      strokeWidth={2}
                      strokeDasharray="5 4"
                    />
                    {draft.map((point, index) => (
                      <circle key={index} cx={point.x} cy={point.y} r={3} fill="#ffd54f" />
                    ))}
                  </svg>
                ) : null}
              </Box>
            </Box>
          </Box>
        ) : null}
      </Box>

      {editing ? (
        <MapDiagramTypesDialog open={typesOpen} onClose={() => setTypesOpen(false)} onSaved={reload} />
      ) : null}
    </Box>
  )
}
