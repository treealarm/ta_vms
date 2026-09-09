import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Chip,
  CircularProgress,
  Divider,
  IconButton,
  MenuItem,
  Select,
  Stack,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Tooltip,
  Typography,
} from '@mui/material'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import SaveIcon from '@mui/icons-material/Save'
import MyLocationIcon from '@mui/icons-material/MyLocation'
import AddIcon from '@mui/icons-material/Add'
import InfoOutlinedIcon from '@mui/icons-material/InfoOutlined'
import { useTranslation } from 'react-i18next'
import { useAppSelector } from '../store'
import { apiFetch } from '../api/apiFetch'
import CameraStream from './CameraStream'
import MasterPointOverlay from './bbox/MasterPointOverlay'
import PtzControlPad from './PtzControlPad'
import type {
  PtzPresetDto,
  PtzSlaveAimResultDto,
  PtzSlaveBindingDto,
  PtzSlaveBindingMethod,
  PtzSlaveBindingPointDto,
  PtzStatusDto,
} from '../dto/dtos'

type Props = {
  /** The camera being driven. The binding belongs to it: one motor, one master. */
  cameraId: string
  profileToken: string
  /** This camera's own live picture, shown beside its master's while the survey is marked out. */
  slaveWhepUrl?: string
  slaveStunUrl?: string | null
}

/** A point being marked: the spot is chosen, the slave is being driven to it. */
type Pending = { masterX: number; masterY: number }

/** Width of the list column. Wide enough for a preset name, narrow enough to leave the pictures big. */
const LIST_WIDTH = 260

/** How long the camera is held per renewal, and how often to renew — comfortably inside it. */
const HOLD_SECONDS = 40
const HOLD_RENEW_MS = 15000

function errorText(err: unknown, fallback: string): string {
  if (!(err instanceof Error) || !err.message) return fallback
  const text = err.message.trim()
  return text.startsWith('{') || text.startsWith('[') ? fallback : text
}

/**
 * The centre of the slave's frame, marked so a spot can be lined up on it precisely.
 *
 * Which point of the slave's picture the object ends up on is decided entirely by how the operator
 * aimed while marking — the binding stores the position, not a framing rule — so "the middle" only
 * means something if the middle is visible. Eyeballing the centre of a moving picture is exactly
 * the sort of small inaccuracy that a survey then bakes in at every distance.
 *
 * The picture is centred in its box by object-fit: contain, so the box centre is the picture centre
 * whatever the stream's aspect ratio, and this needs no letterbox arithmetic of its own.
 */
function AimCrosshair() {
  const arm = { position: 'absolute' as const, bgcolor: 'rgba(255,255,255,0.85)', boxShadow: '0 0 2px rgba(0,0,0,0.9)' }
  return (
    <Box sx={{ position: 'absolute', inset: 0, pointerEvents: 'none' }}>
      {/* A gap in the middle, so the crosshair frames the target instead of covering it. */}
      <Box sx={{ ...arm, left: '50%', top: 'calc(50% - 28px)', width: '1px', height: '18px', ml: '-0.5px' }} />
      <Box sx={{ ...arm, left: '50%', top: 'calc(50% + 10px)', width: '1px', height: '18px', ml: '-0.5px' }} />
      <Box sx={{ ...arm, top: '50%', left: 'calc(50% - 28px)', height: '1px', width: '18px', mt: '-0.5px' }} />
      <Box sx={{ ...arm, top: '50%', left: 'calc(50% + 10px)', height: '1px', width: '18px', mt: '-0.5px' }} />
      <Box
        sx={{
          position: 'absolute',
          left: '50%',
          top: '50%',
          width: 20,
          height: 20,
          ml: '-10px',
          mt: '-10px',
          borderRadius: '50%',
          border: '1px solid rgba(255,255,255,0.5)',
        }}
      />
    </Box>
  )
}

