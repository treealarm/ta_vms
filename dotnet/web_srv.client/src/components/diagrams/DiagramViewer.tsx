import { useEffect, useMemo, useRef, useState } from 'react'
import {
  Box, Breadcrumbs, Button, CircularProgress, IconButton, Stack, ToggleButton, ToggleButtonGroup,
  Typography,
} from '@mui/material'
import RestartAltIcon from '@mui/icons-material/RestartAlt'
import ZoomInIcon from '@mui/icons-material/ZoomIn'
import ZoomOutIcon from '@mui/icons-material/ZoomOut'
import { useTranslation } from 'react-i18next'
import type { MapDiagramTypeRegion, MapMarker, MapWatchMessage } from '../../dto/mapDtos'
import { mapLiveUrl } from '../../api/mapApi'
import { useAppDispatch, useAppSelector } from '../../store'
import { loadDiagramContent, loadDiagramValues, setDiagramDepth } from '../../store/diagramsSlice'
import { loadVisualStates } from '../../store/statesSlice'
import {
  createDiagramNode, createReplicaOnDiagram, deleteNode, saveDiagramPlacement,
} from '../../store/mapEditsSlice'
import { useMapLive } from '../../hooks/useMapLive'
import { NODE_DRAG_TYPE } from '../../map/dragTypes'
import { fitScale, ownRect, regionRect, type Rect } from './diagramGeometry'
import DiagramElement from './DiagramElement'
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
}

const ZOOM_STEP = 1.25
const MAX_FACTOR = 8
const MIN_FACTOR = 0.25

/**
 * A mnemoscheme instead of the middle canvas: the diagram's type picture as the paper, its
 * children placed by region or by their own coordinates, values written into the regions, and
 * the breadcrumb chain out of it. A nested diagram is entered by picking it, and left by its
 * breadcrumb — the same movement the map uses for nested plans.
 */
