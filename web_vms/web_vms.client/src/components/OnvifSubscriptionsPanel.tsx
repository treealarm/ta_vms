import { useCallback, useEffect, useState } from 'react'
import {
  Box,
  Button,
  Chip,
  CircularProgress,
  Divider,
  FormControlLabel,
  IconButton,
  MenuItem,
  Stack,
  Switch,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Tooltip,
  Typography,
} from '@mui/material'
import DeleteIcon from '@mui/icons-material/Delete'
import { useTranslation } from 'react-i18next'
import { useAppSelector } from '../store'
import type { OnvifCameraEntryDto } from '../dto/dtos'
import OnvifAlarmFiltersPanel from './OnvifAlarmFiltersPanel'

async function fetchManaged(): Promise<OnvifCameraEntryDto[]> {
  const r = await fetch('/api/onvif/subscriptions')
  return r.ok ? (r.json() as Promise<OnvifCameraEntryDto[]>) : []
}

async function addCamera(cameraId: string) {
  await fetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/subscription`, { method: 'POST' })
}

async function removeCamera(cameraId: string) {
  await fetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/subscription`, { method: 'DELETE' })
}

async function setSubscribed(cameraId: string, subscribed: boolean) {
  await fetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/subscription`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ subscribed }),
  })
}

async function fetchArmed(cameraId: string): Promise<boolean> {
  const r = await fetch(`/api/cameras/${encodeURIComponent(cameraId)}/state`)
  if (!r.ok) return false
  return ((await r.json()) as { armed: boolean }).armed
}

async function setArmed(cameraId: string, armed: boolean) {
  await fetch(`/api/cameras/${encodeURIComponent(cameraId)}/state`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ armed }),
  })
}

async function setPollConfig(cameraId: string, pollTimeoutSeconds: number, messageLimit: number) {
  await fetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/poll-config`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ pollTimeoutSeconds, messageLimit }),
  })
}

