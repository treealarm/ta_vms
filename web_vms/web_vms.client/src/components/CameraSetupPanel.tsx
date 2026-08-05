import { useEffect, useState } from 'react'
import { Box, FormControlLabel, MenuItem, Stack, Switch, TextField, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { addCamera, changeCameraParent, fetchCameraState, removeCamera } from '../store/liveSlice'
import { setCameraStorages } from '../store/storageSlice'
import { setCameraSchedules } from '../store/scheduleSlice'
import type { CameraSetupDto, CameraStorageAssignmentDto } from '../dto/dtos'
import CameraStoragesControl from './CameraStoragesControl'
import CameraSchedulesControl from './CameraSchedulesControl'
import PasswordField from './PasswordField'
import CameraStream from './CameraStream'
import DetailToolbar from './settings/DetailToolbar'
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
  const [mediaMtxServerId, setMediaMtxServerId] = useState(
    isNew ? (mtxServers[0]?.id ?? '') : (existing?.mediaMtxServerId ?? '')
  )
  const [user, setUser] = useState(existing?.cred?.user ?? '')
  const [pass, setPass] = useState(existing?.cred?.pass ?? '')
  const [ip, setIp] = useState(existing?.onvifConn?.ip ?? '')
  const [port, setPort] = useState(isNew ? 80 : (existing?.onvifConn?.port ?? 0))
  const [disabled, setDisabled] = useState(existing?.disabled === true)
  const [edgeBackfillStream, setEdgeBackfillStream] = useState(existing?.edgeBackfillStream ?? '')
  const [pendingStorages, setPendingStorages] = useState<CameraStorageAssignmentDto[]>([])
  const [pendingSchedules, setPendingSchedules] = useState<string[]>([])

  const setNonNegativeNumber = (raw: string, setter: (n: number) => void) => {
    const value = Number(raw)
    if (!Number.isNaN(value)) setter(Math.max(0, value))
  }

  // Pre-select first MediaMTX server once the list loads (in case store wasn't ready on mount)
  useEffect(() => {
    if (isNew && !mediaMtxServerId && mtxServers.length > 0)
      setMediaMtxServerId(mtxServers[0].id)
  }, [mtxServers]) // eslint-disable-line react-hooks/exhaustive-deps

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
      setSnap({ name, serverId, mediaMtxServerId, user, pass, ip, port, disabled, edgeBackfillStream })
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
          {/* Left: form fields */}
          <Stack spacing={2} sx={{ flex: 1, minWidth: 0, maxWidth: 380 }}>
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

            <TextField
              select
              label={t('cameras.mediaMtxServer')}
              value={mediaMtxServerId}
              onChange={(e) => setMediaMtxServerId(e.target.value)}
              size="small"
              helperText={t('cameras.mediaMtxHelper')}
            >
              <MenuItem value="">{t('cameras.autoAssign')}</MenuItem>
              {mtxServers.map((s) => (
                <MenuItem key={s.id} value={s.id}>{s.name}</MenuItem>
              ))}
            </TextField>

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

            {/* One stream, chosen here, because the camera's storage holds a single archive and
                ONVIF does not report which stream went into it. */}
            {cameraStreams.length > 0 && (
              <TextField
                select
                size="small"
                fullWidth
                label={t('cameras.edgeBackfill')}
                value={cameraStreams.some((s) => s.streamName === edgeBackfillStream) ? edgeBackfillStream : ''}
                onChange={(e) => setEdgeBackfillStream(e.target.value)}
                helperText={t('cameras.edgeBackfillHint')}
              >
                <MenuItem value="">{t('cameras.edgeBackfillOff')}</MenuItem>
                {cameraStreams.map((s) => (
                  <MenuItem key={s.streamName} value={s.streamName}>{s.streamName}</MenuItem>
                ))}
              </TextField>
            )}

            {isNew
              ? <CameraStoragesControl value={pendingStorages} onChange={setPendingStorages} />
              : <CameraStoragesControl cameraId={initialCameraId} />
            }

            {isNew
              ? <CameraSchedulesControl value={pendingSchedules} onChange={setPendingSchedules} />
              : <CameraSchedulesControl cameraId={initialCameraId} />
            }
          </Stack>

          {/* Right: live previews column */}
          {!isNew && webRtcStreams.length > 0 && (
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