export default function DiagramViewer({
  diagramId, selectedId, onPick, onOpen, editing, onCreated, onDeleted,
}: Props) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const content = useAppSelector((s) => s.diagrams.content)
  const depth = useAppSelector((s) => s.diagrams.depth)
  const values = useAppSelector((s) => s.diagrams.values)
  const loading = useAppSelector((s) => s.diagrams.loading)
  const error = useAppSelector((s) => s.diagrams.error)
  const states = useAppSelector((s) => s.states.states)
  const alarms = useAppSelector((s) => s.states.alarms)
  const descriptions = useAppSelector((s) => s.states.descriptions)

  const [factor, setFactor] = useState(1)
  const [size, setSize] = useState({ width: 0, height: 0 })
  const [typesOpen, setTypesOpen] = useState(false)
  // The element under the pointer while it is dragged, at the rectangle it will be saved at.
  const [dragging, setDragging] = useState<{ id: string; rect: Rect } | null>(null)
  const dragRef = useRef<{ id: string; rect: Rect } | null>(null)
  const paperRef = useRef<HTMLDivElement | null>(null)
  const containerRef = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    void dispatch(loadDiagramContent({ id: diagramId, depth }))
  }, [diagramId, depth, dispatch])

  const reload = () => { void dispatch(loadDiagramContent({ id: diagramId, depth })) }

  // The answer for another diagram may still be in the store for a render or two.
  const shown = content?.diagramId === diagramId ? content : null

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
  const paper = ownRect(root)
  const scale = fitScale(size, paper) * factor
  const shownDepth = shown?.depth ?? depth
  const openType = shown?.dgrTypes?.find((candidate) => candidate.id === root?.dgrType)

  const pick = (marker: MapMarker) => {
    // Entering a nested diagram is the same movement as opening a plan on the map.
    if (marker.nodeKind === 'diagram') onOpen(marker)
    else onPick(marker)
  }

  /** The region of the open diagram's type a point of its paper falls in. */
  const regionAt = (x: number, y: number): MapDiagramTypeRegion | undefined =>
    (openType?.regions ?? []).find((region) => {
      const area = regionRect(region, paper)
      return !!area && x >= area.left && x <= area.left + area.width && y >= area.top && y <= area.top + area.height
    })

  /** Saves where an element was put: the region its middle fell in, or its own rectangle. */
  const place = async (id: string, ownerId: string | undefined, name: string | undefined, rect: Rect) => {
    const element = shown?.content?.find((candidate) => candidate.id === id)
    const region = regionAt(rect.left + rect.width / 2, rect.top + rect.height / 2)

    await dispatch(saveDiagramPlacement({
      id,
      parentId: diagramId,
      ownerId,
      name,
      dgrType: element?.dgrType || undefined,
      regionId: region?.regionKey,
      geometry: region ? undefined : {
        top: rect.top, left: rect.left, width: rect.width, height: rect.height, rotation: rect.rotation,
      },
      fileId: element?.fileId || undefined,
    })).unwrap()

    reload()
  }

  const beginDrag = (marker: MapMarker, rect: Rect, event: React.PointerEvent) => {
    if (!editing) return
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
    if (!editing) return

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
        <Breadcrumbs sx={{ flex: 1, minWidth: 0, '& .MuiBreadcrumbs-ol': { flexWrap: 'nowrap' } }}>
          {(shown?.parents ?? []).map((parent) => parent.id === diagramId ? (
            <Typography key={parent.id} variant="body2" noWrap>{parent.name || parent.id}</Typography>
          ) : (
            <Button
              key={parent.id}
              size="small"
              onClick={() => onOpen(parent)}
              sx={{ minWidth: 0, textTransform: 'none', py: 0 }}
            >
              {parent.name || parent.id}
            </Button>
          ))}
          {!shown?.parents?.length ? <Typography variant="body2" noWrap>{diagramId}</Typography> : null}
        </Breadcrumbs>

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
            <Button size="small" variant="outlined" onClick={() => void createChild()}>
              {t('diagramEditor.newDiagram')}
            </Button>
            <Button size="small" onClick={() => setTypesOpen(true)}>{t('diagramEditor.types')}</Button>
            <Button size="small" color="error" onClick={() => void deleteDiagram()}>
              {t('common.delete')}
            </Button>
          </>
        ) : null}
      </Stack>

      <Box ref={containerRef} sx={{ flex: 1, minHeight: 0, overflow: 'auto', position: 'relative' }}>
        {loading && !shown ? (
          <CircularProgress size={24} sx={{ position: 'absolute', top: '50%', left: '50%', ml: '-12px', mt: '-12px' }} />
        ) : null}
        {error ? <Typography color="error" sx={{ p: 2 }}>{error}</Typography> : null}
        {!loading && !error && !shown ? (
          <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>{t('maps.diagram.empty')}</Typography>
        ) : null}
        {shown && paper.width > 0 ? (
          <Box sx={{ display: 'flex', justifyContent: 'center', p: 2, minWidth: 'min-content' }}>
            <Box
              ref={paperRef}
              onDragOver={(event) => {
                if (editing && event.dataTransfer.types.includes(NODE_DRAG_TYPE)) event.preventDefault()
              }}
              onDrop={(event) => void dropNode(event)}
              sx={{
                width: paper.width * scale,
                height: paper.height * scale,
                position: 'relative',
                flexShrink: 0,
                border: 1,
                borderColor: 'divider',
                bgcolor: '#1f1f1f',
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
                  onMove={editing ? beginDrag : undefined}
                  movable={false}
                  dragged={dragging}
                />
              </Box>
            </Box>
          </Box>
        ) : null}
        {shown && paper.width <= 0 ? (
          <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>{t('maps.diagram.empty')}</Typography>
        ) : null}
      </Box>

      {editing ? (
        <MapDiagramTypesDialog open={typesOpen} onClose={() => setTypesOpen(false)} onSaved={reload} />
      ) : null}
    </Box>
  )
}