export default function OnvifSubscriptionsPanel() {
  const { t } = useTranslation()
  const allCameras = useAppSelector((s) => Object.values(s.live.cameraStatesById))
  const allCamerasById = useAppSelector((s) => s.live.cameraStatesById)

  const [managed, setManaged] = useState<OnvifCameraEntryDto[]>([])
  const [armedMap, setArmedMap] = useState<Record<string, boolean>>({})
  const [loading, setLoading] = useState(true)
  const [addId, setAddId] = useState('')
  const [selectedId, setSelectedId] = useState<string | null>(null)

  const reload = useCallback(async () => {
    setLoading(true)
    try {
      const entries = await fetchManaged()
      setManaged(entries)
      const pairs = await Promise.all(
        entries.map(async (e) => [e.cameraId, await fetchArmed(e.cameraId)] as const)
      )
      setArmedMap(Object.fromEntries(pairs))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { void reload() }, [reload])

  // Keep selection valid after reload
  useEffect(() => {
    if (selectedId && !managed.find((e) => e.cameraId === selectedId))
      setSelectedId(null)
  }, [managed, selectedId])

  const handleAdd = async () => {
    if (!addId) return
    await addCamera(addId)
    setAddId('')
    await reload()
  }

  const handleRemove = async (cameraId: string) => {
    await removeCamera(cameraId)
    setManaged((prev) => prev.filter((e) => e.cameraId !== cameraId))
    setArmedMap((prev) => { const next = { ...prev }; delete next[cameraId]; return next })
    if (selectedId === cameraId) setSelectedId(null)
  }

  const handleToggleSubscribed = async (cameraId: string, subscribed: boolean) => {
    setManaged((prev) => prev.map((e) => e.cameraId === cameraId ? { ...e, subscribed } : e))
    await setSubscribed(cameraId, subscribed)
  }

  const handleToggleArmed = async (cameraId: string, armed: boolean) => {
    setArmedMap((prev) => ({ ...prev, [cameraId]: armed }))
    await setArmed(cameraId, armed)
  }

  const handlePollConfigChange = (cameraId: string, field: 'pollTimeoutSeconds' | 'messageLimit', value: number) => {
    setManaged((prev) => prev.map((e) => e.cameraId === cameraId ? { ...e, [field]: value } : e))
  }

  const handleSavePollConfig = async (cameraId: string, pollTimeoutSeconds: number, messageLimit: number) => {
    await setPollConfig(cameraId, pollTimeoutSeconds, messageLimit)
  }

  const managedIds = new Set(managed.map((e) => e.cameraId))
  const available = allCameras.filter((c) => c.onvifConn?.ip && !managedIds.has(c.cameraId))
  const selected = managed.find((e) => e.cameraId === selectedId) ?? null
  const selectedCam = selectedId ? allCamerasById[selectedId] : null

  if (loading) return <CircularProgress sx={{ m: 4 }} />

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      {/* Add camera row */}
      <Stack direction="row" spacing={1} alignItems="center" sx={{ mb: 2 }}>
        <TextField
          select
          size="small"
          label={t('onvifSubs.addCamera')}
          value={addId}
          onChange={(e) => setAddId(e.target.value)}
          sx={{ minWidth: 220 }}
          disabled={available.length === 0}
          SelectProps={{ displayEmpty: true }}
        >
          {available.map((c) => (
            <MenuItem key={c.cameraId} value={c.cameraId}>
              {c.cameraId} ({c.onvifConn!.ip})
            </MenuItem>
          ))}
        </TextField>
        <Button variant="contained" size="small" disabled={!addId} onClick={() => void handleAdd()}>
          {t('common.add')}
        </Button>
      </Stack>

      {/* Master-detail */}
      <Box sx={{ flex: 1, minHeight: 0, display: 'flex', gap: 0, border: 1, borderColor: 'divider', borderRadius: 1, overflow: 'hidden' }}>
        {/* Left: camera list */}
        <Box sx={{ width: 220, flexShrink: 0, borderRight: 1, borderColor: 'divider', overflowY: 'auto' }}>
          {managed.length === 0 && (
            <Typography variant="body2" color="text.secondary" sx={{ p: 1.5 }}>
              {t('onvifSubs.noCameras')}
            </Typography>
          )}
          {managed.map((entry) => {
            const armed = armedMap[entry.cameraId] ?? false
            return (
              <Box
                key={entry.cameraId}
                onClick={() => setSelectedId(entry.cameraId)}
                sx={{
                  px: 1.5,
                  py: 1,
                  cursor: 'pointer',
                  borderBottom: 1,
                  borderColor: 'divider',
                  bgcolor: selectedId === entry.cameraId ? 'action.selected' : 'transparent',
                  '&:hover': { bgcolor: selectedId === entry.cameraId ? 'action.selected' : 'action.hover' },
                }}
              >
                <Typography variant="body2" fontWeight={600} noWrap>{entry.cameraId}</Typography>
                <Stack direction="row" spacing={0.5} sx={{ mt: 0.5 }}>
                  <Chip
                    label={entry.subscribed ? 'ON' : 'OFF'}
                    size="small"
                    color={entry.subscribed ? 'success' : 'default'}
                    sx={{ height: 16, fontSize: '0.6rem' }}
                  />
                  {entry.subscribed && (
                    <Chip
                      label={armed ? t('cameraCard.armedToggle') : t('cameraCard.disarmedToggle')}
                      size="small"
                      color={armed ? 'warning' : 'default'}
                      sx={{ height: 16, fontSize: '0.6rem' }}
                    />
                  )}
                </Stack>
              </Box>
            )
          })}
        </Box>

        {/* Right: detail */}
        <Box sx={{ flex: 1, overflowY: 'auto', p: 2 }}>
          {!selected ? (
            <Typography variant="body2" color="text.secondary">
              {managed.length > 0 ? t('logs.selectHint') : ''}
            </Typography>
          ) : (
            <Stack spacing={2} sx={{ height: '100%' }}>
              {/* Header */}
              <Stack direction="row" alignItems="flex-start" justifyContent="space-between">
                <Box>
                  <Typography variant="h6">{selected.cameraId}</Typography>
                  {selectedCam?.onvifConn && (
                    <Typography variant="body2" color="text.secondary">
                      {selectedCam.onvifConn.ip}:{selectedCam.onvifConn.port}
                    </Typography>
                  )}
                </Box>
                <Tooltip title={t('onvifSubs.removeCamera')}>
                  <IconButton color="error" size="small" onClick={() => void handleRemove(selected.cameraId)}>
                    <DeleteIcon fontSize="small" />
                  </IconButton>
                </Tooltip>
              </Stack>

              {/* Toggles */}
              <Stack direction="row" spacing={3} alignItems="center">
                <FormControlLabel
                  control={
                    <Switch
                      checked={selected.subscribed}
                      onChange={(e) => void handleToggleSubscribed(selected.cameraId, e.target.checked)}
                    />
                  }
                  label="Subscription"
                />
                <Box>
                  <Typography variant="caption" color="text.secondary" display="block" gutterBottom>
                    {t('cameraCard.guardSection')}
                  </Typography>
                  <ToggleButtonGroup
                    exclusive
                    size="small"
                    value={(armedMap[selected.cameraId] ?? false) ? 'armed' : 'disarmed'}
                    disabled={!selected.subscribed}
                    onChange={(_, v) => v && void handleToggleArmed(selected.cameraId, v === 'armed')}
                    sx={{
                      '& .MuiToggleButton-root': { py: 0.25, px: 1.5, fontSize: '0.75rem' },
                      '& .MuiToggleButton-root[value="armed"].Mui-selected': { color: 'success.main', borderColor: 'success.main' },
                    }}
                  >
                    <ToggleButton value="armed">{t('cameraCard.armedToggle')}</ToggleButton>
                    <ToggleButton value="disarmed">{t('cameraCard.disarmedToggle')}</ToggleButton>
                  </ToggleButtonGroup>
                </Box>
              </Stack>

              <Divider />

              {/* Poll configuration */}
              <Box>
                <Typography variant="subtitle2" gutterBottom>{t('onvifSubs.pollConfig')}</Typography>
                <Stack direction="row" spacing={2} alignItems="center">
                  <TextField
                    type="number"
                    size="small"
                    label={t('onvifSubs.pollTimeoutSeconds')}
                    value={selected.pollTimeoutSeconds}
                    onChange={(e) => handlePollConfigChange(selected.cameraId, 'pollTimeoutSeconds', Math.max(1, Number(e.target.value)))}
                    sx={{ width: 160 }}
                    slotProps={{ htmlInput: { min: 1 } }}
                  />
                  <TextField
                    type="number"
                    size="small"
                    label={t('onvifSubs.messageLimit')}
                    value={selected.messageLimit}
                    onChange={(e) => handlePollConfigChange(selected.cameraId, 'messageLimit', Math.max(1, Number(e.target.value)))}
                    sx={{ width: 160 }}
                    slotProps={{ htmlInput: { min: 1 } }}
                  />
                  <Button
                    variant="outlined"
                    size="small"
                    onClick={() => void handleSavePollConfig(selected.cameraId, selected.pollTimeoutSeconds, selected.messageLimit)}
                  >
                    {t('common.save')}
                  </Button>
                </Stack>
              </Box>

              <Divider />

              {/* Filters */}
              <Box sx={{ flex: 1 }}>
                <Typography variant="subtitle2" gutterBottom>{t('onvifFilters.tabFilters')}</Typography>
                <OnvifAlarmFiltersPanel cameraId={selected.cameraId} />
              </Box>

            </Stack>
          )}
        </Box>
      </Box>
    </Box>
  )
}
