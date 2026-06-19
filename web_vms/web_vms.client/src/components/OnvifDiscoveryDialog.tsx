import { useEffect, useState } from 'react'
import {
  Box,
  Button,
  Checkbox,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  FormControlLabel,
  MenuItem,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { addCamera } from '../store/liveSlice'
import type { CameraStreamDto, DiscoveredDeviceDto } from '../dto/dtos'
import PasswordField from './PasswordField'

type Props = {
  open: boolean
  onClose: () => void
  onAdded: () => Promise<void>
}

type OnvifProfile = { token: string; name: string; width: number; height: number; encoding: string }
type StreamDraft = { profile: OnvifProfile; selected: boolean }

type DraftState = {
  cameraId: string
  name: string
  user: string
  pass: string
  streams: StreamDraft[] | null // null = not loaded yet
  loadingStreams: boolean
  streamsError: string | null
}

export default function OnvifDiscoveryDialog({ open, onClose, onAdded }: Props) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const servers = useAppSelector((state) => state.live.servers)
  const camerasMap = useAppSelector((state) => state.live.cameraStatesById)

  const [serverId, setServerId] = useState('')
  const [scanning, setScanning] = useState(false)
  const [devices, setDevices] = useState<DiscoveredDeviceDto[]>([])
  const [scanOk, setScanOk] = useState(true)
  const [hasScanned, setHasScanned] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [drafts, setDrafts] = useState<Record<string, DraftState>>({})
  const [addingId, setAddingId] = useState<string | null>(null)

  // Reset scan results when the dialog is reopened, default server to the first one
  useEffect(() => {
    if (!open) return
    setDevices([])
    setHasScanned(false)
    setDrafts({})
    setError(null)
    setServerId((prev) => prev || servers[0]?.serverId || '')
  }, [open]) // eslint-disable-line react-hooks/exhaustive-deps

  const existingAddrs = new Set(
    Object.values(camerasMap)
      .filter((c) => c.onvifConn?.ip)
      .map((c) => `${c.onvifConn!.ip.trim().toLowerCase()}:${c.onvifConn!.port}`)
  )

  const handleScan = async () => {
    setScanning(true)
    setError(null)
    try {
      const response = await fetch('/api/onvif/discover', { method: 'POST' })
      if (!response.ok) throw new Error(await response.text())
      const result: { scanOk: boolean; devices: DiscoveredDeviceDto[] } = await response.json()
      setDevices(result.devices)
      setScanOk(result.scanOk)
      setHasScanned(true)
      // Materialize a draft (with its cameraId) for every device right away — draftFor()/
      // updateDraft() must never independently mint a fresh cameraId on first access, or
      // a "Load streams" call and a later "Add" call could end up using two different ids
      // and create two cameras (one of them missing whatever streams were loaded).
      setDrafts((prev) => {
        const next = { ...prev }
        for (const device of result.devices) {
          if (!next[device.endpointUuid]) next[device.endpointUuid] = defaultDraft(device)
        }
        return next
      })
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setScanning(false)
    }
  }

  const defaultDraft = (device: DiscoveredDeviceDto): DraftState => ({
    cameraId: crypto.randomUUID(),
    name: device.name || device.hardware || device.ip,
    user: '',
    pass: '',
    streams: null,
    loadingStreams: false,
    streamsError: null,
  })

  const draftFor = (device: DiscoveredDeviceDto): DraftState =>
    drafts[device.endpointUuid] ?? defaultDraft(device)

  const updateDraft = (device: DiscoveredDeviceDto, patch: Partial<DraftState>) =>
    setDrafts((prev) => ({
      ...prev,
      [device.endpointUuid]: { ...(prev[device.endpointUuid] ?? defaultDraft(device)), ...patch },
    }))

  const cameraPayload = (device: DiscoveredDeviceDto, draft: DraftState) => ({
    cameraId: draft.cameraId,
    name: draft.name,
    serverId,
    recordingMode: 'off' as const,
    webRtc: false,
    path: '',
    cred: draft.user || draft.pass ? { user: draft.user, pass: draft.pass } : undefined,
    onvifConn: { ip: device.ip, port: device.port },
  })

  // Pure preview — reads profiles directly from the device (ip/port/credentials), with no
  // side effect on VmsCfg. The camera is only ever created when "Add" is clicked.
  const handleLoadStreams = async (device: DiscoveredDeviceDto) => {
    const draft = draftFor(device)
    updateDraft(device, { loadingStreams: true, streamsError: null })
    try {
      const response = await fetch('/api/onvif/discover/profiles', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ ip: device.ip, port: device.port, user: draft.user, pass: draft.pass }),
      })
      if (!response.ok) throw new Error(await response.text())
      const profiles: OnvifProfile[] = await response.json()
      const streams = profiles.map((profile) => ({
        profile,
        selected: profile.encoding.toUpperCase() !== 'JPEG',
      }))
      updateDraft(device, { streams, loadingStreams: false })
    } catch (e) {
      updateDraft(device, {
        loadingStreams: false,
        streamsError: e instanceof Error ? e.message : String(e),
      })
    }
  }

  const toggleStream = (device: DiscoveredDeviceDto, token: string, selected: boolean) => {
    const draft = draftFor(device)
    if (!draft.streams) return
    updateDraft(device, {
      streams: draft.streams.map((s) => (s.profile.token === token ? { ...s, selected } : s)),
    })
  }

  const handleAdd = async (device: DiscoveredDeviceDto) => {
    if (!serverId) return
    const draft = draftFor(device)
    setAddingId(device.endpointUuid)
    try {
      const selected = draft.streams?.filter((s) => s.selected) ?? []
      const streams: CameraStreamDto[] | undefined = draft.streams
        ? selected.map((s, i) => ({
            streamName: s.profile.token.toLowerCase(),
            profileToken: s.profile.token,
            path: '',
            webRtc: i === 0,
          }))
        : undefined

      const result = await dispatch(addCamera({ ...cameraPayload(device, draft), streams }))
      if (addCamera.rejected.match(result)) {
        setError(result.error.message ?? String(result.error))
        return
      }
      if (!result.payload.success) {
        setError(result.payload.message)
        return
      }
      await onAdded()
    } finally {
      setAddingId(null)
    }
  }

  return (
    <Dialog open={open} onClose={onClose} maxWidth="sm" fullWidth>
      <DialogTitle>{t('cameraControl.discoverDevices')}</DialogTitle>
      <DialogContent>
        <Stack spacing={2} sx={{ mt: 1 }}>
          <Stack direction="row" spacing={2} alignItems="center">
            <TextField
              select
              label={t('cameras.server')}
              value={serverId}
              onChange={(e) => setServerId(e.target.value)}
              size="small"
              sx={{ minWidth: 180 }}
            >
              {servers.map((s) => (
                <MenuItem key={s.serverId} value={s.serverId}>{s.serverId}</MenuItem>
              ))}
            </TextField>
            <Button variant="outlined" onClick={() => void handleScan()} disabled={scanning}>
              {t('cameraControl.scan')}
            </Button>
            {scanning && <CircularProgress size={20} />}
          </Stack>

          {error && <Typography color="error" variant="body2">{error}</Typography>}

          {!scanning && hasScanned && !error && !scanOk && (
            <Typography variant="body2" color="error">
              {t('cameraControl.scanUnavailable')}
            </Typography>
          )}

          {!scanning && hasScanned && !error && scanOk && devices.length === 0 && (
            <Typography variant="body2" color="text.secondary">
              {t('cameraControl.noDevicesFound')}
            </Typography>
          )}

          <Stack spacing={1.5}>
            {devices.map((device) => {
              const addr = `${device.ip.trim().toLowerCase()}:${device.port}`
              const alreadyAdded = existingAddrs.has(addr)
              const draft = draftFor(device)
              return (
                <Box key={device.endpointUuid} sx={{ border: 1, borderColor: 'divider', borderRadius: 1, p: 1.5 }}>
                  <Stack direction="row" spacing={1} alignItems="baseline">
                    <Typography variant="subtitle2">{addr}</Typography>
                    {(device.hardware || device.name) && (
                      <Typography variant="caption" color="text.secondary">
                        {[device.name, device.hardware].filter(Boolean).join(' — ')}
                      </Typography>
                    )}
                  </Stack>

                  {alreadyAdded ? (
                    <Typography variant="caption" color="text.secondary">
                      {t('cameraControl.alreadyAdded')}
                    </Typography>
                  ) : (
                    <>
                      <Stack direction="row" spacing={1} sx={{ mt: 1 }} alignItems="center">
                        <TextField
                          label={t('cameras.cameraName')}
                          value={draft.name}
                          onChange={(e) => updateDraft(device, { name: e.target.value })}
                          size="small"
                          fullWidth
                        />
                        <TextField
                          label={t('common.user')}
                          value={draft.user}
                          onChange={(e) => updateDraft(device, { user: e.target.value })}
                          size="small"
                          fullWidth
                        />
                        <PasswordField pass={draft.pass} setPass={(pass) => updateDraft(device, { pass })} />
                      </Stack>

                      <Stack direction="row" spacing={1} sx={{ mt: 1 }} alignItems="center">
                        <Button
                          variant="text"
                          size="small"
                          disabled={!draft.name.trim() || draft.loadingStreams}
                          onClick={() => void handleLoadStreams(device)}
                        >
                          {draft.loadingStreams ? <CircularProgress size={16} /> : t('cameraControl.loadStreams')}
                        </Button>
                        <Box sx={{ flexGrow: 1 }} />
                        <Button
                          variant="outlined"
                          size="small"
                          disabled={!serverId || !draft.name.trim() || addingId === device.endpointUuid}
                          onClick={() => void handleAdd(device)}
                        >
                          {t('cameraControl.addDevice')}
                        </Button>
                      </Stack>

                      {draft.streamsError && (
                        <Typography variant="caption" color="error" display="block" sx={{ mt: 0.5 }}>
                          {draft.streamsError}
                        </Typography>
                      )}

                      {draft.streams && draft.streams.length === 0 && !draft.streamsError && (
                        <Typography variant="caption" color="text.secondary" display="block" sx={{ mt: 0.5 }}>
                          {t('cameraControl.noStreamsFound')}
                        </Typography>
                      )}

                      {draft.streams && draft.streams.length > 0 && (
                        <Stack sx={{ mt: 0.5 }}>
                          {draft.streams.map((s) => {
                            const res = s.profile.width && s.profile.height ? ` ${s.profile.width}×${s.profile.height}` : ''
                            const label = `${s.profile.name || s.profile.token}${res} ${s.profile.encoding}`
                            return (
                              <FormControlLabel
                                key={s.profile.token}
                                control={
                                  <Checkbox
                                    size="small"
                                    checked={s.selected}
                                    onChange={(e) => toggleStream(device, s.profile.token, e.target.checked)}
                                  />
                                }
                                label={<Typography variant="caption">{label}</Typography>}
                              />
                            )
                          })}
                        </Stack>
                      )}
                    </>
                  )}
                </Box>
              )
            })}
          </Stack>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>{t('common.close')}</Button>
      </DialogActions>
    </Dialog>
  )
}
