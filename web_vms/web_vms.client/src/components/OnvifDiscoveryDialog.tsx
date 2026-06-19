import { useEffect, useState } from 'react'
import {
  Box,
  Button,
  CircularProgress,
  Dialog,
  DialogActions,
  DialogContent,
  DialogTitle,
  MenuItem,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { addCamera } from '../store/liveSlice'
import type { DiscoveredDeviceDto } from '../dto/dtos'
import PasswordField from './PasswordField'

type Props = {
  open: boolean
  onClose: () => void
  onAdded: () => Promise<void>
}

type DraftState = { name: string; user: string; pass: string }

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
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setScanning(false)
    }
  }

  const defaultDraft = (device: DiscoveredDeviceDto): DraftState =>
    ({ name: device.name || device.hardware || device.ip, user: '', pass: '' })

  const draftFor = (device: DiscoveredDeviceDto): DraftState =>
    drafts[device.endpointUuid] ?? defaultDraft(device)

  const updateDraft = (device: DiscoveredDeviceDto, patch: Partial<DraftState>) =>
    setDrafts((prev) => ({
      ...prev,
      [device.endpointUuid]: { ...(prev[device.endpointUuid] ?? defaultDraft(device)), ...patch },
    }))

  const handleAdd = async (device: DiscoveredDeviceDto) => {
    if (!serverId) return
    const draft = draftFor(device)
    setAddingId(device.endpointUuid)
    try {
      const result = await dispatch(addCamera({
        cameraId: crypto.randomUUID(),
        name: draft.name,
        serverId,
        recordingMode: 'off',
        webRtc: false,
        path: '',
        cred: draft.user || draft.pass ? { user: draft.user, pass: draft.pass } : undefined,
        onvifConn: { ip: device.ip, port: device.port },
      }))
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
                    <Stack direction="row" spacing={1} sx={{ mt: 1 }} alignItems="center">
                      <TextField
                        label={t('cameras.cameraName')}
                        value={draft.name}
                        onChange={(e) => updateDraft(device, { name: e.target.value })}
                        size="small"
                        sx={{ flex: 1 }}
                      />
                      <TextField
                        label={t('common.user')}
                        value={draft.user}
                        onChange={(e) => updateDraft(device, { user: e.target.value })}
                        size="small"
                        sx={{ flex: 1 }}
                      />
                      <Box sx={{ flex: 1 }}>
                        <PasswordField pass={draft.pass} setPass={(pass) => updateDraft(device, { pass })} />
                      </Box>
                      <Button
                        variant="outlined"
                        size="small"
                        disabled={!serverId || !draft.name.trim() || addingId === device.endpointUuid}
                        onClick={() => void handleAdd(device)}
                      >
                        {t('cameraControl.addDevice')}
                      </Button>
                    </Stack>
                  )}
                </Box>
              )
            })}
          </Stack>
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>{t('common.cancel')}</Button>
      </DialogActions>
    </Dialog>
  )
}
