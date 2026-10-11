import { useEffect, useState } from 'react'
import { Alert, Box, MenuItem, Stack, TextField } from '@mui/material'
import { useMap, useMapEvents } from 'react-leaflet'
import { useTranslation } from 'react-i18next'
import type { GeoBoundsDto, SpaceDto, SpaceKind } from '../../dto/dtos'
import { useAppDispatch, useAppSelector } from '../../store'
import { errorText, removeSpace, upsertSpace } from '../../store/spatialSlice'
import SpaceCanvas from '../../map/SpaceCanvas'
import { boundsAround, defaultFootprint } from '../../map/geo'
import DetailToolbar from '../settings/DetailToolbar'
import { useDirtyGuard, type ApplyIfDirtyHandle } from '../settings/useDirtyGuard'
import { childrenOf, parseLatLon, pathTo, treeOf } from './spatialUtils'

/** What is being created; absent when an existing space is edited. */
export type NewSpace = { kind: SpaceKind; parent: SpaceDto | null }

type Props = {
  ref?: React.Ref<ApplyIfDirtyHandle>
  /** The space being edited, or null when one is being created as `creating` says. */
  space: SpaceDto | null
  creating?: NewSpace
  onSaved: (space: SpaceDto, created: boolean) => void
  onRemoved: () => void
}

const WORLD: GeoBoundsDto = { southWest: { lat: -60, lon: -170 }, northEast: { lat: 75, lon: 170 } }

// A plan with nothing to go by opens on this much around its middle, in metres each way.
const DEFAULT_HALF_VIEW_M = 50

/** Everything the given spaces show at once; the whole world when there are none. */
function unionOf(views: GeoBoundsDto[]): GeoBoundsDto {
  if (views.length === 0) return WORLD
  return {
    southWest: { lat: Math.min(...views.map((v) => v.southWest.lat)), lon: Math.min(...views.map((v) => v.southWest.lon)) },
    northEast: { lat: Math.max(...views.map((v) => v.northEast.lat)), lon: Math.max(...views.map((v) => v.northEast.lon)) },
  }
}

const boundsOf = (points: { lat: number; lon: number }[]): GeoBoundsDto => unionOf(
  points.map((p) => ({ southWest: p, northEast: p })))

/** "Plan 3" — the first such name not taken among its siblings, so a space can be added without typing. */
function freeName(base: string, siblings: SpaceDto[]): string {
  const taken = new Set(siblings.map((s) => s.name))
  for (let n = siblings.length + 1; ; n++) if (!taken.has(`${base} ${n}`)) return `${base} ${n}`
}

/**
 * Where a new space opens. A map is framed by the administrator on the picker. A plan in made-up
 * coordinates opens around its own zero. A plan in real coordinates opens where it is in the world:
 * over its footprint when its parent is real too, otherwise around the real spaces there already are.
 */
function initialView(creating: NewSpace, real: boolean, footprint: { lat: number; lon: number }[], spaces: SpaceDto[]): GeoBoundsDto {
  if (!real) return boundsAround({ lat: 0, lon: 0 }, DEFAULT_HALF_VIEW_M)
  if (creating.parent?.realCoordinates && footprint.length) return boundsOf(footprint)
  return unionOf(spaces.filter((s) => s.realCoordinates).map((s) => s.view))
}

/**
 * Name and settings of one space. A new map is framed on a world map below the fields — what is in
 * view when it is added is what it opens on; a new plan is laid out in its own editor once it exists.
 */
