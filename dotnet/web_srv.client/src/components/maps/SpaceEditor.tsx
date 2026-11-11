import { useEffect, useMemo, useRef, useState } from 'react'
import {
  Alert, Box, Button, Divider, FormControlLabel, IconButton, Stack, Switch, Tab, Tabs,
  ToggleButton, ToggleButtonGroup, Tooltip, Typography,
} from '@mui/material'
import AccountTreeIcon from '@mui/icons-material/AccountTree'
import PanToolIcon from '@mui/icons-material/PanToolAlt'
import PlaceIcon from '@mui/icons-material/Place'
import ImageIcon from '@mui/icons-material/Image'
import CropRotateIcon from '@mui/icons-material/CropRotate'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import CenterFocusStrongIcon from '@mui/icons-material/CenterFocusStrong'
import AdjustIcon from '@mui/icons-material/Adjust'
import TimelineIcon from '@mui/icons-material/Timeline'
import PentagonOutlinedIcon from '@mui/icons-material/PentagonOutlined'
import { useMap } from 'react-leaflet'
import L from 'leaflet'
import { useTranslation } from 'react-i18next'
import type { CameraPlacementDto, GeoPointDto, PlanCornersDto, SpaceDto } from '../../dto/dtos'
import type { MapFigure, MapGeometry, MapMarker } from '../../dto/mapDtos'
import { useAppDispatch, useAppSelector } from '../../store'
import {
  errorText, loadLandmarks, loadPlacements, removeLandmark, removePlacement, removeSpacePlan, setPlanCorners,
  upsertLandmark, upsertPlacement, upsertSpace, uploadSpacePlan,
} from '../../store/spatialSlice'
import { loadCanvas, loadNodes, loadObjProps, revealNode } from '../../store/objectsSlice'
import { loadLevels } from '../../store/levelsSlice'
import { isUnder } from '../../map/tree'
import { createReplicaAt, deleteNode, moveNode, movePlacedCamera, saveFigure } from '../../store/mapEditsSlice'
import { loadDiagramContent } from '../../store/diagramsSlice'
import { useMapChildren } from '../../hooks/useMapChildren'
import SpaceCanvas from '../../map/SpaceCanvas'
import PlanOverlay from '../../map/PlanOverlay'
import FiguresLayer from '../../map/FiguresLayer'
import DrawingPreview, { FinishOnDoubleClick } from '../../map/DrawingPreview'
import { CameraMarker, LandmarkMarker } from '../../map/markers'
import { FootprintEditor, Footprints } from '../../map/footprints'
import { cameraMapStatus, STATUS_COLOR } from '../../map/cameraStatus'
import { cornersAround, distance, figureAtPlacement, pointsCentre, rescaleCorners } from '../../map/geo'
import { CameraPlacementPanel, FootprintPanel, LandmarkPanel } from './PlacementPanels'
import MoveToField from './MoveToField'
import ObjectPropsEditor from './ObjectPropsEditor'
import ObjectRights from '../objects/ObjectRights'
import DiagramViewer from '../diagrams/DiagramViewer'
import DiagramElementEditor from '../diagrams/DiagramElementEditor'
import NumberField from './NumberField'
import SpaceNav from './SpaceNav'
import PtzGeoCalibrationDialog from './PtzGeoCalibrationDialog'
import GroundCalibrationDialog from './GroundCalibrationDialog'
import { DRAW_MODES, useEditorState, type Mode, type SideTab } from './editorState'
import { useDeviceDiagram, useDeviceDiagramLink } from './useDeviceDiagram'

/** Mirrors VmsCfg's limit, so an oversized plan is refused before it is uploaded. */
const MAX_PLAN_BYTES = 20 * 1024 * 1024

const DEFAULT_PLACEMENT = { heightM: 3, headingDeg: 0, hfovDeg: 60, rangeM: 30 }

/** Width of the property column: a placement's fields side by side, with room for the toolbar's buttons. */
const SIDE_WIDTH = 300

type Props = {
  space: SpaceDto
  /** The space's own form (name, coordinates…), shown on the Space tab beside the map. */
  settings: React.ReactNode
  /** A camera to select, once, when its placement here appears — for arriving with it in mind. */
  initialCameraId?: string | null
  /** A node to select, once, when its place on this canvas appears: found on the map elsewhere. */
  initialNodeId?: string | null
  /** That node was picked out; the page drops its note of it. */
  onInitialNodeShown?: () => void
  /**
   * Moves the whole page to another space — the tree beside it follows. A node named with it is
   * selected there, so "show on map" crosses spaces without losing the object.
   */
  onOpenSpace: (spaceId: string, nodeId?: string) => void
}

/**
 * The administrator's view of one space: place cameras and aim their cones, drop landmarks, upload a
 * plan and line it up, shape the footprints of the spaces nested in it. Everything here saves as it
 * is done — a drag when released, a field when left — because a map is edited by hand, one object
 * at a time, and an Apply button far from the object would be one more thing to forget.
 *
 * The map's own tools sit above it; the lists of what can be put on it are the page's left column
 * (`MapsSidebar`); properties are the column on the right, the space's own on one tab and the
 * selected object's on the other. The selection, the tool and the camera waiting to be placed are
 * shared with the lists through `useEditorState`.
 */
