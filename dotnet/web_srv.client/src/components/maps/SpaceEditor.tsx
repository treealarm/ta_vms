import { useEffect, useMemo, useRef, useState } from 'react'
import {
  Alert, Box, Button, Divider, FormControlLabel, Stack, Switch, Tab, Tabs, ToggleButton,
  ToggleButtonGroup, Tooltip, Typography,
} from '@mui/material'
import PanToolIcon from '@mui/icons-material/PanToolAlt'
import PlaceIcon from '@mui/icons-material/Place'
import ImageIcon from '@mui/icons-material/Image'
import CropRotateIcon from '@mui/icons-material/CropRotate'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import CenterFocusStrongIcon from '@mui/icons-material/CenterFocusStrong'
import { useMap } from 'react-leaflet'
import L from 'leaflet'
import { useTranslation } from 'react-i18next'
import type { CameraPlacementDto, GeoPointDto, PlanCornersDto, SpaceDto } from '../../dto/dtos'
import { useAppDispatch, useAppSelector } from '../../store'
import {
  errorText, loadLandmarks, loadPlacements, removeLandmark, removePlacement, removeSpacePlan, setPlanCorners,
  upsertLandmark, upsertPlacement, upsertSpace, uploadSpacePlan,
} from '../../store/spatialSlice'
import SpaceCanvas from '../../map/SpaceCanvas'
import PlanOverlay from '../../map/PlanOverlay'
import { CameraMarker, LandmarkMarker } from '../../map/markers'
import { FootprintEditor, Footprints } from '../../map/footprints'
import { cameraMapStatus, STATUS_COLOR } from '../../map/cameraStatus'
import { cornersAround, distance, rescaleCorners } from '../../map/geo'
import { CameraPlacementPanel, FootprintPanel, LandmarkPanel } from './PlacementPanels'
import NumberField from './NumberField'
import SpaceNav from './SpaceNav'
import PtzGeoCalibrationDialog from './PtzGeoCalibrationDialog'
import GroundCalibrationDialog from './GroundCalibrationDialog'
import { useEditorState, type Mode, type SideTab } from './editorState'
import { childrenOf } from './spatialUtils'

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
  /** Moves the whole page to another space — the tree beside it follows. */
  onOpenSpace: (spaceId: string) => void
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
export default function SpaceEditor({ space, settings, initialCameraId, onOpenSpace }: Props) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const camerasById = useAppSelector((s) => s.live.cameraStatesById)
  const placements = useAppSelector((s) => s.spatial.placements)
  const spaces = useAppSelector((s) => s.spatial.spaces)
  const loadedLandmarks = useAppSelector((s) => s.spatial.landmarksBySpace[space.id ?? ''])
  const landmarks = useMemo(() => loadedLandmarks ?? [], [loadedLandmarks])

  const {
    mode, setMode, selection, setSelection, pendingCameraId, setPendingCameraId, focus, select,
    sideTab, setSideTab,
  } = useEditorState()
  const [draft, setDraft] = useState<CameraPlacementDto | null>(null)
  const [draftCorners, setDraftCorners] = useState<PlanCornersDto | null>(null)
  const [draftFootprint, setDraftFootprint] = useState<GeoPointDto[] | null>(null)
  const [alignOnMap, setAlignOnMap] = useState(false)
  const [calibrating, setCalibrating] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const mapRef = useRef<L.Map | null>(null)
  const fileRef = useRef<HTMLInputElement>(null)

  const spaceId = space.id ?? ''
  useEffect(() => {
    void dispatch(loadPlacements())
    if (spaceId) void dispatch(loadLandmarks(spaceId))
  }, [dispatch, spaceId])

  const here = placements.filter((p) => p.spaceId === spaceId)
  const nested = childrenOf(spaces, spaceId)
  const placementHere = (cameraId: string) => here.find((p) => p.cameraId === cameraId)
  const referenceOf = (cameraId: string) => placements.find((p) => p.cameraId === cameraId && p.isReference)
  const spaceById = (id: string | undefined) => spaces.find((s) => s.id === id)
  const selectedPlacement = selection?.kind === 'camera' ? placementHere(selection.id) : undefined
  const selectedLandmark = selection?.kind === 'landmark' ? landmarks.find((m) => m.id === selection.id) : undefined
  const selectedNested = selection?.kind === 'footprint' ? nested.find((s) => s.id === selection.id) : undefined

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
      await dispatch(upsertPlacement(p)).unwrap()
      setDraft(null)
    })

  const saveFootprint = (of: SpaceDto, footprint: GeoPointDto[]) =>
    run(async () => {
      await dispatch(upsertSpace({ ...of, footprint })).unwrap()
      setDraftFootprint(null)
    })

  const handleMapClick = (at: GeoPointDto) => {
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

  const hint = pendingCameraId
    ? t('maps.placeHint', { name: camerasById[pendingCameraId]?.name || pendingCameraId })
    : mode === 'landmark' ? t('maps.landmarkModeHint')
    : mode === 'plan' ? t('maps.planModeHint')
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

  const selectionPanel = selectedPlacement ? (() => {
    const reference = referenceOf(selectedPlacement.cameraId)
    const referenceSpace = reference && reference.spaceId !== spaceId ? spaceById(reference.spaceId) ?? null : null
    return (
      <CameraPlacementPanel
        key={selectedPlacement.cameraId}
        camera={camerasById[selectedPlacement.cameraId]}
        placement={selectedPlacement}
        referenceSpace={referenceSpace}
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
      onOpen={() => selectedNested.id && onOpenSpace(selectedNested.id)}
      onRemoveFootprint={() => {
        void saveFootprint(selectedNested, [])
        setSelection(null)
      }}
      onClose={() => setSelection(null)}
    />
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
        {/* The map's own tools sit on the map's edge: they act on what is drawn below them. */}
        <Stack direction="row" spacing={1} alignItems="center" sx={{ px: 1, py: 0.5, flexShrink: 0, minHeight: 44 }}>
          <ToggleButtonGroup
            size="small"
            exclusive
            value={mode}
            onChange={(_, v: Mode | null) => { if (v) { setMode(v); setPendingCameraId(null) } }}
          >
            <ToggleButton value="select"><Tooltip title={t('maps.modeSelect')}><PanToolIcon fontSize="small" /></Tooltip></ToggleButton>
            <ToggleButton value="landmark"><Tooltip title={t('maps.modeLandmark')}><PlaceIcon fontSize="small" /></Tooltip></ToggleButton>
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
          {pendingCameraId && <Button size="small" onClick={() => setPendingCameraId(null)}>{t('common.cancel')}</Button>}
        </Stack>
        {error && <Alert severity="error" onClose={() => setError(null)} sx={{ mx: 1, mb: 0.5 }}>{error}</Alert>}

        <SpaceCanvas
          key={spaceId}
          space={space}
          alignOnMap={alignOnMap}
          onMapClick={handleMapClick}
          picking={!!pendingCameraId || mode === 'landmark'}
          focus={focus}
          overlay={<SpaceNav spaces={spaces} space={space} />}
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
          <Footprints
            nested={nested.map((s) => (s.id === selectedNested?.id && draftFootprint ? { ...s, footprint: draftFootprint } : s))}
            selectedId={selectedNested?.id}
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
            return (
              <CameraMarker
                key={p.cameraId}
                placement={p}
                label={camera?.name || p.cameraId}
                color={STATUS_COLOR[cameraMapStatus(camera)]}
                selected={selection?.kind === 'camera' && selection.id === p.cameraId}
                editable={mode === 'select'}
                onSelect={() => setSelection({ kind: 'camera', id: p.cameraId })}
                onChange={(next, commit) => (commit ? void savePlacement(next) : setDraft(next))}
              />
            )
          })}
          {landmarks.map((m) => (
            <LandmarkMarker
              key={m.id}
              landmark={m}
              selected={selection?.kind === 'landmark' && selection.id === m.id}
              editable={mode !== 'plan'}
              onSelect={() => m.id && setSelection({ kind: 'landmark', id: m.id })}
              onMove={(lat, lon) => void run(() => dispatch(upsertLandmark({ ...m, lat, lon })).unwrap())}
            />
          ))}
        </SpaceCanvas>
      </Box>

      <Box sx={{ width: SIDE_WIDTH, flexShrink: 0, borderLeft: 1, borderColor: 'divider', display: 'flex', flexDirection: 'column', minHeight: 0 }}>
        <Tabs
          value={sideTab}
          onChange={(_, v: SideTab) => setSideTab(v)}
          variant="fullWidth"
          sx={{ borderBottom: 1, borderColor: 'divider', flexShrink: 0, minHeight: 40, '& .MuiTab-root': { minHeight: 40 } }}
        >
          <Tab value="space" label={t('maps.tabSpace')} />
          <Tab value="selection" label={t('maps.tabSelection')} />
        </Tabs>
        <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
          {/* Both stay mounted: the space's form keeps its unsaved edits while an object is looked at. */}
          <Box sx={{ display: sideTab === 'space' ? 'block' : 'none' }}>
            {settings}
            {planPicture}
          </Box>
          {sideTab === 'selection' && selectionPanel}
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
