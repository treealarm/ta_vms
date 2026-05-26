import { useEffect, useImperativeHandle, useRef, useState } from 'react'
import { Box, Button, MenuItem, Stack, TextField, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { addCamera, changeCameraParent, fetchCameraState, removeCamera } from '../store/liveSlice'
import type { CameraSetupDto } from '../dto/dtos'
import CameraStoragesControl from './CameraStoragesControl'
import PasswordField from './PasswordField'
import CameraStream from './CameraStream'

export type CameraSetupHandle = { applyIfDirty: () => Promise<void> }

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
  const camerasMap = useAppSelector((state) => state.live.cameraStatesById)
  const existing = camerasMap[initialCameraId]
  const isNew = initialCameraId === ''

  const [cameraId, setCameraId] = useState(initialCameraId)
  const [serverId, setServerId] = useState(initialServerId)
  const [mediaMtxServerId, setMediaMtxServerId] = useState(existing?.mediaMtxServerId ?? '')
  const [user, setUser] = useState(existing?.cred?.user ?? '')
  const [pass, setPass] = useState(existing?.cred?.pass ?? '')
  const [onvifIp, setOnvifIp] = useState(existing?.onvifConn?.ip ?? '')
  const [onvifPort, setOnvifPort] = useState(existing?.onvifConn?.port ?? 0)

  const setNonNegativeNumber = (raw: string, setter: (n: number) => void) => {
    const value = Number(raw)
    if (!Number.isNaN(value)) setter(Math.max(0, value))
  }

  // Snapshot of the saved state — used for dirty detection and Cancel
  const snap = useRef({
    serverId: initialServerId,
    mediaMtxServerId: existing?.mediaMtxServerId ?? '',
    user: existing?.cred?.user ?? '',
    pass: existing?.cred?.pass ?? '',
    onvifIp: existing?.onvifConn?.ip ?? '',
    onvifPort: existing?.onvifConn?.port ?? 0,
  })

  const dirty = !isNew && (
    serverId !== snap.current.serverId
    || mediaMtxServerId !== snap.current.mediaMtxServerId
    || user !== snap.current.user
    || pass !== snap.current.pass
    || onvifIp !== snap.current.onvifIp
    || onvifPort !== snap.current.onvifPort
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
  })

  const applyInternal = async (): Promise<boolean> => {
    if (!dirty) return true
    const payload = buildPayload()
    const serverChanged = !isNew && existing && serverId !== existing.serverId
    const action = serverChanged ? changeCameraParent : addCamera
    const result = await dispatch(action(payload))
    if (!action.rejected.match(result)) {
      snap.current = { serverId, mediaMtxServerId, user, pass, onvifIp, onvifPort }
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
      if (!addCamera.rejected.match(result)) await onSaved()
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
  }

  const handleRemove = async () => {
    const result = await dispatch(removeCamera({ cameraId: initialCameraId }))
    if (!removeCamera.rejected.match(result)) {
      await onRemoved()
    }
  }

  // Expose applyIfDirty for parent (CameraControlPanel) to call before switching selection
  useImperativeHandle(ref, () => ({ applyIfDirty: async () => { await applyInternal() } }))

  // Warn browser on close when dirty
  useEffect(() => {
    if (!dirty) return
    const handler = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = '' }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [dirty])

  return (
    <Stack component="form" direction="row" spacing={3} alignItems="flex-start" onSubmit={handleApply}>
      {/* Left: form fields */}
      <Stack spacing={2} sx={{ flex: 1, minWidth: 0 }}>
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
          <TextField label={t('cameras.onvifIp')} value={onvifIp} onChange={(e) => setOnvifIp(e.target.value)} size="small" />
          <TextField
            label={t('cameras.onvifPort')}
            value={onvifPort}
            onChange={(e) => setNonNegativeNumber(e.target.value, setOnvifPort)}
            size="small"
            sx={{ width: 120 }}
          />
        </Stack>

        <Stack direction="row" spacing={1}>
          <TextField label={t('common.user')} value={user} onChange={(e) => setUser(e.target.value)} size="small" />
          <PasswordField pass={pass} setPass={setPass} />
        </Stack>

        <Stack direction="row" spacing={1}>
          <Button type="submit" variant="outlined" size="small" disabled={!isNew && !dirty}>
            {isNew ? t('cameras.addCamera') : t('common.apply')}
          </Button>
          {!isNew && (
            <Button variant="text" size="small" onClick={handleCancel} disabled={!dirty}>
              {t('common.cancel')}
            </Button>
          )}
          {!isNew && (
            <Button color="error" variant="outlined" size="small" onClick={handleRemove}>
              {t('cameras.deleteCamera')}
            </Button>
          )}
        </Stack>

        {!isNew && <CameraStoragesControl cameraId={initialCameraId} />}
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
}