export default function SpaceEditor({
  space, settings, initialCameraId, initialNodeId, onInitialNodeShown, onOpenSpace,
}: Props) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const camerasById = useAppSelector((s) => s.live.cameraStatesById)
  const placements = useAppSelector((s) => s.spatial.placements)
  const spaces = useAppSelector((s) => s.spatial.spaces)
  const loadedLandmarks = useAppSelector((s) => s.spatial.landmarksBySpace[space.id ?? ''])
  const landmarks = useMemo(() => loadedLandmarks ?? [], [loadedLandmarks])

  const {
    mode, setMode, selection, setSelection, pendingCameraId, setPendingCameraId,
    pendingDiagramId, setPendingDiagramId, focus, select,
    sideTab, setSideTab, openDiagramId, openDiagram, showInTree, focusOn,
  } = useEditorState()
  const [draft, setDraft] = useState<CameraPlacementDto | null>(null)
  const [draftCorners, setDraftCorners] = useState<PlanCornersDto | null>(null)
  const [draftFootprint, setDraftFootprint] = useState<GeoPointDto[] | null>(null)
  // The shape being drawn, click by click, and the one being dragged, before it is saved.
  const [draftPoints, setDraftPoints] = useState<GeoPointDto[] | null>(null)
  const [draftFigure, setDraftFigure] = useState<MapFigure | null>(null)
  const [alignOnMap, setAlignOnMap] = useState(false)
  const [calibrating, setCalibrating] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const mapRef = useRef<L.Map | null>(null)
  const fileRef = useRef<HTMLInputElement>(null)

  const spaceId = space.id ?? ''
  const { openDeviceDiagram, placeDeviceDiagram } = useDeviceDiagram(spaceId)
  // The map is taking a click rather than selecting what is under it: a landmark, a camera or a
  // device's diagram, or a shape being drawn. What the click is for always wins over what it lands
  // on.
  const picking = !!pendingCameraId || !!pendingDiagramId || mode === 'landmark'
    || DRAW_MODES.includes(mode)
  const mapData = useMapChildren(spaceId || null)
  const levels = useAppSelector((s) => s.levels.levels)
  const markersById = useAppSelector((s) => s.objects.markers)
  const figuresById = useAppSelector((s) => s.objects.figures)
  const propsById = useAppSelector((s) => s.objects.props)
  const selectedNodeId = selection?.kind === 'node' ? selection.id : null
  const diagramContent = useAppSelector((s) => s.diagrams.content)
  const diagramDepth = useAppSelector((s) => s.diagrams.depth)
  // The diagram of the camera picked on the canvas, offered as a link beside it.
  const deviceDiagram = useDeviceDiagramLink(selectedNodeId ? markersById[selectedNodeId] : undefined)

  /**
   * Finds the node where it is drawn: the diagram it stands in is opened over the plan, a node of
   * another space opens that space, and on this plan the map is centred on its point. The mirror of
   * "show in tree": there the row was known and the place was not.
   */
  const showOnCanvas = async (id: string) => {
    const marker = markersById[id]
    if (!marker) return

    // Drawn on this canvas already — a figure here, a camera's placement here — so the place is
    // this one: the map comes to it and nothing else moves. Opening the diagram such a node is
    // *of* would take away the very canvas the reader pointed at. The figure is looked for among
    // this canvas's, not among every node the store happens to hold.
    const cameraId = marker.cameraId
    const placement = cameraId
      ? placements.find((candidate) => candidate.cameraId === cameraId && candidate.spaceId === spaceId)
      : undefined
    const onThisCanvas = mapData.figures.find((figure) => figure.id === id)
    const at = pointsCentre(onThisCanvas?.geometry?.coord ?? [])
      ?? (placement ? { lat: placement.lat, lon: placement.lon } : null)
    if (at) {
      focusOn(at)
      return
    }

    // Drawn elsewhere: the diagram it stands in is opened over the plan with the node picked out,
    // or the space it stands in is opened — and picked out there too, so the object is not lost on
    // the way.
    const ancestors = await dispatch(revealNode(marker)).unwrap().catch(() => [] as MapMarker[])
    const canvas = marker.nodeKind === 'diagram'
      ? marker
      : [...ancestors].reverse()
        .find((ancestor) => ancestor.nodeKind === 'space' || ancestor.nodeKind === 'diagram')

    if (!canvas) return
    if (canvas.nodeKind === 'diagram') {
      setSelection({ kind: 'node', id })
      if (canvas.id !== openDiagramId) openDiagram(canvas.id)
      return
    }
    if (canvas.id !== spaceId) {
      openDiagram(null)
      onOpenSpace(canvas.id, id)
    }
  }

  // A diagram is its own canvas: opening it from the list, or entering a nested one, replaces the
  // map. A node drawn inside the open diagram keeps it open — picking a zone on its paper must not
  // throw the reader back to the plan.
  const selectNode = (id: string) => {
    setSelection({ kind: 'node', id })
    if (markersById[id]?.nodeKind === 'diagram') openDiagram(id)
    else if (!openDiagramId || !isUnder(markersById, id, openDiagramId)) openDiagram(null)
  }

  // On the space's own canvas a click only selects. Entering a diagram on click would take the
  // canvas away at the very moment its point must be dragged, and the point is the way in only
  // for the operator; the administrator opens it from the node tree.
  const selectOnCanvas = (id: string) => setSelection({ kind: 'node', id })

  // When a diagram is open, a selected child that has a row in it is an element of it.
  const shownDiagram = openDiagramId && diagramContent?.diagramId === openDiagramId ? diagramContent : null
  const selectedElement = selectedNodeId && shownDiagram
    ? shownDiagram.content?.find((candidate) => candidate.id === selectedNodeId)
    : undefined
  const openDiagramRow = openDiagramId
    ? shownDiagram?.content?.find((candidate) => candidate.id === openDiagramId)
    : undefined
  const parentDiagramType = shownDiagram?.dgrTypes?.find((candidate) => candidate.id === openDiagramRow?.dgrType)

  useEffect(() => {
    void dispatch(loadPlacements())
    if (spaceId) void dispatch(loadLandmarks(spaceId))
  }, [dispatch, spaceId])

  useEffect(() => {
    if (levels.length === 0) void dispatch(loadLevels())
  }, [levels.length, dispatch])

  const here = placements.filter((p) => p.spaceId === spaceId)
  // The spaces whose footprint is on this canvas, wherever the tree holds them.
  const onCanvas = new Set(mapData.markers.filter((m) => m.nodeKind === 'space').map((m) => m.id))
  const nested = spaces.filter((s) => !!s.id && onCanvas.has(s.id))
  // The layer each footprint belongs to, from the space's own polygon in the canvas read.
  const spaceZoomLevels: Record<string, string | undefined> = {}
  for (const s of nested) {
    if (s.id) spaceZoomLevels[s.id] = figuresById[s.id]?.zoomLevel
  }
  const placementHere = (cameraId: string) => here.find((p) => p.cameraId === cameraId)
  // The node that carries a placement is a replica of the camera -- always, reference or picture:
  // the canonical node stands under its server and is never drawn on a canvas.
  const nodeForPlacement = (p: CameraPlacementDto) =>
    figureAtPlacement(mapData.figures, p) ?? mapData.markers.find((m) => m.cameraId === p.cameraId)
  const referenceOf = (cameraId: string) => placements.find((p) => p.cameraId === cameraId && p.isReference)
  const spaceById = (id: string | undefined) => spaces.find((s) => s.id === id)
  // The camera behind the selection: a picture clicked on the canvas names it, a row picked in the
  // tree names its node. Only the node a placement draws is that placement — another picture of the
  // same camera (its readings, say) is a node of its own and has no placement here.
  const selectedCameraId = selection?.kind === 'camera'
    ? selection.id
    : selection?.kind === 'node'
      ? (() => {
          const cameraId = markersById[selection.id]?.cameraId
          if (!cameraId) return undefined
          const placement = placementHere(cameraId)
          return placement && nodeForPlacement(placement)?.id === selection.id ? cameraId : undefined
        })()
      : undefined
  const selectedPlacement = selectedCameraId ? placementHere(selectedCameraId) : undefined
  const selectedLandmark = selection?.kind === 'landmark' ? landmarks.find((m) => m.id === selection.id) : undefined
  const selectedNested = selection?.kind === 'footprint' ? nested.find((s) => s.id === selection.id) : undefined
  // The node the selection stands for, whichever panel shows it: the one to move and to put a
  // new object under.
  const selectionNodeId = selectedNodeId
    ?? (selectedPlacement ? nodeForPlacement(selectedPlacement)?.id : undefined)
    ?? selectedLandmark?.id
    ?? selectedNested?.id
    ?? undefined
  // The node whichever panel shows: a camera by its placement's replica, a landmark, a nested
  // space by its footprint — so the Object tab is the same form for all of them.
  const selectedNode = selectionNodeId ? markersById[selectionNodeId] : undefined
  const selectedFigure = selectionNodeId ? figuresById[selectionNodeId] : undefined
  const selectedProps = selectionNodeId ? propsById[selectionNodeId] : undefined
  // "Show on map" names a place on this canvas: a node drawn nowhere here — a container, a camera
  // part — has none to name, and a node inside the open diagram is already where it is.
  const selectionDrawn = !!selectedFigure
    || (!!selectedNode?.cameraId && here.some((placement) => placement.cameraId === selectedNode.cameraId))

  // The editor needs the node's properties and look, which the tree read does not carry.
  useEffect(() => {
    if (selectionNodeId) {
      void dispatch(loadObjProps(selectionNodeId))
      // Its figure too: whether it is drawn anywhere, and where — what the "show on map" offer
      // is about — is told by the node's own geometry.
      void dispatch(loadNodes([selectionNodeId]))
    }
  }, [selectionNodeId, dispatch])

  // A container is any selected node: a new object goes inside it, as a parent is chosen in Square.
  const containerId = selection?.kind === 'node' ? selectedNodeId : null

  const run = async (action: () => Promise<unknown>) => {
    setError(null)
    try {
      await action()
    } catch (e) {
      setError(errorText(e))
    }
  }

  // What the administrator is looking at becomes what everyone sees when the space opens.
  const saveView = () =>
    run(async () => {
      const map = mapRef.current
      if (!map) return
      const b = map.getBounds()
      await dispatch(upsertSpace({
        ...space,
        view: {
          southWest: { lat: b.getSouth(), lon: b.getWest() },
          northEast: { lat: b.getNorth(), lon: b.getEast() },
        },
      })).unwrap()
    })

  const savePlacement = (p: CameraPlacementDto) =>
    run(async () => {
      // A placement this canvas did not have before is a new node here; one written while a
      // container is selected goes inside it.
      const isNew = !here.some((stored) => stored.cameraId === p.cameraId)
      await dispatch(upsertPlacement(p)).unwrap()
      if (isNew && containerId) {
        await dispatch(movePlacedCamera({
          cameraId: p.cameraId,
          canvasId: spaceId,
          containerId,
        })).unwrap()
      }
      setDraft(null)
    })

  const saveFootprint = (of: SpaceDto, footprint: GeoPointDto[]) =>
    run(async () => {
      await dispatch(upsertSpace({ ...of, footprint })).unwrap()
      setDraftFootprint(null)
    })

  /** A new shape on the canvas: a node of its own, its geometry in the space's coordinates. */
  const createFigure = (points: GeoPointDto[], kind: 'point' | 'line' | 'polygon') => {
    if (!spaceId) return

    const id = crypto.randomUUID()
    const geometry: MapGeometry = {
      type: kind === 'point' ? 'Point' : kind === 'line' ? 'LineString' : 'Polygon',
      // A polygon's ring is closed before it leaves the browser; the map stores it that way.
      coord: kind === 'polygon' ? [...points, points[0]] : points,
    }

    setDraftPoints(null)
    void run(async () => {
      await dispatch(saveFigure({
        figure: {
          id,
          // A selected container holds the new shape; otherwise it is a child of the space.
          parentId: containerId ?? spaceId,
          name: `${t('maps.editor.figure')} ${mapData.markers.length + 1}`,
          geometry,
        },
        canvasId: spaceId,
      })).unwrap()
      // The tool has done its job: back to selecting, with the new shape in hand.
      setMode('select')
      setSelection({ kind: 'node', id })
    })
  }

  /** Ends the shape being drawn; a double click repeats its last point, so that one is dropped. */
  const finishDrawing = () => {
    const points = draftPoints ?? []
    const last = points[points.length - 1]
    const previous = points[points.length - 2]
    const clean = last && previous && last.lat === previous.lat && last.lon === previous.lon
      ? points.slice(0, -1)
      : points

    if (mode === 'line' && clean.length >= 2) createFigure(clean, 'line')
    else if (mode === 'polygon' && clean.length >= 3) createFigure(clean, 'polygon')
    else setDraftPoints(null)
  }

  // Enter ends the shape, Escape drops it: the keyboard half of drawing on the map.
  const finishRef = useRef(finishDrawing)
  useEffect(() => {
    finishRef.current = finishDrawing
  })
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && DRAW_MODES.includes(mode)) {
        setDraftPoints(null)
        setMode('select')
      }
      if (event.key === 'Enter' && draftPoints) finishRef.current()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [mode, draftPoints, setMode])

  /** A vertex was dragged: the shape follows it live and is saved when it is let go. */
  const handleGeometryChange = (figure: MapFigure, geometry: MapGeometry, commit: boolean) => {
    const next = { ...figure, geometry }
    if (!commit) {
      setDraftFigure(next)
      return
    }

    setDraftFigure(null)
    void run(async () => { await dispatch(saveFigure({ figure: next })).unwrap() })
  }

  const handleMapClick = (at: GeoPointDto) => {
    if (mode === 'point' && spaceId) {
      createFigure([at], 'point')
      return
    }
    if ((mode === 'line' || mode === 'polygon') && spaceId) {
      setDraftPoints((previous) => (previous ? [...previous, at] : [at]))
      return
    }
    if (pendingDiagramId) {
      // The diagram is the camera's, standing where it was asked for: one per device, so this
      // either draws it here or brings the one it has to this point.
      const cameraId = pendingDiagramId
      setPendingDiagramId(null)
      void run(async () => {
        const placed = await placeDeviceDiagram(cameraId, { lat: at.lat, lon: at.lon })
        if (!placed) throw new Error(t('diagramEditor.noDeviceDiagram'))
      })
      return
    }
    if (pendingCameraId) {
      // A camera drawn here for the first time looks the way it does where it really stands: the
      // picture should not contradict the original.
      const reference = referenceOf(pendingCameraId)
      const cameraId = pendingCameraId
      setPendingCameraId(null)
      void savePlacement({
        ...DEFAULT_PLACEMENT,
        ...(reference ? { heightM: reference.heightM, headingDeg: reference.headingDeg, hfovDeg: reference.hfovDeg, rangeM: reference.rangeM } : {}),
        cameraId,
        spaceId,
        isReference: false,
        lat: at.lat,
        lon: at.lon,
      }).then(() => setSelection({ kind: 'camera', id: cameraId }))
      return
    }
    if (mode === 'landmark' && spaceId) {
      void run(async () => {
        const saved = await dispatch(upsertLandmark({
          spaceId,
          name: `${t('maps.landmark')} ${landmarks.length + 1}`,
          lat: at.lat,
          lon: at.lon,
          heightM: 0,
        })).unwrap()
        // A selected container holds the new landmark; otherwise it hangs under the space.
        if (saved.id && containerId) {
          await dispatch(moveNode({
            node: { id: saved.id, parentId: containerId, name: saved.name },
            canvasId: spaceId,
          })).unwrap()
        }
        if (saved.id) setSelection({ kind: 'landmark', id: saved.id })
      })
      return
    }
    setSelection(null)
  }

  const handlePlanFile = async (file: File) => {
    if (file.size > MAX_PLAN_BYTES) {
      setError(t('maps.planTooLarge', { mb: MAX_PLAN_BYTES / (1024 * 1024) }))
      return
    }
    let widthPx: number
    let heightPx: number
    try {
      const bitmap = await createImageBitmap(file)
      widthPx = bitmap.width
      heightPx = bitmap.height
      bitmap.close()
    } catch {
      setError(t('maps.planUnreadable'))
      return
    }

    // A replacement drawing of the same plan keeps where the old one was laid; anything else
    // starts flat in the middle of the view, a little narrower than it, ready to be dragged.
    const old = space.plan
    let corners: PlanCornersDto
    if (old && Math.abs(old.widthPx / old.heightPx - widthPx / heightPx) < 0.01) {
      corners = old.corners
    } else {
      const map = mapRef.current
      const bounds = map?.getBounds()
      const centre = map ? map.getCenter() : L.latLng(0, 0)
      const viewWidthM = bounds
        ? distance({ lat: centre.lat, lon: bounds.getWest() }, { lat: centre.lat, lon: bounds.getEast() })
        : 100
      corners = cornersAround({ lat: centre.lat, lon: centre.lng }, widthPx, heightPx, viewWidthM * 0.6)
    }

    await run(async () => {
      await dispatch(uploadSpacePlan({ spaceId, file, widthPx, heightPx, corners })).unwrap()
      setMode('plan')
    })
  }

  const commitCorners = (corners: PlanCornersDto) =>
    run(async () => {
      await dispatch(setPlanCorners({ spaceId, corners })).unwrap()
      setDraftCorners(null)
    })

  const plan = space.plan
  const planCorners = draftCorners ?? plan?.corners
  const planWidthM = plan && planCorners ? distance(planCorners.topLeft, planCorners.topRight) : 0

  const hint = pendingDiagramId
    ? t('diagramEditor.placeDiagramHint', { name: camerasById[pendingDiagramId]?.name || pendingDiagramId })
    : pendingCameraId
    ? t('maps.placeHint', { name: camerasById[pendingCameraId]?.name || pendingCameraId })
    : mode === 'landmark' ? t('maps.landmarkModeHint')
    : mode === 'plan' ? t('maps.planModeHint')
    : mode === 'point' ? t('maps.editor.pointHint')
    : mode === 'line' ? t('maps.editor.lineHint', { count: draftPoints?.length ?? 0 })
    : mode === 'polygon' ? t('maps.editor.polygonHint', { count: draftPoints?.length ?? 0 })
    : null

  // Applied a single time: placements load after the editor mounts, so this waits for the
  // camera's rather than firing on mount, and from then on the administrator's clicks own the
  // selection.
  const initialApplied = useRef(false)
  useEffect(() => {
    if (initialApplied.current || !initialCameraId) return
    const placement = here.find((p) => p.cameraId === initialCameraId)
    if (!placement) return
    initialApplied.current = true
    select('camera', initialCameraId, placement)
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `here` is rebuilt every render
  }, [initialCameraId, placements, select])

  // Arrived from "show on map" on another space: the node is picked out once its canvas has it,
  // and the map comes to it. Waiting for the canvas is what tells the page is shown, not just
  // mounted — the marker is drawn only when the space's figures have been read.
  const initialNodeApplied = useRef(false)
  useEffect(() => {
    if (initialNodeApplied.current || !initialNodeId) return
    const drawn = mapData.figures.find((figure) => figure.id === initialNodeId)
    if (!drawn) return
    initialNodeApplied.current = true
    setSelection({ kind: 'node', id: initialNodeId })
    const at = pointsCentre(drawn.geometry?.coord ?? [])
    if (at) focusOn(at)
    onInitialNodeShown?.()
    // eslint-disable-next-line react-hooks/exhaustive-deps -- the canvas is rebuilt every render
  }, [initialNodeId, mapData])

  const selectionPanel = selectedPlacement ? (() => {
    const reference = referenceOf(selectedPlacement.cameraId)
    const referenceSpace = reference && reference.spaceId !== spaceId ? spaceById(reference.spaceId) ?? null : null
    return (
      <CameraPlacementPanel
        key={selectedPlacement.cameraId}
        camera={camerasById[selectedPlacement.cameraId]}
        placement={selectedPlacement}
        nodeId={nodeForPlacement(selectedPlacement)?.id}
        node={nodeForPlacement(selectedPlacement)}
        // A plan is looked at at one scale: the layer is a map's way of hiding what is far away.
        showLayer={space.kind === 'map'}
        referenceSpace={referenceSpace}
        onDeviceDiagram={() => void openDeviceDiagram(selectedPlacement.cameraId)}
        onChange={(p) => void savePlacement(p)}
        onRemove={() => void run(async () => {
          await dispatch(removePlacement({ cameraId: selectedPlacement.cameraId, spaceId })).unwrap()
          setSelection(null)
        })}
        onCalibrate={() => setCalibrating(selectedPlacement.cameraId)}
        onGoToReference={() => referenceSpace?.id && onOpenSpace(referenceSpace.id)}
        onClose={() => setSelection(null)}
      />
    )
  })() : selectedLandmark ? (
    <LandmarkPanel
      key={selectedLandmark.id}
      landmark={selectedLandmark}
      showLayer={space.kind === 'map'}
      onChange={(m) => void run(() => dispatch(upsertLandmark(m)).unwrap())}
      onRemove={() => {
        if (!window.confirm(t('maps.confirmRemoveLandmark', { name: selectedLandmark.name }))) return
        void run(async () => {
          await dispatch(removeLandmark({ spaceId, landmarkId: selectedLandmark.id! })).unwrap()
          setSelection(null)
        })
      }}
      onClose={() => setSelection(null)}
    />
  ) : selectedNested ? (
    <FootprintPanel
      key={selectedNested.id}
      space={selectedNested}
      showLayer={space.kind === 'map'}
      onOpen={() => selectedNested.id && onOpenSpace(selectedNested.id)}
      onRemoveFootprint={() => {
        void saveFootprint(selectedNested, [])
        setSelection(null)
      }}
      onClose={() => setSelection(null)}
    />
  ) : selectedElement && selectedNode && openDiagramId ? (
    <DiagramElementEditor
      // The element as stored: a drag or a save reopens the form on the new row, not on the old
      // one it was mounted with.
      key={`${selectedElement.id}:${JSON.stringify(selectedElement)}`}
      marker={selectedNode}
      element={selectedElement}
      parentType={parentDiagramType}
      onSaved={() => void dispatch(loadDiagramContent({ id: openDiagramId, depth: diagramDepth }))}
      onClose={() => setSelection(null)}
    />
  ) : selectedNode ? (
    /* A node with nothing contextual to show — a drawn shape, a container — is edited on the
       Object tab, where every node of the canvas is edited the same way. */
    <Box sx={{ p: 2 }}>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
        {t('maps.objectOnItsTab')}
      </Typography>
      <Button size="small" variant="outlined" onClick={() => setSideTab('object')}>
        {t('maps.tabObject')}
      </Button>
    </Box>
  ) : (
    <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>{t('maps.nothingSelected')}</Typography>
  )

  /** The selected node itself — its name, look and properties — whichever panel selected it. */
  const objectPanel = selectedNode ? (
    selectedProps ? (
      <ObjectPropsEditor
        // The marker's version changes with every write, so a save reopens the form on what was
        // stored rather than leaving the old fields marked dirty.
        key={`${selectedNode.id}:${selectedProps.marker?.version ?? 0}`}
        node={selectedNode}
        figure={selectedFigure ?? null}
        loaded={selectedProps}
        levels={levels}
        showLayer={space.kind === 'map'}
        onDelete={() => void run(async () => {
          await dispatch(deleteNode(selectedNode.id)).unwrap()
          // The canvas is not the node tree: a camera's picture is drawn by its placement and a
          // landmark by the space's own list, so what is drawn here is read again rather than
          // trusted to have been dropped with the node.
          await dispatch(loadCanvas(spaceId)).unwrap()
          await dispatch(loadPlacements()).unwrap()
          if (spaceId) await dispatch(loadLandmarks(spaceId)).unwrap()
          setSelection(null)
        })}
        onClose={() => setSelection(null)}
      />
    ) : (
      <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>{t('common.loading')}</Typography>
    )
  ) : (
    <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>{t('maps.nothingSelected')}</Typography>
  )

  // The plan's picture is a property of the space, so its controls live with the space's other
  // settings; only lining it up is a map tool, since that is done on the map.
  const planPicture = space.kind === 'plan' && (
    <Box sx={{ px: 2, py: 1.5 }}>
      <Divider sx={{ mb: 1.5 }} />
      <Typography variant="subtitle2" gutterBottom>{t('maps.planPicture')}</Typography>
      <Stack spacing={1} alignItems="flex-start">
        <Button size="small" startIcon={<ImageIcon />} onClick={() => fileRef.current?.click()}>
          {plan ? t('maps.replacePlan') : t('maps.uploadPlan')}
        </Button>
        <input
          ref={fileRef}
          type="file"
          accept="image/png,image/jpeg,image/webp"
          hidden
          onChange={(e) => {
            const file = e.target.files?.[0]
            e.target.value = ''
            if (file) void handlePlanFile(file)
          }}
        />
        {plan && planCorners && (
          <NumberField
            label={t('maps.planWidth')}
            value={Math.round(planWidthM * 10) / 10}
            min={0.1}
            onCommit={(metres) => void commitCorners(
              rescaleCorners(planCorners, plan.widthPx, plan.heightPx, { x: 0, y: 0 }, { x: plan.widthPx, y: 0 }, metres))}
            sx={{ width: 160 }}
          />
        )}
        {plan && (
          <Button
            size="small"
            color="inherit"
            startIcon={<DeleteOutlineIcon />}
            onClick={() => {
              if (window.confirm(t('maps.confirmRemovePlan'))) {
                void run(() => dispatch(removeSpacePlan(spaceId)).unwrap())
                setMode('select')
              }
            }}
          >
            {t('maps.removePlan')}
          </Button>
        )}
        {space.realCoordinates && (
          <Tooltip title={t('maps.alignOnMapHint')}>
            <FormControlLabel
              control={<Switch size="small" checked={alignOnMap} onChange={(e) => setAlignOnMap(e.target.checked)} />}
              label={<Typography variant="body2">{t('maps.alignOnMap')}</Typography>}
            />
          </Tooltip>
        )}
      </Stack>
    </Box>
  )

  return (
    <Box sx={{ flex: 1, minHeight: 0, display: 'flex' }}>
      <Box sx={{ flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
        {/* The map's own tools sit on the map's edge: they act on what is drawn below them. A
            diagram in the map's place has its own header, so they step aside. */}
        {!openDiagramId && (
        <Stack direction="row" spacing={1} alignItems="center" sx={{ px: 1, py: 0.5, flexShrink: 0, minHeight: 44 }}>
          <ToggleButtonGroup
            size="small"
            exclusive
            value={mode}
            onChange={(_, v: Mode | null) => {
              if (!v) return
              setMode(v)
              setPendingCameraId(null)
              setPendingDiagramId(null)
              setDraftPoints(null)
            }}
          >
            <ToggleButton value="select"><Tooltip title={t('maps.modeSelect')}><PanToolIcon fontSize="small" /></Tooltip></ToggleButton>
            <ToggleButton value="landmark"><Tooltip title={t('maps.modeLandmark')}><PlaceIcon fontSize="small" /></Tooltip></ToggleButton>
            <ToggleButton value="point"><Tooltip title={t('maps.editor.modePoint')}><AdjustIcon fontSize="small" /></Tooltip></ToggleButton>
            <ToggleButton value="line"><Tooltip title={t('maps.editor.modeLine')}><TimelineIcon fontSize="small" /></Tooltip></ToggleButton>
            <ToggleButton value="polygon"><Tooltip title={t('maps.editor.modePolygon')}><PentagonOutlinedIcon fontSize="small" /></Tooltip></ToggleButton>
            <ToggleButton value="plan" disabled={!plan}><Tooltip title={t('maps.modePlan')}><CropRotateIcon fontSize="small" /></Tooltip></ToggleButton>
          </ToggleButtonGroup>
          <Tooltip title={t('maps.saveView')}>
            <Button size="small" color="inherit" onClick={() => void saveView()}>
              <CenterFocusStrongIcon fontSize="small" />
            </Button>
          </Tooltip>
          {hint && (
            <Typography variant="caption" color="info.main" sx={{ ml: 1 }} noWrap>
              {hint}
            </Typography>
          )}
          {(pendingCameraId || pendingDiagramId) && (
            <Button size="small" onClick={() => { setPendingCameraId(null); setPendingDiagramId(null) }}>
              {t('common.cancel')}
            </Button>
          )}
          {draftPoints && (mode === 'line' || mode === 'polygon') && (
            <>
              <Button size="small" variant="outlined" onClick={finishDrawing}>{t('maps.editor.finish')}</Button>
              <Button size="small" onClick={() => setDraftPoints(null)}>{t('common.cancel')}</Button>
            </>
          )}
        </Stack>
        )}
        {error && <Alert severity="error" onClose={() => setError(null)} sx={{ mx: 1, mb: 0.5 }}>{error}</Alert>}

        {openDiagramId ? (
          <DiagramViewer
            diagramId={openDiagramId}
            selectedId={selectedNodeId}
            editing
            onPick={(marker) => setSelection({ kind: 'node', id: marker.id })}
            onOpen={(marker) => {
              if (marker.nodeKind === 'space') { openDiagram(null); onOpenSpace(marker.id) }
              else selectNode(marker.id)
            }}
            onCreated={(marker) => { setSelection({ kind: 'node', id: marker.id }); openDiagram(marker.id) }}
            onDeleted={() => { openDiagram(null); setSelection(null) }}
          />
        ) : (
          <SpaceCanvas
            key={spaceId}
            space={space}
            alignOnMap={alignOnMap}
            onMapClick={handleMapClick}
            picking={picking}
            focus={focus}
            overlay={<SpaceNav spaces={spaces} space={space} />}
            onNodeDrop={(at, nodeId) => void run(async () => {
              const marker = await dispatch(createReplicaAt({
                ownerId: nodeId, parentId: spaceId, lat: at.lat, lon: at.lon,
              })).unwrap()
              setSelection({ kind: 'node', id: marker.id })
            })}
          >
            <MapBridge mapRef={mapRef} />
            {plan && (
              <PlanOverlay
                space={space}
                corners={planCorners}
                editable={mode === 'plan'}
                onCornersChange={(c, commit) => (commit ? void commitCorners(c) : setDraftCorners(c))}
              />
            )}
            <FiguresLayer
              figures={mapData.figures.map((figure) => (draftFigure?.id === figure.id ? draftFigure : figure))}
              states={{}}
              alarms={{}}
              descriptions={{}}
              levels={levels}
              selectedId={selectedNodeId}
              onSelect={selectOnCanvas}
              // A camera placed here is drawn by its marker, not by the shape the node carries. A
              // diagram that is a replica of a camera is not that picture: it is a node of its own
              // on this canvas, and hiding it hid the whole readings panel. So only the replica
              // standing exactly on a placement is skipped.
              skipIds={new Set(here
                .map((placement) => figureAtPlacement(mapData.figures, placement)?.id)
                .filter((id): id is string => !!id))}
              editable={mode === 'select'}
              interactive={!picking}
              onGeometryChange={handleGeometryChange}
            />
            {draftPoints && <DrawingPreview points={draftPoints} closed={mode === 'polygon'} />}
            {(mode === 'line' || mode === 'polygon') && <FinishOnDoubleClick onFinish={() => finishRef.current()} />}
            <Footprints
              nested={nested.map((s) => (s.id === selectedNested?.id && draftFootprint ? { ...s, footprint: draftFootprint } : s))}
              selectedId={selectionNodeId}
              levels={levels}
              zoomLevels={spaceZoomLevels}
              interactive={!picking}
              onPick={(s) => s.id && setSelection({ kind: 'footprint', id: s.id })}
            />
            {selectedNested && mode === 'select' && (
              <FootprintEditor
                footprint={draftFootprint ?? selectedNested.footprint}
                onChange={(f, commit) => (commit ? void saveFootprint(selectedNested, f) : setDraftFootprint(f))}
              />
            )}
            {here.map((stored) => {
              const p = draft?.cameraId === stored.cameraId ? draft : stored
              const camera = camerasById[p.cameraId]
              // The node behind the placement carries the camera's scale layer.
              const node = nodeForPlacement(p)
              return (
                <CameraMarker
                  key={p.cameraId}
                  placement={p}
                  label={camera?.name || p.cameraId}
                  color={STATUS_COLOR[cameraMapStatus(camera)]}
                  // The picture is the placement node's own: the map turns it by the heading, as it
                  // turns its dot.
                  visual={node ? figuresById[node.id]?.visual : undefined}
                  // A marker is the picture the placement draws: the one node, not every picture
                  // of that camera.
                  selected={selection?.kind === 'camera'
                    ? selection.id === p.cameraId
                    : nodeForPlacement(p)?.id === selectedNodeId}
                  // The device's diagram is a link beside the picked camera, not a node of its own:
                  // clicking it opens the diagram, and the plan stays one point per camera.
                  diagramLink={deviceDiagram && node?.id === selectedNodeId
                    ? {
                        label: t('diagramEditor.openDeviceDiagram'),
                        visual: deviceDiagram.visual,
                        onOpen: () => selectNode(deviceDiagram.diagram.id),
                      }
                    : undefined}
                  editable={mode === 'select'}
                  interactive={!picking}
                  levels={levels}
                  zoomLevel={node ? figuresById[node.id]?.zoomLevel : undefined}
                  onSelect={() => {
                    const node = nodeForPlacement(p)
                    setSelection(node ? { kind: 'node', id: node.id } : { kind: 'camera', id: p.cameraId })
                  }}
                  onChange={(next, commit) => (commit ? void savePlacement(next) : setDraft(next))}
                />
              )
            })}
            {landmarks.map((m) => (
              <LandmarkMarker
                key={m.id}
                landmark={m}
                // The node whichever way it was picked: a row in the tree selects the node, the
                // map itself selects the landmark.
                selected={selectionNodeId === m.id}
                // A landmark is picked while selecting and while placing one; the drawing tools
                // and a camera waiting for its spot take the click instead.
                interactive={!pendingCameraId && (mode === 'select' || mode === 'landmark')}
                editable={mode === 'select'}
                levels={levels}
                zoomLevel={m.id ? figuresById[m.id]?.zoomLevel : undefined}
                onSelect={() => m.id && setSelection({ kind: 'landmark', id: m.id })}
                onMove={(lat, lon) => void run(() => dispatch(upsertLandmark({ ...m, lat, lon })).unwrap())}
              />
            ))}
          </SpaceCanvas>
        )}
      </Box>

      <Box sx={{ width: SIDE_WIDTH, flexShrink: 0, borderLeft: 1, borderColor: 'divider', display: 'flex', flexDirection: 'column', minHeight: 0 }}>
        <Stack
          direction="row"
          alignItems="center"
          sx={{ borderBottom: 1, borderColor: 'divider', flexShrink: 0 }}
        >
          {/* Four labels in a 300px column: the tabs share the width and keep their own letter
              size, so the last one is never the one that falls off the edge. */}
          <Tabs
            value={sideTab}
            onChange={(_, v: SideTab) => setSideTab(v)}
            variant="fullWidth"
            sx={{
              flex: 1, minWidth: 0, minHeight: 40,
              '& .MuiTab-root': { minHeight: 40, minWidth: 0, px: 0.5, fontSize: 11, textTransform: 'none' },
            }}
          >
            <Tab value="space" label={t('maps.tabSpace')} />
            <Tab value="selection" label={t('maps.tabSelection')} />
            <Tab value="object" label={t('maps.tabObject')} />
            <Tab value="rights" label={t('rights.tab')} />
          </Tabs>
          {/* Both ways round for the node the panel is about: where it stands among its
              neighbours in the tree, and where it is drawn on the plan. A space is the canvas
              itself, not a row of its own tree. */}
          {selectionNodeId && selection?.kind !== 'footprint' ? (
            <>
              {selectionDrawn && !openDiagramId ? (
                <Tooltip title={t('maps.node.locate')}>
                  <IconButton
                    size="small"
                    aria-label={t('maps.node.locate')}
                    onClick={() => void showOnCanvas(selectionNodeId)}
                  >
                    <CenterFocusStrongIcon fontSize="small" />
                  </IconButton>
                </Tooltip>
              ) : null}
              <Tooltip title={t('maps.node.showInTree')}>
                <IconButton
                  size="small"
                  aria-label={t('maps.node.showInTree')}
                  onClick={() => showInTree(selectionNodeId)}
                  sx={{ mr: 0.5 }}
                >
                  <AccountTreeIcon fontSize="small" />
                </IconButton>
              </Tooltip>
            </>
          ) : null}
        </Stack>
        <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
          {/* Both stay mounted: the space's form keeps its unsaved edits while an object is looked at. */}
          <Box sx={{ display: sideTab === 'space' ? 'block' : 'none' }}>
            {settings}
            {planPicture}
          </Box>
          {sideTab === 'selection' && (
            <>
              {/* Whether a node is a child of a container is the tree's business, not the map's. */}
              {selectionNodeId && (
                <Box sx={{ px: 1.5, pt: 1.5 }}>
                  <MoveToField nodeId={selectionNodeId} canvasId={spaceId} />
                </Box>
              )}
              {selectionPanel}
            </>
          )}
          {/* The rights of what is selected — or of the space itself, when nothing else is. */}
          {sideTab === 'object' && objectPanel}
          {sideTab === 'rights' && <ObjectRights nodeId={selectionNodeId ?? spaceId} />}
        </Box>
      </Box>

      {/* A PTZ learns where to point; a fixed camera, where its picture lies on the ground. */}
      {calibrating && (camerasById[calibrating]?.hasTelemetry ? (
        <PtzGeoCalibrationDialog
          cameraId={calibrating}
          space={space}
          landmarks={landmarks}
          onClose={() => setCalibrating(null)}
        />
      ) : (
        <GroundCalibrationDialog
          cameraId={calibrating}
          space={space}
          landmarks={landmarks}
          onClose={() => setCalibrating(null)}
        />
      ))}
    </Box>
  )
}

/** Hands the Leaflet map out of the MapContainer, for the things done outside it: sizing a new plan, saving the view. */
function MapBridge({ mapRef }: { mapRef: React.RefObject<L.Map | null> }) {
  const map = useMap()
  useEffect(() => {
    mapRef.current = map
    return () => { mapRef.current = null }
  }, [map, mapRef])
  return null
}