/** An explanation that stays out of the way until asked for. */
function Hint({ text }: { text: string }) {
  return (
    <Tooltip title={text} placement="top">
      <InfoOutlinedIcon fontSize="inherit" sx={{ color: 'text.disabled', cursor: 'help' }} />
    </Tooltip>
  )
}

export default function CameraSlaveBindingSection({
  cameraId, profileToken, slaveWhepUrl, slaveStunUrl,
}: Props) {
  const { t } = useTranslation()
  const camerasById = useAppSelector((state) => state.live.cameraStatesById)

  const [binding, setBinding] = useState<PtzSlaveBindingDto | null>(null)
  const [master, setMaster] = useState('')
  const [method, setMethod] = useState<PtzSlaveBindingMethod>('absolute')
  const [points, setPoints] = useState<PtzSlaveBindingPointDto[]>([])
  const [selected, setSelected] = useState<number | null>(null)
  const [pending, setPending] = useState<Pending | null>(null)
  const [presets, setPresets] = useState<PtzPresetDto[]>([])
  const [chosenPreset, setChosenPreset] = useState('')
  const [newPresetName, setNewPresetName] = useState('')
  const [loading, setLoading] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [note, setNote] = useState<string | null>(null)

  /**
   * Reads the survey back from the server.
   *
   * @param spinner Only on first load. After an edit the section is already on screen with the
   *   right content, and blanking it to a spinner on every dragged marker would flash the pictures
   *   away several times a minute.
   */
  const fetchBinding = useCallback(async (spinner: boolean) => {
    if (spinner) setLoading(true)
    try {
      const data = await apiFetch<PtzSlaveBindingDto | null>(
        `/api/ptz-slave-binding/${encodeURIComponent(cameraId)}`)
      setBinding(data)
      setMaster(data?.masterCameraId ?? '')
      setMethod(data?.method ?? 'absolute')
      setPoints(data?.points ?? [])
    } catch (e) {
      setError(errorText(e, t('slaveBinding.loadFailed')))
    } finally {
      if (spinner) setLoading(false)
    }
  }, [cameraId, t])

  useEffect(() => {
    setSelected(null)
    void fetchBinding(true)
  }, [fetchBinding])

  // The slave's own presets, for pinning a marked point to one.
  const loadPresets = useCallback(async () => {
    try {
      const rows = await apiFetch<PtzPresetDto[]>(
        `/api/ptz/cameras/${encodeURIComponent(cameraId)}/presets`
        + `?profileToken=${encodeURIComponent(profileToken)}`)
      setPresets(rows)
    } catch {
      // A camera that will not list its presets simply offers none: the coordinates path still
      // works, and saying so here would be noise on a camera nobody intends to use presets with.
      setPresets([])
    }
  }, [cameraId, profileToken])

  useEffect(() => { void loadPresets() }, [loadPresets])

  const masterCamera = camerasById[master]
  const masterStream = useMemo(
    () => (masterCamera?.streams ?? []).find((s) => s.webRtc && s.whepUrl),
    [masterCamera])

  // Anything but this camera: a camera cannot be its own master, and the pairing has no meaning
  // for one with no analytics — but analytics can be switched on later, so it is not filtered out.
  const candidates = useMemo(
    () => Object.values(camerasById)
      .filter((c) => c.cameraId !== cameraId)
      .sort((a, b) => (a.name ?? '').localeCompare(b.name ?? '')),
    [camerasById, cameraId])

  // Whether the survey can drive the camera the way it is set. Answered here as well as by the
  // server so the operator sees it while editing, before saving.
  const unusable = useMemo(() => {
    if (points.length === 0) return t('slaveBinding.noPoints')
    if (method === 'presets') {
      const without = points.filter((p) => !p.presetToken).length
      return without === 0 ? null : t('slaveBinding.needPresets', { count: without, total: points.length })
    }
    const withPosition = points.filter((p) => p.pan != null && p.tilt != null).length
    return withPosition >= 3 ? null : t('slaveBinding.needPositions', { count: withPosition })
  }, [points, method, t])

  /**
   * Keeps the slave's own aiming session off the camera while this page is open.
   *
   * Marking a spot means driving the camera there and recording where it ended up, so a live
   * session pulling it elsewhere mid-way does not merely annoy — it records a position nobody
   * chose. The hold expires by itself and is renewed while the page is open, so closing the laptop
   * releases the camera instead of parking it.
   */
  useEffect(() => {
    const hold = (seconds: number) =>
      apiFetch(`/api/ptz-slave-binding/${encodeURIComponent(cameraId)}/hold`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ seconds }),
      }).catch(() => {
        // Best effort. A camera that cannot be held is a camera that may move under the operator,
        // which is visible in the pictures; failing the whole page over it would be worse.
      })

    void hold(HOLD_SECONDS)
    const timer = window.setInterval(() => void hold(HOLD_SECONDS), HOLD_RENEW_MS)
    return () => {
      window.clearInterval(timer)
      void hold(0)
    }
  }, [cameraId])

  /**
   * Writes the survey as it now stands, and remembers that it is written.
   *
   * Every edit goes through here the moment it is made — marking a spot, dragging one, deleting
   * one, switching method. There is no Save button and deliberately so: a marked spot costs a trip
   * to the camera and a drive by hand, and an explicit save is a step that can be forgotten, at
   * which point the whole survey is gone. Saving as you go cannot lose it. It is also safe to save
   * a half-finished survey — the tracking service refuses to run a session on one it cannot use and
   * says which points are short, so a partial survey aims nothing wrongly, it just does not start.
   */
  const persist = useCallback(async (
    next: { master?: string; method?: PtzSlaveBindingMethod; points?: PtzSlaveBindingPointDto[] },
  ) => {
    const wantedMaster = next.master ?? master
    const wantedMethod = next.method ?? method
    const wantedPoints = next.points ?? points

    if (!wantedMaster) return
    setError(null)
    try {
      await apiFetch(`/api/ptz-slave-binding/${encodeURIComponent(cameraId)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          masterCameraId: wantedMaster,
          method: wantedMethod,
          points: wantedPoints.map(stripResidual),
        }),
      })
      // Re-read rather than trusting the local copy: the residuals are computed server-side, and
      // they are the whole point of the list showing numbers at all. Without the spinner — the
      // selection and the pictures stay put.
      await fetchBinding(false)
    } catch (e) {
      setError(errorText(e, t('slaveBinding.saveFailed')))
    }
  }, [cameraId, master, method, points, fetchBinding, t])

  const removeBinding = async () => {
    setBusy(true)
    setError(null)
    try {
      await apiFetch(`/api/ptz-slave-binding/${encodeURIComponent(cameraId)}`, { method: 'DELETE' })
      setSelected(null)
      await fetchBinding(false)
    } catch (e) {
      setError(errorText(e, t('slaveBinding.saveFailed')))
    } finally {
      setBusy(false)
    }
  }

  /** Records the slave's current aim against the spot being marked. */
  const rememberPosition = async () => {
    if (!pending) return
    setBusy(true)
    setError(null)
    try {
      let presetToken = chosenPreset || null
      let presetName = presets.find((p) => p.token === chosenPreset)?.name ?? null

      // Creating a preset stores wherever the camera is pointing now — which is the spot just
      // driven to, so it needs no coordinates of its own.
      if (!presetToken && newPresetName.trim()) {
        const created = await apiFetch<{ presetToken: string }>(
          `/api/ptz/cameras/${encodeURIComponent(cameraId)}/presets`,
          {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ profileToken, presetName: newPresetName.trim() }),
          })
        presetToken = created.presetToken
        presetName = newPresetName.trim()
        await loadPresets()
      }

      // Read whatever the camera will say. A camera that answers nothing still leaves a usable
      // point as long as it has a preset — which is the whole reason both are stored.
      let pan: number | null = null
      let tilt: number | null = null
      const status = await apiFetch<PtzStatusDto>(
        `/api/ptz/status?cameraId=${encodeURIComponent(cameraId)}`
        + `&profileToken=${encodeURIComponent(profileToken)}`)
      if (status.available) {
        pan = status.pan
        tilt = status.tilt
      }

      if (pan === null && !presetToken) {
        setError(t('slaveBinding.nothingToRecord'))
        return
      }

      const marked = [...points, {
        masterX: pending.masterX,
        masterY: pending.masterY,
        pan,
        tilt,
        presetToken,
        presetName,
      }]
      setPoints(marked)
      setSelected(points.length)
      setPending(null)
      setChosenPreset('')
      setNewPresetName('')
      setNote(null)
      await persist({ points: marked })
    } catch (e) {
      setError(errorText(e, t('slaveBinding.recordFailed')))
    } finally {
      setBusy(false)
    }
  }

  /** Sends the slave where a spot maps to — how a survey is checked. */
  const aimAt = async (x: number, y: number) => {
    setBusy(true)
    setError(null)
    setNote(null)
    try {
      const result = await apiFetch<PtzSlaveAimResultDto>(
        `/api/ptz-slave-binding/${encodeURIComponent(cameraId)}/aim`,
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ masterX: x, masterY: y }),
        })
      const where = result.presetName ?? result.presetToken
        ?? `${result.pan?.toFixed(3)}, ${result.tilt?.toFixed(3)}`
      setNote(result.extrapolated
        ? t('slaveBinding.aimedOutside', { where })
        : t('slaveBinding.aimed', { where }))
    } catch (e) {
      setError(errorText(e, t('slaveBinding.aimFailed')))
    } finally {
      setBusy(false)
    }
  }

  /** Drives the camera to a preset, which is the only way to find out where it points. */
  const gotoPreset = async (token: string) => {
    setBusy(true)
    setError(null)
    try {
      await apiFetch(
        `/api/ptz/cameras/${encodeURIComponent(cameraId)}/presets/${encodeURIComponent(token)}/goto`,
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ profileToken }),
        })
    } catch (e) {
      setError(errorText(e, t('slaveBinding.gotoFailed')))
    } finally {
      setBusy(false)
    }
  }

  /**
   * Re-records a preset where the camera is now, keeping its name.
   *
   * ONVIF stores a preset at wherever the head currently points, so "edit" is: drive there, then
   * press this. Passing the existing token asks the camera to overwrite that entry rather than add
   * one — which not every firmware honours, so the token it answers with is checked rather than
   * assumed.
   *
   * Marked spots pinned to this preset have their coordinates brought along. A point holds two
   * records of one position, the preset and the pan/tilt, and moving the preset without the other
   * would leave the two disagreeing — the presets method would go to the new place and the
   * coordinates method to the old one, from the same marked spot.
   */
  const updatePreset = async (token: string, name: string) => {
    setBusy(true)
    setError(null)
    setNote(null)
    try {
      const saved = await apiFetch<{ presetToken: string }>(
        `/api/ptz/cameras/${encodeURIComponent(cameraId)}/presets`,
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ profileToken, presetName: name, presetToken: token }),
        })

      const landed = saved.presetToken || token
      await loadPresets()

      if (points.some((p) => p.presetToken === token)) {
        const status = await apiFetch<PtzStatusDto>(
          `/api/ptz/status?cameraId=${encodeURIComponent(cameraId)}`
          + `&profileToken=${encodeURIComponent(profileToken)}`)

        const next = points.map((p) => (p.presetToken === token
          ? {
              ...p,
              presetToken: landed,
              pan: status.available ? status.pan : p.pan,
              tilt: status.available ? status.tilt : p.tilt,
            }
          : p))
        setPoints(next)
        await persist({ points: next })
      }

      setNote(landed === token
        ? t('slaveBinding.presetUpdated', { name })
        // The camera added an entry instead of replacing one. The survey is re-pinned to the new
        // token above so it keeps working, but the stale entry is left alone: deleting a preset
        // this operator did not ask to delete is not a decision to take on their behalf.
        : t('slaveBinding.presetCopied', { name, token }))
    } catch (e) {
      setError(errorText(e, t('slaveBinding.updatePresetFailed')))
    } finally {
      setBusy(false)
    }
  }

  /** Deletes a preset from the camera, and unpins any marked spot that was using it. */
  const deletePreset = async (token: string) => {
    setBusy(true)
    setError(null)
    try {
      await apiFetch(
        `/api/ptz/cameras/${encodeURIComponent(cameraId)}/presets/${encodeURIComponent(token)}`
        + `?profileToken=${encodeURIComponent(profileToken)}`,
        { method: 'DELETE' })
      await loadPresets()
      // A marked spot pointing at a preset that no longer exists would send the slave nowhere, and
      // the survey would look complete while being broken. Clearing it here surfaces the gap
      // immediately as "this spot has no preset".
      const unpinned = points.map((p) =>
        p.presetToken === token ? { ...p, presetToken: null, presetName: null } : p)
      setPoints(unpinned)
      await persist({ points: unpinned })
    } catch (e) {
      setError(errorText(e, t('slaveBinding.deletePresetFailed')))
    } finally {
      setBusy(false)
    }
  }

  const movePoint = useCallback((index: number, x: number, y: number) => {
    setPoints((current) => current.map((p, i) => (i === index ? { ...p, masterX: x, masterY: y } : p)))
  }, [])

  if (loading)
    return <Box sx={{ py: 2 }}><CircularProgress size={20} /></Box>

  const overlayPoints = pending
    ? [...points, { masterX: pending.masterX, masterY: pending.masterY, presetToken: null }]
    : points

  return (
    <Stack spacing={2}>
      {error && <Alert severity="error" onClose={() => setError(null)}>{error}</Alert>}
      {note && <Alert severity="info" onClose={() => setNote(null)}>{note}</Alert>}

      {/* One row of settings rather than a column of labelled paragraphs: what each control means
          is one hover away, and the pictures below are what this tab is actually for. */}
      <Stack direction="row" spacing={2} alignItems="center" flexWrap="wrap" useFlexGap>
        <Stack direction="row" spacing={0.5} alignItems="center">
          <Typography variant="body2">{t('slaveBinding.master')}</Typography>
          <Hint text={t('slaveBinding.intro')} />
        </Stack>
        <Select
          size="small"
          displayEmpty
          value={master}
          onChange={(e) => {
            const next = e.target.value
            setMaster(next)
            // Clearing the master is not an edit to the survey, it is the end of the pairing: the
            // marked spots are coordinates in that camera's frame and mean nothing without it.
            if (next) void persist({ master: next })
            else void removeBinding()
          }}
          sx={{ minWidth: 200 }}
        >
          <MenuItem value="">{t('slaveBinding.noMaster')}</MenuItem>
          {candidates.map((c) => (
            <MenuItem key={c.cameraId} value={c.cameraId}>{c.name || c.cameraId}</MenuItem>
          ))}
        </Select>

        <Stack direction="row" spacing={0.5} alignItems="center">
          <Typography variant="body2">{t('slaveBinding.method')}</Typography>
          <Hint text={method === 'absolute'
            ? t('slaveBinding.methodAbsoluteHint')
            : t('slaveBinding.methodPresetsHint')} />
        </Stack>
        <ToggleButtonGroup
          size="small"
          exclusive
          value={method}
          onChange={(_, next: PtzSlaveBindingMethod | null) => {
            if (!next) return
            setMethod(next)
            void persist({ method: next })
          }}
        >
          <ToggleButton value="absolute">{t('slaveBinding.methodAbsolute')}</ToggleButton>
          <ToggleButton value="presets">{t('slaveBinding.methodPresets')}</ToggleButton>
        </ToggleButtonGroup>
      </Stack>

      {/* Explained rather than hidden: an operator who has just marked six points needs to know
          which ones are short of a preset, not to find the switch greyed out. */}
      {unusable && <Alert severity="warning" sx={{ py: 0 }}>{unusable}</Alert>}

      {master && !masterStream && (
        <Alert severity="warning">{t('slaveBinding.masterHasNoStream')}</Alert>
      )}

      {master && masterStream?.whepUrl && (
        <Stack direction={{ xs: 'column', lg: 'row' }} spacing={2} alignItems="stretch">
          {/* The list first, beside the pictures rather than under them. Below the fold it was
              invisible at exactly the moment it matters — while marking, when the operator needs to
              see what is already covered. */}
          <Stack spacing={1} sx={{ width: { lg: LIST_WIDTH }, flexShrink: 0 }}>
            {/* At the top of the column, not the bottom of the page. Down there it was below the
                fold: the operator clicked the picture, the form appeared out of sight, and the next
                click did nothing because a spot was already pending — which reads as the page
                having frozen rather than as a step waiting to be finished. */}
            {pending && (
              <Stack
                spacing={1}
                sx={{ p: 1, border: 1, borderColor: 'warning.main', borderRadius: 1 }}
              >
                <Typography variant="caption" color="text.secondary">
                  {t('slaveBinding.recordAt', {
                    x: pending.masterX.toFixed(3),
                    y: pending.masterY.toFixed(3),
                  })}
                </Typography>
                <Select
                  size="small"
                  displayEmpty
                  value={chosenPreset}
                  onChange={(e) => setChosenPreset(e.target.value)}
                >
                  <MenuItem value="">{t('slaveBinding.noPreset')}</MenuItem>
                  {presets.map((p) => (
                    <MenuItem key={p.token} value={p.token}>{p.name || p.token}</MenuItem>
                  ))}
                </Select>
                {!chosenPreset && (
                  <TextField
                    size="small"
                    placeholder={t('slaveBinding.newPresetName')}
                    value={newPresetName}
                    onChange={(e) => setNewPresetName(e.target.value)}
                    InputProps={{ startAdornment: <AddIcon fontSize="small" sx={{ mr: 0.5 }} /> }}
                  />
                )}
                <Stack direction="row" spacing={1}>
                  <Button
                    variant="contained"
                    size="small"
                    disabled={busy}
                    onClick={() => void rememberPosition()}
                  >
                    {t('slaveBinding.remember')}
                  </Button>
                  <Button size="small" disabled={busy} onClick={() => setPending(null)}>
                    {t('common.cancel')}
                  </Button>
                </Stack>
              </Stack>
            )}

            <Stack direction="row" spacing={0.5} alignItems="center">
              <Typography variant="subtitle2">{t('slaveBinding.points')}</Typography>
              <Hint text={t('slaveBinding.residualHint')} />
            </Stack>

            {points.length === 0 ? (
              <Typography variant="caption" color="text.secondary">
                {t('slaveBinding.pickHint')}
              </Typography>
            ) : (
              <Stack spacing={0.5} sx={{ maxHeight: 320, overflowY: 'auto', pr: 0.5 }}>
                {points.map((point, index) => (
                  <Box
                    key={index}
                    onClick={() => setSelected(index)}
                    sx={{
                      p: 0.75,
                      borderRadius: 1,
                      cursor: 'pointer',
                      border: 1,
                      borderColor: selected === index ? 'primary.main' : 'divider',
                      bgcolor: selected === index ? 'action.selected' : 'transparent',
                    }}
                  >
                    <Stack direction="row" spacing={0.5} alignItems="center">
                      <Chip size="small" label={index + 1} />
                      <Typography variant="caption" sx={{ flex: 1, minWidth: 0 }} noWrap>
                        {describePoint(point) ?? t('slaveBinding.emptyPoint')}
                      </Typography>
                      {point.residual != null && (
                        <Tooltip title={t('slaveBinding.residualTooltip')}>
                          <Chip
                            size="small"
                            variant="outlined"
                            color={point.residual > 0.1 ? 'warning' : 'default'}
                            label={point.residual.toFixed(2)}
                          />
                        </Tooltip>
                      )}
                    </Stack>
                    <Stack direction="row" spacing={0.5} alignItems="center" sx={{ mt: 0.25 }}>
                      <Typography variant="caption" color="text.secondary" sx={{ flex: 1 }}>
                        {point.masterX.toFixed(2)}, {point.masterY.toFixed(2)}
                      </Typography>
                      <Tooltip title={t('slaveBinding.goHere')}>
                        <span>
                          <IconButton
                            size="small"
                            disabled={busy}
                            onClick={(e) => { e.stopPropagation(); void aimAt(point.masterX, point.masterY) }}
                          >
                            <MyLocationIcon fontSize="inherit" />
                          </IconButton>
                        </span>
                      </Tooltip>
                      <Tooltip title={t('slaveBinding.deletePoint')}>
                        <IconButton
                          size="small"
                          onClick={(e) => {
                            e.stopPropagation()
                            const remaining = points.filter((_, i) => i !== index)
                            setPoints(remaining)
                            setSelected(null)
                            void persist({ points: remaining })
                          }}
                        >
                          <DeleteOutlineIcon fontSize="inherit" />
                        </IconButton>
                      </Tooltip>
                    </Stack>
                  </Box>
                ))}
              </Stack>
            )}

            <Divider />

            {/* The camera's presets, listed so they can be tried and tidied without leaving the
                survey. Where a preset points is not knowable from its name, so the only honest way
                to find out is to send the camera there and look. */}
            <Stack direction="row" spacing={0.5} alignItems="center">
              <Typography variant="subtitle2">{t('slaveBinding.cameraPresets')}</Typography>
              <Hint text={t('slaveBinding.cameraPresetsHint')} />
            </Stack>
            {presets.length === 0 ? (
              <Typography variant="caption" color="text.secondary">
                {t('slaveBinding.noCameraPresets')}
              </Typography>
            ) : (
              <Stack spacing={0.25} sx={{ maxHeight: 160, overflowY: 'auto', pr: 0.5 }}>
                {presets.map((preset) => (
                  <Stack key={preset.token} direction="row" spacing={0.5} alignItems="center">
                    <Tooltip title={t('slaveBinding.gotoPreset')}>
                      <Button
                        size="small"
                        disabled={busy}
                        onClick={() => void gotoPreset(preset.token)}
                        sx={{ flex: 1, justifyContent: 'flex-start', textTransform: 'none', minWidth: 0 }}
                      >
                        <Typography variant="caption" noWrap>{preset.name || preset.token}</Typography>
                      </Button>
                    </Tooltip>
                    <Tooltip title={t('slaveBinding.updatePreset')}>
                      <span>
                        <IconButton
                          size="small"
                          disabled={busy}
                          onClick={() => void updatePreset(preset.token, preset.name || preset.token)}
                        >
                          <SaveIcon fontSize="inherit" />
                        </IconButton>
                      </span>
                    </Tooltip>
                    <Tooltip title={t('slaveBinding.deletePreset')}>
                      <span>
                        <IconButton
                          size="small"
                          disabled={busy}
                          onClick={() => void deletePreset(preset.token)}
                        >
                          <DeleteOutlineIcon fontSize="inherit" />
                        </IconButton>
                      </span>
                    </Tooltip>
                  </Stack>
                ))}
              </Stack>
            )}
          </Stack>

          {/* Both pictures at the same size and on the same line, because marking a spot is a
              comparison between them: the operator looks at a place on the master's ground and has
              to see what the slave is showing while they drive it there. */}
          <Stack spacing={0.5} sx={{ flex: 1, minWidth: 0 }}>
            <Stack direction="row" spacing={0.5} alignItems="center">
              <Typography variant="caption" color="text.secondary" noWrap>
                {t('slaveBinding.masterPicture', { name: masterCamera?.name || master })}
              </Typography>
              <Hint text={t('slaveBinding.pickHint')} />
              {method === 'presets' && points.length > 0 && <Hint text={t('slaveBinding.zonesHint')} />}
            </Stack>
            <Box sx={{ position: 'relative', width: '100%', aspectRatio: '16 / 9', bgcolor: 'black' }}>
              <CameraStream
                whepUrl={masterStream.whepUrl}
                stunUrl={masterCamera?.effectiveWebrtcStunUrl}
                overlay={(videoRef) => (
                  <MasterPointOverlay
                    videoRef={videoRef}
                    points={overlayPoints}
                    activeIndex={pending ? overlayPoints.length - 1 : null}
                    selectedIndex={selected}
                    showZones={method === 'presets'}
                    onSelectPoint={setSelected}
                    onMovePoint={movePoint}
                    onCommitPoint={() => void persist({})}
                    onPick={(x, y) => {
                      // Clicking again while a spot is being recorded moves it. Ignoring the click
                      // was the first behaviour and read as the page having frozen — the operator
                      // clicks, nothing happens, and the reason is a form they cannot see.
                      setPending({ masterX: x, masterY: y })
                      setNote(null)
                    }}
                  />
                )}
              />
            </Box>
          </Stack>

          <Stack spacing={0.5} sx={{ flex: 1, minWidth: 0 }}>
            <Stack direction="row" spacing={0.5} alignItems="center">
              <Typography variant="caption" color="text.secondary" noWrap>
                {t('slaveBinding.slavePicture')}
              </Typography>
              <Hint text={t('slaveBinding.driveHint')} />
            </Stack>
            {/* The pad sits on the picture it moves, which is both where the room is and where
                the operator is already looking — they are driving by eye. */}
            <Box sx={{
              position: 'relative', width: '100%', aspectRatio: '16 / 9',
              bgcolor: slaveWhepUrl ? 'black' : 'action.hover',
            }}>
              {slaveWhepUrl ? (
                <CameraStream whepUrl={slaveWhepUrl} stunUrl={slaveStunUrl} />
              ) : (
                <Box sx={{ height: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center', p: 2 }}>
                  <Typography variant="caption" color="text.secondary" align="center">
                    {t('slaveBinding.slaveHasNoStream')}
                  </Typography>
                </Box>
              )}
              {slaveWhepUrl && <AimCrosshair />}
              <Box
                sx={{
                  position: 'absolute',
                  // Top, not bottom: the video keeps its native controls, whose bar appears along
                  // the bottom edge on hover.
                  top: 8,
                  right: 8,
                  p: 0.5,
                  borderRadius: 1,
                  bgcolor: 'rgba(0,0,0,0.35)',
                  backdropFilter: 'blur(2px)',
                }}
              >
                <PtzControlPad cameraId={cameraId} profileToken={profileToken} />
              </Box>
            </Box>
          </Stack>
        </Stack>
      )}

      {binding && (
        <Stack direction="row" spacing={1} alignItems="center">
          <Button size="small" color="error" disabled={busy} onClick={() => void removeBinding()}>
            {t('slaveBinding.clear')}
          </Button>
          <Typography variant="caption" color="text.secondary">
            {t('slaveBinding.savedAsYouGo')}
          </Typography>
        </Stack>
      )}
    </Stack>
  )
}

/** The residual is computed by the server; sending it back would be sending it our own answer. */
function stripResidual(point: PtzSlaveBindingPointDto): PtzSlaveBindingPointDto {
  return {
    masterX: point.masterX,
    masterY: point.masterY,
    pan: point.pan,
    tilt: point.tilt,
    presetToken: point.presetToken,
    presetName: point.presetName,
  }
}

/** What a marked spot was recorded with — coordinates, a preset, or both. Null when neither. */
function describePoint(point: PtzSlaveBindingPointDto): string | null {
  const parts: string[] = []
  if (point.presetToken)
    parts.push(point.presetName || point.presetToken)
  if (point.pan != null && point.tilt != null)
    parts.push(`${point.pan.toFixed(2)}, ${point.tilt.toFixed(2)}`)
  return parts.length > 0 ? parts.join(' · ') : null
}
