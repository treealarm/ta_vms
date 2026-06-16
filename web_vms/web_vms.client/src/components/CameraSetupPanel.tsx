import { useEffect, useImperativeHandle, useRef, useState } from 'react'
import { Box, Button, FormControlLabel, MenuItem, Stack, Switch, TextField, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { addCamera, changeCameraParent, fetchCameraState, removeCamera } from '../store/liveSlice'
import { setCameraStorages } from '../store/storageSlice'
import type { CameraSetupDto, CameraStorageAssignmentDto } from '../dto/dtos'
import CameraStoragesControl from './CameraStoragesControl'
import PasswordField from './PasswordField'
import CameraStream from './CameraStream'

export type CameraSetupHandle = { applyIfDirty: () => Promise<boolean> }

type Props = {
  ref?: React.Ref<CameraSetupHandle>
  cameraId: string  // '' = new camera
  serverId: string  // pre-selected server
  onSaved: () => Promise<void>
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

  const [cameraId, setCameraId] = useState(() => {
    if (!isNew) return initialCameraId
    const ids = new Set(Object.keys(camerasMap))
    let n = ids.size + 1
    while (ids.has(`cam${n}`)) n++
    return `cam${n}`
  })
  const [serverId, setServerId] = useState(initialServerId)
  const [mediaMtxServerId, setMediaMtxServerId] = useState(
    isNew ? (mtxServers[0]?.id ?? '') : (existing?.mediaMtxServerId ?? '')
  )
  const [user, setUser] = useState(existing?.cred?.user ?? '')
  const [pass, setPass] = useState(existing?.cred?.pass ?? '')
  const [onvifIp, setOnvifIp] = useState(existing?.onvifConn?.ip ?? '')
  const [onvifPort, setOnvifPort] = useState(isNew ? 80 : (existing?.onvifConn?.port ?? 0))
  const [disabled, setDisabled] = useState(existing?.disabled === true)
  const [pendingStorages, setPendingStorages] = useState<CameraStorageAssignmentDto[]>([])

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
  const snap = useRef({
    serverId: initialServerId,
    mediaMtxServerId: existing?.mediaMtxServerId ?? '',
    user: existing?.cred?.user ?? '',
    pass: existing?.cred?.pass ?? '',
    onvifIp: existing?.onvifConn?.ip ?? '',
    onvifPort: existing?.onvifConn?.port ?? 0,
    disabled: existing?.disabled === true,
  })

  const dirty = !isNew && (
    serverId !== snap.current.serverId
    || mediaMtxServerId !== snap.current.mediaMtxServerId
    || user !== snap.current.user
    || pass !== snap.current.pass
    || onvifIp !== snap.current.onvifIp
    || onvifPort !== snap.current.onvifPort
    || disabled !== snap.current.disabled
  )

  // WebRTC streams available for preview
  const webRtcStreams = (existing?.streams ?? []).filter((s) => s.webRtc)

  // WebRTC URL: prefer selected MediaMTX server's webrtcUrl, fall back to camera's stored value
  const selectedMtxServer = mtxServers.find((s) => s.id === mediaMtxServerId)
  const previewBaseUrl = selectedMtxServer?.webrtcUrl ?? existing?.mediaMtxWebrtcUrl

  const buildPayload = (): CameraSetupDto => ({
    cameraId: isNew ? cameraId : initialCameraId,
    serverId,
    recordingMode: existing?.recordingMode ?? 'off',
    webRtc: existing?.webRtc ?? false,
    rtspUrl: existing?.rtspUrl ?? '',
    streams: existing?.streams ?? [],
    cred: user || pass ? { user, pass } : undefined,
    onvifConn: onvifIp ? { ip: onvifIp, port: onvifPort } : undefined,
    mediaMtxServerId: mediaMtxServerId || undefined,
    disabled: disabled || undefined,
  })

  const applyInternal = async (): Promise<boolean> => {
    if (!dirty) return true
    const payload = buildPayload()
    const serverChanged = !isNew && existing && serverId !== existing.serverId
    const action = serverChanged ? changeCameraParent : addCamera
    const result = await dispatch(action(payload))
    if (!action.rejected.match(result)) {
      snap.current = { serverId, mediaMtxServerId, user, pass, onvifIp, onvifPort, disabled }
      await dispatch(fetchCameraState(initialCameraId))
      return true
    }
    return false
  }

  const handleApply = async (e: React.FormEvent) => {
    e.preventDefault()
    if (isNew) {
      const payload = buildPayload()
      const result = await dispatch(addCamera(payload))
      if (!addCamera.rejected.match(result)) {
        if (pendingStorages.length > 0) {
          const withId = pendingStorages.map((a, i) => ({ ...a, cameraId: payload.cameraId, priority: i }))
          await dispatch(setCameraStorages({ cameraId: payload.cameraId, assignments: withId }))
        }
        await onSaved()
      }
    } else {
      const ok = await applyInternal()
      if (ok) await onSaved()
    }
  }

  const handleCancel = () => {
    setServerId(snap.current.serverId)
    setMediaMtxServerId(snap.current.mediaMtxServerId)
    setUser(snap.current.user)
    setPass(snap.current.pass)
    setOnvifIp(snap.current.onvifIp)
    setOnvifPort(snap.current.onvifPort)
    setDisabled(snap.current.disabled)
  }

  const handleRemove = async () => {
    const result = await dispatch(removeCamera({ cameraId: initialCameraId }))
    if (!removeCamera.rejected.match(result)) {
      await onRemoved()
    }
  }

  // Expose applyIfDirty for parent (CameraControlPanel) to call before switching selection
  useImperativeHandle(ref, () => ({ applyIfDirty: () => applyInternal() }))

  // Warn browser on close when dirty
  useEffect(() => {
    if (!dirty) return
    const handler = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = '' }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [dirty])

  const setupForm = (
    <Stack component="form" direction="row" spacing={3} alignItems="flex-start" onSubmit={handleApply}>
      {/* Left: form fields */}
      <Stack spacing={2} sx={{ flex: 1, minWidth: 0, maxWidth: 380 }}>
        <Typography variant="subtitle2" color="text.secondary">
          {isNew ? t('cameras.newCamera') : t('cameras.cameraLabel', { id: initialCameraId })}
        </Typography>

        <TextField
          label={t('cameras.cameraId')}
          value={isNew ? cameraId : initialCameraId}
          onChange={(e) => setCameraId(e.target.value)}
          disabled={!isNew}
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
          <TextField label={t('cameras.onvifIp')} value={onvifIp} onChange={(e) => setOnvifIp(e.target.value)} size="small" sx={{ flex: 1 }} />
          <TextField
            label={t('cameras.onvifPort')}
            value={onvifPort}
            onChange={(e) => setNonNegativeNumber(e.target.value, setOnvifPort)}
            size="small"
            sx={{ width: 100 }}
          />
        </Stack>

        <Stack direction="row" spacing={1}>
          <TextField label={t('common.user')} value={user} onChange={(e) => setUser(e.target.value)} size="small" sx={{ flex: 1 }} />
          <Box sx={{ flex: 1 }}><PasswordField pass={pass} setPass={setPass} /></Box>
        </Stack>

        <FormControlLabel
          control={<Switch checked={disabled} onChange={(e) => setDisabled(e.target.checked)} color="warning" />}
          label={t('cameras.disabled')}
        />

        {!isNew && (
          <Stack direction="row" spacing={1}>
            <Button type="submit" variant="outlined" size="small" disabled={!dirty}>
              {t('common.apply')}
            </Button>
            <Button variant="text" size="small" onClick={handleCancel} disabled={!dirty}>
              {t('common.cancel')}
            </Button>
            <Button color="error" variant="outlined" size="small" onClick={handleRemove}>
              {t('cameras.deleteCamera')}
            </Button>
          </Stack>
        )}

        {isNew
          ? <CameraStoragesControl value={pendingStorages} onChange={setPendingStorages} />
          : <CameraStoragesControl cameraId={initialCameraId} />
        }

        {isNew && (
          <Stack direction="row">
            <Button type="submit" variant="outlined" size="small">
              {t('cameras.addCamera')}
            </Button>
          </Stack>
        )}
      </Stack>

      {/* Right: live previews column */}
      {!isNew && webRtcStreams.length > 0 && (
        <Stack spacing={1.5} sx={{ width: 280, flexShrink: 0 }}>
          {webRtcStreams.map((s) => (
            <Box key={s.streamName}>
              <Typography variant="caption" color="text.secondary" display="block" gutterBottom>
                {t('cameras.livePreview', { stream: s.streamName })}
              </Typography>
              <CameraStream
                streamName={`${initialCameraId}/${s.streamName}`}
                baseUrl={previewBaseUrl}
              />
            </Box>
          ))}
        </Stack>
      )}
    </Stack>
  )

  return setupForm
}