export function SpaceForm({ ref, space, creating, onSaved, onRemoved }: Props) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const spaces = useAppSelector((st) => st.spatial.spaces)
  const tilesEnabled = useAppSelector((st) => st.frontendConfig.mapTilesEnabled)
  const isNew = space === null
  const kind: SpaceKind = space?.kind ?? creating?.kind ?? 'plan'
  const parent = isNew ? creating?.parent ?? null : spaces.find((s) => s.id === space.parentId) ?? null
  const siblings = parent?.id ? childrenOf(spaces, parent.id) : spaces.filter((s) => !s.parentId)

  const [snap, setSnap] = useState<SpaceDto | null>(space)
  const [name, setName] = useState(space?.name ?? freeName(kind === 'map' ? t('maps.kindMap') : t('maps.kindPlan'), siblings))
  const [real, setReal] = useState(space?.realCoordinates ?? kind === 'map')
  const [ordinal, setOrdinal] = useState(String(space?.ordinal ?? siblings.length))
  const [elevation, setElevation] = useState(String(space?.elevationM ?? 0))
  // The picker for a new map, read once at mount so moving it does not rebuild its tile layer.
  const [pickerSpace] = useState<SpaceDto>(() => ({
    name: '', kind: 'map', realCoordinates: true, ordinal: 0, elevationM: 0, footprint: [],
    view: unionOf(spaces.filter((s) => s.realCoordinates).map((s) => s.view)),
  }))
  const [pickerView, setPickerView] = useState<GeoBoundsDto>(pickerSpace.view)
  const [goTo, setGoTo] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const picking = isNew && kind === 'map' && tilesEnabled
  const ordinalN = Number(ordinal)
  const elevationM = Number(elevation.replace(',', '.'))
  const valid = name.trim() !== '' && Number.isInteger(ordinalN) && Number.isFinite(elevationM)
  const dirty = isNew
    ? valid
    : valid && !!snap && (name !== snap.name || real !== snap.realCoordinates || ordinalN !== snap.ordinal || elevationM !== snap.elevationM)

  const apply = async (): Promise<boolean> => {
    if (!dirty) return true
    setBusy(true)
    setError(null)
    try {
      let next: SpaceDto
      if (space) {
        next = { ...space, name: name.trim(), realCoordinates: real, ordinal: ordinalN, elevationM }
      } else {
        const footprint = parent ? defaultFootprint(parent) : []
        next = {
          parentId: parent?.id ?? null,
          name: name.trim(),
          kind,
          realCoordinates: kind === 'map' || real,
          ordinal: ordinalN,
          elevationM,
          footprint,
          view: kind === 'map' ? pickerView : initialView({ kind, parent }, real, footprint, spaces),
        }
      }
      const saved = await dispatch(upsertSpace(next)).unwrap()
      setSnap(saved)
      onSaved(saved, isNew)
      return true
    } catch (e) {
      setError(errorText(e))
      return false
    } finally {
      setBusy(false)
    }
  }

  const remove = async () => {
    if (!space?.id) return
    // Everything nested goes with it; say how much, since it may be far more than this one plan.
    const nested = treeOf(spaces).filter(({ space: s }) => s.id !== space.id && pathTo(spaces, s.id ?? '').some((p) => p.id === space.id)).length
    if (!window.confirm(t('maps.confirmRemoveSpace', { name: space.name, count: nested }))) return
    setBusy(true)
    try {
      await dispatch(removeSpace(space.id)).unwrap()
      onRemoved()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  useDirtyGuard(ref, dirty, apply)

  const title = space ? space.name
    : kind === 'map' ? t('maps.newMap')
    : parent ? t('maps.newNestedPlan', { name: parent.name }) : t('maps.newPlan')

  return (
    <Box
      component="form"
      onSubmit={(e) => { e.preventDefault(); void apply() }}
      sx={picking ? { flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' } : { flexShrink: 0 }}
    >
      <DetailToolbar
        title={title}
        subtitle={space ? (kind === 'map' ? t('maps.kindMap') : t('maps.kindPlan')) : undefined}
        dirty={dirty}
        busy={busy}
        applyLabel={isNew ? t('common.add') : undefined}
        onCancel={!snap ? undefined : () => {
          setName(snap.name)
          setReal(snap.realCoordinates)
          setOrdinal(String(snap.ordinal))
          setElevation(String(snap.elevationM))
        }}
        onDelete={isNew ? undefined : () => void remove()}
        deleteLabel={t('maps.removeSpace')}
      />
      <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap sx={{ px: 2, pt: 1.5, pb: 1 }}>
        <TextField label={t('common.name')} value={name} onChange={(e) => setName(e.target.value)} size="small" required sx={{ width: 220 }} />
        {kind === 'plan' && (
          <TextField
            select
            label={t('maps.coordinates')}
            value={real ? 'real' : 'made-up'}
            onChange={(e) => setReal(e.target.value === 'real')}
            size="small"
            helperText={real ? t('maps.coordinatesRealHint') : t('maps.coordinatesMadeUpHint')}
            sx={{ width: 260 }}
          >
            <MenuItem value="made-up">{t('maps.coordinatesMadeUp')}</MenuItem>
            <MenuItem value="real">{t('maps.coordinatesReal')}</MenuItem>
          </TextField>
        )}
        {kind === 'plan' && (
          <TextField label={t('maps.ordinal')} value={ordinal} onChange={(e) => setOrdinal(e.target.value)} size="small" helperText={t('maps.ordinalHint')} sx={{ width: 140 }} />
        )}
        {kind === 'plan' && real && (
          <TextField label={t('maps.elevation')} value={elevation} onChange={(e) => setElevation(e.target.value)} size="small" helperText={t('maps.elevationHint')} sx={{ width: 200 }} />
        )}
        {picking && (
          <TextField
            label={t('maps.goToCoordinates')}
            value={goTo}
            onChange={(e) => setGoTo(e.target.value)}
            size="small"
            placeholder="41.311100, 69.279700"
            error={goTo.trim() !== '' && parseLatLon(goTo) === null}
            helperText={t('maps.frameHint')}
            sx={{ width: 320 }}
          />
        )}
      </Stack>
      {error && <Alert severity="error" sx={{ mx: 2, mb: 1 }} onClose={() => setError(null)}>{error}</Alert>}
      {picking && (
        <SpaceCanvas space={pickerSpace}>
          <ViewTracker onChange={setPickerView} />
          <GoTo text={goTo} />
        </SpaceCanvas>
      )}
    </Box>
  )
}

/** Reports what the map shows each time it stops moving. */
function ViewTracker({ onChange }: { onChange: (view: GeoBoundsDto) => void }) {
  const map = useMapEvents({
    moveend: () => {
      const b = map.getBounds()
      onChange({ southWest: { lat: b.getSouth(), lon: b.getWest() }, northEast: { lat: b.getNorth(), lon: b.getEast() } })
    },
  })
  return null
}

/** Brings typed coordinates into view, close enough to see the street. */
function GoTo({ text }: { text: string }) {
  const map = useMap()
  const point = parseLatLon(text)
  const lat = point?.lat
  const lon = point?.lon
  useEffect(() => {
    if (lat !== undefined && lon !== undefined) map.setView([lat, lon], 17)
  }, [map, lat, lon])
  return null
}
