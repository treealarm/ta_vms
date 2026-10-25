import { useEffect, useState } from 'react'
import { Box, FormControlLabel, MenuItem, Stack, Switch, Tab, Tabs, TextField, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { addCamera, changeCameraParent, fetchCameraState, removeCamera } from '../store/liveSlice'
import { setCameraStorages } from '../store/storageSlice'
import { setCameraSchedules } from '../store/scheduleSlice'
import type { CameraSetupDto, CameraStorageAssignmentDto } from '../dto/dtos'
import CameraStoragesControl from './CameraStoragesControl'
import CameraSchedulesControl from './CameraSchedulesControl'
import CameraDetectionsSection from './CameraDetectionsSection'
import CameraCalibrationSection from './CameraCalibrationSection'
import CameraTrackingSection from './CameraTrackingSection'
import CameraSlaveBindingSection from './CameraSlaveBindingSection'
import PasswordField from './PasswordField'
import CameraStream from './CameraStream'
import DetailToolbar from './settings/DetailToolbar'
import FieldHint from './settings/FieldHint'
import { useDirtyGuard } from './settings/useDirtyGuard'
import type { ApplyIfDirtyHandle } from './settings/useDirtyGuard'

type Props = {
  ref?: React.Ref<ApplyIfDirtyHandle>
  cameraId: string  // '' = new camera
  serverId: string  // pre-selected server
  onSaved: (createdCameraId?: string) => Promise<void>
  onRemoved: () => Promise<void>
}

export default function CameraSetupPanel({ ref, cameraId: initialCameraId, serverId: initialServerId, onSaved, onRemoved }: Props) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const servers = useAppSelector((state) => state.live.servers)
  const mtxServers = useAppSelector((state) => state.mediaMtx.servers)
  const allStorages = useAppSelector((state) => state.storage.storages)
  const camerasMap = useAppSelector((state) => state.live.cameraStatesById)
  const existing = camerasMap[initialCameraId]
  const isNew = initialCameraId === ''

  const [cameraId] = useState(() => isNew ? crypto.randomUUID() : initialCameraId)
  const [name, setName] = useState(existing?.name ?? '')
  const [serverId, setServerId] = useState(initialServerId)
  // A new camera is left to VmsCfg, which picks its node's own MediaMTX when there is one.
  const [mediaMtxServerId, setMediaMtxServerId] = useState(isNew ? '' : (existing?.mediaMtxServerId ?? ''))
  const [user, setUser] = useState(existing?.cred?.user ?? '')
  const [pass, setPass] = useState(existing?.cred?.pass ?? '')
  const [ip, setIp] = useState(existing?.onvifConn?.ip ?? '')
  const [port, setPort] = useState(isNew ? 80 : (existing?.onvifConn?.port ?? 0))
  const [disabled, setDisabled] = useState(existing?.disabled === true)
  const [edgeBackfillStream, setEdgeBackfillStream] = useState(existing?.edgeBackfillStream ?? '')
  const [roiTranscode, setRoiTranscode] = useState(existing?.roiTranscode === true)
  const [onvifMetadataDisabled, setOnvifMetadataDisabled] = useState(existing?.onvifMetadataDisabled === true)
  const [hasTelemetry, setHasTelemetry] = useState(existing?.hasTelemetry === true)
  // Percent in the field, fraction on the wire: the API speaks 0..1, operators do not.
  const pctOf = (v: number | undefined, fallback: number) =>
    String(Math.round((v ?? fallback) * 1000) / 10)
  const [roiDelayHours, setRoiDelayHours] = useState(String(existing?.roiDelayHours ?? 0))
  const [roiMinCoverage, setRoiMinCoverage] = useState(pctOf(existing?.roiMinCoverage, 0))
  const [roiMaxCoverage, setRoiMaxCoverage] = useState(pctOf(existing?.roiMaxCoverage, 1))
  type Tab = 'main' | 'advanced' | 'detections' | 'tracking' | 'calibration' | 'binding'
  const [tab, setTab] = useState<Tab>('main')
  // The settings column always fills whatever the camera list and the preview leave; what varies
  // is how each tab uses that space. The survey takes all of it — it lays two live pictures side
  // by side. The main and advanced forms flow into as many columns as fit. Tracking and calibration
  // are a priority list and a button wizard, where a row stretched across the whole panel puts its
  // controls a mouse-journey away from its label, so they stop at a comfortable reading width.
  const readingWidth = 640

  useEffect(() => {
    if (!hasTelemetry && (tab === 'tracking' || tab === 'calibration' || tab === 'binding'))
      setTab('main')
  }, [hasTelemetry, tab])
  const [pendingStorages, setPendingStorages] = useState<CameraStorageAssignmentDto[]>([])
  const [pendingSchedules, setPendingSchedules] = useState<string[]>([])

  const setNonNegativeNumber = (raw: string, setter: (n: number) => void) => {
    const value = Number(raw)
    if (!Number.isNaN(value)) setter(Math.max(0, value))
  }

  // Pre-populate pending storage with the first available storage
  useEffect(() => {
    if (isNew && pendingStorages.length === 0 && allStorages.length > 0)
      setPendingStorages([{ cameraId: '', storageId: allStorages[0].storageId, priority: 0 }])
  }, [allStorages]) // eslint-disable-line react-hooks/exhaustive-deps

  // Snapshot of the saved state — used for dirty detection and Cancel
  const [snap, setSnap] = useState({
    name: existing?.name ?? '',
    serverId: initialServerId,
    mediaMtxServerId: existing?.mediaMtxServerId ?? '',
    user: existing?.cred?.user ?? '',
    pass: existing?.cred?.pass ?? '',
    ip: existing?.onvifConn?.ip ?? '',
    port: existing?.onvifConn?.port ?? 0,
    disabled: existing?.disabled === true,
    edgeBackfillStream: existing?.edgeBackfillStream ?? '',
    roiTranscode: existing?.roiTranscode === true,
    onvifMetadataDisabled: existing?.onvifMetadataDisabled === true,
    hasTelemetry: existing?.hasTelemetry === true,
    roiDelayHours: String(existing?.roiDelayHours ?? 0),
    roiMinCoverage: pctOf(existing?.roiMinCoverage, 0),
    roiMaxCoverage: pctOf(existing?.roiMaxCoverage, 1),
  })

  const dirty = !isNew && (
    name !== snap.name
    || serverId !== snap.serverId
    || mediaMtxServerId !== snap.mediaMtxServerId
    || user !== snap.user
    || pass !== snap.pass
    || ip !== snap.ip
    || port !== snap.port
    || disabled !== snap.disabled
    || edgeBackfillStream !== snap.edgeBackfillStream
    || roiTranscode !== snap.roiTranscode
    || onvifMetadataDisabled !== snap.onvifMetadataDisabled
    || hasTelemetry !== snap.hasTelemetry
    || roiDelayHours !== snap.roiDelayHours
    || roiMinCoverage !== snap.roiMinCoverage
    || roiMaxCoverage !== snap.roiMaxCoverage
  )

  // Which of this camera's streams the camera itself records onto its storage. Empty = the feature
  // is off. Camera-level and single-valued because the card holds one archive, and which stream
  // goes into it is chosen in the camera, not here.
  const cameraStreams = existing?.streams ?? []

  // WebRTC streams available for preview
  const webRtcStreams = (existing?.streams ?? []).filter((s) => s.webRtc)

  const buildPayload = (): CameraSetupDto => ({
    cameraId: isNew ? cameraId : initialCameraId,
    name,
    serverId,
    recordingMode: existing?.recordingMode ?? 'off',
    webRtc: existing?.webRtc ?? false,
    path: existing?.path ?? '',
    streams: existing?.streams ?? [],
    cred: user || pass ? { user, pass } : undefined,
    onvifConn: ip ? { ip, port } : undefined,
    mediaMtxServerId: mediaMtxServerId || undefined,
    disabled: disabled || undefined,
    edgeBackfillStream,
    roiTranscode,
    onvifMetadataDisabled,
    hasTelemetry,
    // Kept as text while editing so a half-typed "0." is not thrown away.
    roiDelayHours: Number(roiDelayHours) || 0,
    roiMinCoverage: (Number(roiMinCoverage) || 0) / 100,
    roiMaxCoverage: (Number(roiMaxCoverage) || 0) / 100,
  })

  const applyInternal = async (): Promise<boolean> => {
    if (isNew) {
      if (!name.trim()) return true  // nothing worth saving — allow discard
      const payload = buildPayload()
      const result = await dispatch(addCamera(payload))
      if (addCamera.rejected.match(result)) return false
      if (pendingStorages.length > 0) {
        const withId = pendingStorages.map((a, i) => ({ ...a, cameraId: payload.cameraId, priority: i }))
        await dispatch(setCameraStorages({ cameraId: payload.cameraId, assignments: withId }))
      }
      if (pendingSchedules.length > 0)
        await dispatch(setCameraSchedules({ cameraId: payload.cameraId, scheduleIds: pendingSchedules }))
      await onSaved(payload.cameraId)
      return true
    }
    if (!dirty) return true
    const payload = buildPayload()
    const serverChanged = existing && serverId !== existing.serverId
    const action = serverChanged ? changeCameraParent : addCamera
    const result = await dispatch(action(payload))
    if (!action.rejected.match(result)) {
      setSnap({ name, serverId, mediaMtxServerId, user, pass, ip, port, disabled, edgeBackfillStream, roiTranscode, onvifMetadataDisabled, hasTelemetry, roiDelayHours, roiMinCoverage, roiMaxCoverage })
      await dispatch(fetchCameraState(initialCameraId))
      return true
    }
    return false
  }

  const handleApply = async (e: React.FormEvent) => {
    e.preventDefault()
    const ok = await applyInternal()
    if (ok && !isNew) await onSaved()
  }

  const handleCancel = () => {
    setName(snap.name)
    setServerId(snap.serverId)
    setMediaMtxServerId(snap.mediaMtxServerId)
    setUser(snap.user)
    setPass(snap.pass)
    setIp(snap.ip)
    setPort(snap.port)
    setDisabled(snap.disabled)
    // These two were missing, so Cancel used to leave the form dirty.
    setEdgeBackfillStream(snap.edgeBackfillStream)
    setRoiTranscode(snap.roiTranscode)
    setOnvifMetadataDisabled(snap.onvifMetadataDisabled)
    setHasTelemetry(snap.hasTelemetry)
    setRoiDelayHours(snap.roiDelayHours)
    setRoiMinCoverage(snap.roiMinCoverage)
    setRoiMaxCoverage(snap.roiMaxCoverage)
  }

  const handleRemove = async () => {
    const result = await dispatch(removeCamera({ cameraId: initialCameraId }))
    if (!removeCamera.rejected.match(result)) {
      await onRemoved()
    }
  }

  // applyIfDirty for the parent (selection/tab switch) + beforeunload warning.
  // For a new form "dirty" means "complete enough to save" — navigation then
  // auto-creates it instead of silently discarding the entered data.
  useDirtyGuard(ref, isNew ? Boolean(name.trim()) : dirty, applyInternal)

  return (
    <Box component="form" onSubmit={(e) => void handleApply(e)} sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      <DetailToolbar
        title={isNew ? t('cameras.newCamera') : (existing?.name || initialCameraId)}
        subtitle={isNew ? undefined : initialCameraId}
        dirty={isNew ? true : dirty}
        applyLabel={isNew ? t('cameras.addCamera') : undefined}
        onCancel={isNew ? undefined : handleCancel}
        onDelete={isNew ? undefined : () => void handleRemove()}
        deleteLabel={t('cameras.deleteCamera')}
      />

      <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', p: 2 }}>
        <Stack direction="row" spacing={3} alignItems="flex-start">
          {/* Left: the settings themselves, filling whatever the list and the preview leave */}
          <Stack spacing={2} sx={{ flex: 1, minWidth: 0 }}>
            <Tabs
              value={tab}
              onChange={(_, v: Tab) => setTab(v)}
              variant="scrollable"
              scrollButtons="auto"
              sx={{ borderBottom: 1, borderColor: 'divider' }}
            >
              <Tab value="main" label={t('cameras.tabMain')} />
              {!isNew && <Tab value="detections" label={t('cameras.tabDetections')} />}
              {/* Three tabs rather than three folds. They were accordions stacked in one tab, which
                  made a single sheet taller than the panel: opening the survey pushed it off the
                  bottom, and its two live pictures had nowhere to be. They are also three separate
                  jobs — what to follow, which way the motor turns, and where a master's ground maps
                  to — done at different times and rarely together. */}
              {!isNew && hasTelemetry && <Tab value="tracking" label={t('cameras.tabTracking')} />}
              {!isNew && hasTelemetry && <Tab value="calibration" label={t('cameras.calibration')} />}
              {!isNew && hasTelemetry && <Tab value="binding" label={t('cameras.tabMaster')} />}
              <Tab value="advanced" label={t('cameras.tabAdvanced')} />
            </Tabs>

            {tab === 'main' && (
            <Box
              sx={{
                display: 'grid',
                // Fields flow into as many columns as the panel is wide enough for, and fall back
                // to one when it is narrow. A single stacked column left most of the panel empty
                // and pushed the rest of the form below the fold — the same complaint the tracking
                // tab had, for the same reason.
                gridTemplateColumns: 'repeat(auto-fill, minmax(300px, 1fr))',
                gap: 2,
                alignItems: 'start',
              }}
            >
            <TextField
              label={t('cameras.cameraName')}
              value={name}
              onChange={(e) => setName(e.target.value)}
              required
              size="small"
            />

            <TextField select label={t('cameras.server')} value={serverId} onChange={(e) => setServerId(e.target.value)} size="small">
              {servers.map((s) => (
                <MenuItem key={s.serverId} value={s.serverId}>{s.serverId}</MenuItem>
              ))}
            </TextField>

            <FieldHint hint={t('cameras.mediaMtxHelper')}>
              <TextField
                select
                fullWidth
                label={t('cameras.mediaMtxServer')}
                value={mediaMtxServerId}
                onChange={(e) => setMediaMtxServerId(e.target.value)}
                size="small"
              >
                <MenuItem value="">{t('cameras.autoAssign')}</MenuItem>
                {/* Its node's own and the shared ones: another node's would carry the video across
                    the link between them. The one already assigned stays listed, so that an
                    assignment made before is still shown. */}
                {mtxServers
                  .filter((s) => !s.serverId || s.serverId === serverId || s.id === mediaMtxServerId)
                  .map((s) => (
                    <MenuItem key={s.id} value={s.id}>{s.name}</MenuItem>
                  ))}
              </TextField>
            </FieldHint>

            <Stack direction="row" spacing={1}>
              <TextField label={t('cameras.ip')} value={ip} onChange={(e) => setIp(e.target.value)} size="small" sx={{ flex: 1 }} />
              <TextField
                label={t('cameras.port')}
                value={port}
                onChange={(e) => setNonNegativeNumber(e.target.value, setPort)}
                size="small"
                sx={{ width: 100 }}
              />
            </Stack>

            <Stack direction="row" spacing={1} alignItems="center">
              <TextField label={t('common.user')} value={user} onChange={(e) => setUser(e.target.value)} size="small" fullWidth />
              <PasswordField pass={pass} setPass={setPass} />
            </Stack>

            <FormControlLabel
              control={<Switch checked={disabled} onChange={(e) => setDisabled(e.target.checked)} color="warning" />}
              label={t('cameras.disabled')}
            />

            <FieldHint hint={t('cameras.hasTelemetryHelper')}>
              <FormControlLabel
                control={<Switch checked={hasTelemetry} onChange={(e) => setHasTelemetry(e.target.checked)} />}
                label={t('cameras.hasTelemetry')}
              />
            </FieldHint>
            {isNew
              ? <CameraStoragesControl value={pendingStorages} onChange={setPendingStorages} />
              : <CameraStoragesControl cameraId={initialCameraId} />
            }

            {isNew
              ? <CameraSchedulesControl value={pendingSchedules} onChange={setPendingSchedules} />
              : <CameraSchedulesControl cameraId={initialCameraId} />
            }
            </Box>
            )}

            {tab === 'detections' && !isNew && (
            <CameraDetectionsSection
              cameraId={initialCameraId}
              onvifMetadataDisabled={onvifMetadataDisabled}
              setOnvifMetadataDisabled={setOnvifMetadataDisabled}
            />
            )}

            {tab === 'tracking' && !isNew && hasTelemetry && (
              <Box sx={{ maxWidth: readingWidth }}>
                <CameraTrackingSection cameraId={initialCameraId} />
              </Box>
            )}

            {tab === 'calibration' && !isNew && hasTelemetry && (
              <Box sx={{ maxWidth: readingWidth }}>
                <CameraCalibrationSection
                  cameraId={initialCameraId}
                  hasTelemetry={hasTelemetry}
                />
              </Box>
            )}

            {/* Uncapped: this one lays two live pictures out side by side and wants the room. */}
            {tab === 'binding' && !isNew && hasTelemetry && (
              <CameraSlaveBindingSection
                cameraId={initialCameraId}
                profileToken={existing?.streams?.[0]?.profileToken ?? ''}
                slaveWhepUrl={webRtcStreams.find((st) => st.whepUrl)?.whepUrl}
                slaveStunUrl={existing?.effectiveWebrtcStunUrl}
              />
            )}

            {tab === 'advanced' && (
            <Box
              sx={{
                display: 'grid',
                // Fields flow into as many columns as the panel is wide enough for, and fall back
                // to one when it is narrow. A single stacked column left most of the panel empty
                // and pushed the rest of the form below the fold — the same complaint the tracking
                // tab had, for the same reason.
                gridTemplateColumns: 'repeat(auto-fill, minmax(320px, 1fr))',
                gap: 2,
                alignItems: 'start',
              }}
            >
              <FieldHint hint={t('cameras.roiTranscodeHint')}>
                <FormControlLabel
                  control={<Switch checked={roiTranscode} onChange={(e) => setRoiTranscode(e.target.checked)} />}
                  label={t('cameras.roiTranscode')}
                />
              </FieldHint>
              {roiTranscode && (
                <FieldHint hint={t('cameras.roiDelayHoursHint')}>
                  <TextField
                    label={t('cameras.roiDelayHours')}
                    type="number"
                    size="small"
                    fullWidth
                    value={roiDelayHours}
                    onChange={(e) => setRoiDelayHours(e.target.value)}
                    slotProps={{ htmlInput: { min: 0, step: 0.5 } }}
                  />
                </FieldHint>
              )}
              {roiTranscode && (
                <FieldHint hint={t('cameras.roiCoverageHint')}>
                  <Stack direction="row" spacing={1}>
                    <TextField
                      label={t('cameras.roiMinCoverage')}
                      type="number"
                      size="small"
                      fullWidth
                      value={roiMinCoverage}
                      onChange={(e) => setRoiMinCoverage(e.target.value)}
                      slotProps={{ htmlInput: { min: 0, max: 100, step: 1 } }}
                    />
                    <TextField
                      label={t('cameras.roiMaxCoverage')}
                      type="number"
                      size="small"
                      fullWidth
                      value={roiMaxCoverage}
                      onChange={(e) => setRoiMaxCoverage(e.target.value)}
                      slotProps={{ htmlInput: { min: 0, max: 100, step: 1 } }}
                    />
                  </Stack>
                </FieldHint>
              )}

              {/* One stream, chosen here, because the camera's storage holds a single archive and
                  ONVIF does not report which stream went into it. */}
              {cameraStreams.length > 0 && (
                <FieldHint hint={t('cameras.edgeBackfillHint')}>
                  <TextField
                    select
                    size="small"
                    fullWidth
                    label={t('cameras.edgeBackfill')}
                    value={cameraStreams.some((s) => s.streamName === edgeBackfillStream) ? edgeBackfillStream : ''}
                    onChange={(e) => setEdgeBackfillStream(e.target.value)}
                  >
                    <MenuItem value="">{t('cameras.edgeBackfillOff')}</MenuItem>
                    {cameraStreams.map((s) => (
                      <MenuItem key={s.streamName} value={s.streamName}>{s.streamName}</MenuItem>
                    ))}
                  </TextField>
                </FieldHint>
              )}
            </Box>
            )}
          </Stack>

          {/* Right: live previews column. Hidden on the survey tab, which shows this camera's
              picture itself, beside its master's — a third copy of the same stream would only take
              room away from the two that are being worked with. */}
          {!isNew && tab !== 'binding' && webRtcStreams.length > 0 && (
            <Stack spacing={1.5} sx={{ width: 280, flexShrink: 0 }}>
              {webRtcStreams.filter((s) => s.whepUrl).map((s) => (
                <Box key={s.streamName}>
                  <Typography variant="caption" color="text.secondary" display="block" gutterBottom>
                    {t('cameras.livePreview', { stream: s.streamName })}
                  </Typography>
                  <CameraStream whepUrl={s.whepUrl!} stunUrl={existing?.effectiveWebrtcStunUrl} />
                </Box>
              ))}
            </Stack>
          )}
        </Stack>
      </Box>
    </Box>
  )
}
