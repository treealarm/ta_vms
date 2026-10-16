import { useEffect, useMemo, useRef, useState } from 'react'
import {
  Alert, Box, Button, Chip, Divider, FormControlLabel, Stack, Switch, ToggleButton,
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
import SpaceCanvas, { type MapFocus } from '../../map/SpaceCanvas'
import PlanOverlay from '../../map/PlanOverlay'
import { CameraMarker, LandmarkMarker } from '../../map/markers'
import { FootprintEditor, Footprints } from '../../map/footprints'
import { cameraMapStatus, STATUS_COLOR } from '../../map/cameraStatus'
import { cornersAround, distance, rescaleCorners } from '../../map/geo'
import { CameraPlacementPanel, FootprintPanel, LandmarkPanel } from './PlacementPanels'
import NumberField from './NumberField'
import SpaceNav from './SpaceNav'
import MapObjectList, { type MapObjectSection } from './MapObjectList'
import PtzGeoCalibrationDialog from './PtzGeoCalibrationDialog'
import GroundCalibrationDialog from './GroundCalibrationDialog'
import { childrenOf } from './spatialUtils'

/** What the next click on the map does. */
type Mode = 'select' | 'landmark' | 'plan'

type Selection = { kind: 'camera' | 'landmark' | 'footprint'; id: string } | null

/** Mirrors VmsCfg's limit, so an oversized plan is refused before it is uploaded. */
const MAX_PLAN_BYTES = 20 * 1024 * 1024

const DEFAULT_PLACEMENT = { heightM: 3, headingDeg: 0, hfovDeg: 60, rangeM: 30 }

type Props = {
  space: SpaceDto
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
 */
export default function SpaceEditor({ space, initialCameraId, onOpenSpace }: Props) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const camerasById = useAppSelector((s) => s.live.cameraStatesById)
  const placements = useAppSelector((s) => s.spatial.placements)
  const spaces = useAppSelector((s) => s.spatial.spaces)
  const loadedLandmarks = useAppSelector((s) => s.spatial.landmarksBySpace[space.id ?? ''])
  const landmarks = useMemo(() => loadedLandmarks ?? [], [loadedLandmarks])

  const [mode, setMode] = useState<Mode>('select')
  const [pendingCameraId, setPendingCameraId] = useState<string | null>(null)
  const [selection, setSelection] = useState<Selection>(null)
  const [draft, setDraft] = useState<CameraPlacementDto | null>(null)
  const [draftCorners, setDraftCorners] = useState<PlanCornersDto | null>(null)
  const [draftFootprint, setDraftFootprint] = useState<GeoPointDto[] | null>(null)
  const [alignOnMap, setAlignOnMap] = useState(false)
  const [calibrating, setCalibrating] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [focus, setFocus] = useState<MapFocus | null>(null)
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

  const cameras = Object.values(camerasById).sort((a, b) => (a.name || a.cameraId).localeCompare(b.name || b.cameraId))

  const select = (kind: 'camera' | 'landmark' | 'footprint', id: string, at: GeoPointDto) => {
    setMode('select')
    setPendingCameraId(null)
    setSelection({ kind, id })
    setFocus((f) => ({ lat: at.lat, lon: at.lon, seq: (f?.seq ?? 0) + 1 }))
  }

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
    // eslint-disable-next-line react-hooks/exhaustive-deps -- `select` and `here` are rebuilt every render
  }, [initialCameraId, placements])

  const listSections: MapObjectSection[] = [
    {
      title: t('maps.camerasHere'),
      empty: t('maps.noCamerasHere'),
      items: cameras.filter((c) => placementHere(c.cameraId)).map((c) => {
        const p = placementHere(c.cameraId)!
        return {
          key: c.cameraId,
          primary: c.name || c.cameraId,
          secondary: p.isReference ? t('maps.referenceShort') : t('maps.pictureShort'),
          dot: STATUS_COLOR[cameraMapStatus(c)],
          onClick: () => select('camera', c.cameraId, p),
        }
      }),
    },
    {
      title: t('maps.inside'),
      hint: t('maps.insideHint'),
      empty: t('maps.nothingInside'),
      items: nested.map((s) => ({
        key: s.id ?? s.name,
        primary: s.name,
        secondary: s.footprint.length >= 3 ? undefined : t('maps.noFootprint'),
        onClick: () => s.id && onOpenSpace(s.id),
      })),
    },
    {
      title: t('maps.landmarks'),
      hint: t('maps.landmarksPurpose'),
      empty: t('maps.noLandmarks'),
      items: landmarks.map((m) => ({
        key: m.id ?? m.name,
        primary: m.name,
        secondary: t('maps.landmarkHeightShort', { height: m.heightM }),
        onClick: () => m.id && select('landmark', m.id, m),
      })),
    },
    {
      title: t('maps.camerasToPlace'),
      hint: t('maps.camerasHint'),
      items: cameras.filter((c) => !placementHere(c.cameraId)).map((c) => {
        const reference = referenceOf(c.cameraId)
        return {
          key: c.cameraId,
          primary: c.name || c.cameraId,
          secondary: (
            <Chip
              size="small"
              variant="outlined"
              label={reference ? t('maps.standsIn', { name: spaceById(reference.spaceId)?.name ?? '?' }) : t('maps.notPlaced')}
              sx={{ mt: 0.25 }}
            />
          ),
          selected: pendingCameraId === c.cameraId,
          onClick: () => {
            setMode('select')
            setPendingCameraId(c.cameraId)
          },
        }
      }),
    },
  ]

  const panel = selectedPlacement ? (() => {
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
    <MapObjectList sections={listSections} />
  )

  return (
    <Box sx={{ flex: 1, minHeight: 0, display: 'flex', borderTop: 1, borderColor: 'divider' }}>
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
          {space.kind === 'plan' && (
            <>
              <Divider orientation="vertical" flexItem />
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
            </>
          )}
          {plan && mode === 'plan' && planCorners && (
            <NumberField
              label={t('maps.planWidth')}
              value={Math.round(planWidthM * 10) / 10}
              min={0.1}
              onCommit={(metres) => void commitCorners(
                rescaleCorners(planCorners, plan.widthPx, plan.heightPx, { x: 0, y: 0 }, { x: plan.widthPx, y: 0 }, metres))}
              sx={{ width: 140 }}
            />
          )}
          {plan && (
            <Tooltip title={t('maps.removePlan')}>
              <Button
                size="small"
                color="inherit"
                onClick={() => {
                  if (window.confirm(t('maps.confirmRemovePlan'))) {
                    void run(() => dispatch(removeSpacePlan(spaceId)).unwrap())
                    setMode('select')
                  }
                }}
              >
                <DeleteOutlineIcon fontSize="small" />
              </Button>
            </Tooltip>
          )}
          {space.kind === 'plan' && space.realCoordinates && (
            <Tooltip title={t('maps.alignOnMapHint')}>
              <FormControlLabel
                sx={{ ml: 0.5 }}
                control={<Switch size="small" checked={alignOnMap} onChange={(e) => setAlignOnMap(e.target.checked)} />}
                label={<Typography variant="caption">{t('maps.alignOnMap')}</Typography>}
              />
            </Tooltip>
          )}
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
          overlay={<SpaceNav spaces={spaces} space={space} onOpen={(id) => { setFocus(null); onOpenSpace(id) }} />}
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

      <Box sx={{ width: 280, flexShrink: 0, borderLeft: 1, borderColor: 'divider', overflowY: 'auto' }}>
        {panel}
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
