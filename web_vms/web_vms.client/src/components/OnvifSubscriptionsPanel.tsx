import { useCallback, useEffect, useState } from 'react'
import {
  Box,
  Chip,
  CircularProgress,
  Divider,
  FormControlLabel,
  Menu,
  MenuItem,
  Stack,
  Switch,
  TextField,
  ToggleButton,
  ToggleButtonGroup,
  Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppSelector } from '../store'
import type { CameraSetupDto, OnvifCameraEntryDto } from '../dto/dtos'
import OnvifAlarmFiltersPanel from './OnvifAlarmFiltersPanel'
import DetailToolbar from './settings/DetailToolbar'
import MasterDetailLayout from './settings/MasterDetailLayout'
import SettingsListItem from './settings/SettingsListItem'
import { useDirtyGuard } from './settings/useDirtyGuard'
import type { ApplyIfDirtyHandle } from './settings/useDirtyGuard'
import { useMasterDetailSelection } from './settings/useMasterDetailSelection'

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

type DetailProps = {
  ref?: React.Ref<ApplyIfDirtyHandle>
  entry: OnvifCameraEntryDto
  cam: CameraSetupDto | undefined
  armed: boolean
  onRemove: () => void
  onToggleSubscribed: (subscribed: boolean) => void
  onToggleArmed: (armed: boolean) => void
  onPollConfigSaved: (pollTimeoutSeconds: number, messageLimit: number) => void
}

function SubscriptionDetail({ ref, entry, cam, armed, onRemove, onToggleSubscribed, onToggleArmed, onPollConfigSaved }: DetailProps) {
  const { t } = useTranslation()

  // Poll config is the only "draft" state here — everything else acts immediately
  const [pollTimeoutSeconds, setPollTimeoutSeconds] = useState(entry.pollTimeoutSeconds)
  const [messageLimit, setMessageLimit] = useState(entry.messageLimit)
  const [snap, setSnap] = useState({ pollTimeoutSeconds: entry.pollTimeoutSeconds, messageLimit: entry.messageLimit })

  const dirty = pollTimeoutSeconds !== snap.pollTimeoutSeconds
    || messageLimit !== snap.messageLimit

  const applyInternal = async (): Promise<boolean> => {
    if (!dirty) return true
    await setPollConfig(entry.cameraId, pollTimeoutSeconds, messageLimit)
    setSnap({ pollTimeoutSeconds, messageLimit })
    onPollConfigSaved(pollTimeoutSeconds, messageLimit)
    return true
  }

  const handleCancel = () => {
    setPollTimeoutSeconds(snap.pollTimeoutSeconds)
    setMessageLimit(snap.messageLimit)
  }

  useDirtyGuard(ref, dirty, applyInternal)

  // Deliberately NOT component="form": OnvifAlarmFiltersPanel below renders its
  // own <form>s, and a nested-form submit would bubble up here and phantom-save
  // a dirty poll config. Apply goes through onApply instead of type=submit.
  return (
    <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      <DetailToolbar
        title={cam?.name || entry.cameraId}
        subtitle={cam?.onvifConn ? `${cam.onvifConn.ip}:${cam.onvifConn.port}` : undefined}
        dirty={dirty}
        onApply={() => void applyInternal()}
        onCancel={handleCancel}
        onDelete={onRemove}
        deleteLabel={t('onvifSubs.removeCamera')}
        extra={
          <FormControlLabel
            control={
              <Switch
                checked={entry.subscribed}
                onChange={(e) => onToggleSubscribed(e.target.checked)}
              />
            }
            label="Subscription"
          />
        }
      />

      <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', p: 2 }}>
        <Stack spacing={2}>
          {/* Guard toggle — immediate action */}
          <Box>
            <Typography variant="caption" color="text.secondary" display="block" gutterBottom>
              {t('cameraCard.guardSection')}
            </Typography>
            <ToggleButtonGroup
              exclusive
              size="small"
              value={armed ? 'armed' : 'disarmed'}
              disabled={!entry.subscribed}
              onChange={(_, v) => v && onToggleArmed(v === 'armed')}
              sx={{
                '& .MuiToggleButton-root': { py: 0.25, px: 1.5, fontSize: '0.75rem' },
                '& .MuiToggleButton-root[value="armed"].Mui-selected': { color: 'success.main', borderColor: 'success.main' },
              }}
            >
              <ToggleButton value="armed">{t('cameraCard.armedToggle')}</ToggleButton>
              <ToggleButton value="disarmed">{t('cameraCard.disarmedToggle')}</ToggleButton>
            </ToggleButtonGroup>
          </Box>

          <Divider />

          {/* Poll configuration — saved via the toolbar Apply */}
          <Box>
            <Typography variant="subtitle2" gutterBottom>{t('onvifSubs.pollConfig')}</Typography>
            <Stack direction="row" spacing={2} alignItems="center">
              <TextField
                type="number"
                size="small"
                label={t('onvifSubs.pollTimeoutSeconds')}
                value={pollTimeoutSeconds}
                onChange={(e) => setPollTimeoutSeconds(Math.max(1, Number(e.target.value)))}
                sx={{ width: 160 }}
                slotProps={{ htmlInput: { min: 1 } }}
              />
              <TextField
                type="number"
                size="small"
                label={t('onvifSubs.messageLimit')}
                value={messageLimit}
                onChange={(e) => setMessageLimit(Math.max(1, Number(e.target.value)))}
                sx={{ width: 160 }}
                slotProps={{ htmlInput: { min: 1 } }}
              />
            </Stack>
          </Box>

          <Divider />

          {/* Filters */}
          <Box>
            <Typography variant="subtitle2" gutterBottom>{t('onvifFilters.tabFilters')}</Typography>
            <OnvifAlarmFiltersPanel cameraId={entry.cameraId} />
          </Box>
        </Stack>
      </Box>
    </Box>
  )
}

export default function OnvifSubscriptionsPanel({ ref }: { ref?: React.Ref<ApplyIfDirtyHandle> }) {
  const { t } = useTranslation()
  const allCameras = useAppSelector((s) => Object.values(s.live.cameraStatesById))
  const allCamerasById = useAppSelector((s) => s.live.cameraStatesById)

  const [managed, setManaged] = useState<OnvifCameraEntryDto[]>([])
  const [armedMap, setArmedMap] = useState<Record<string, boolean>>({})
  const [loading, setLoading] = useState(true)
  const [addMenuAnchor, setAddMenuAnchor] = useState<HTMLElement | null>(null)

  const { selected, detailRef, detailKey, select, setId, reset, guard } =
    useMasterDetailSelection(ref, managed, (e) => e.cameraId)

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

  const handleAddPick = async (cameraId: string) => {
    setAddMenuAnchor(null)
    if (!(await guard())) return
    await addCamera(cameraId)
    await reload()
    setId(cameraId)
  }

  const handleRemove = async (cameraId: string) => {
    await removeCamera(cameraId)
    setManaged((prev) => prev.filter((e) => e.cameraId !== cameraId))
    setArmedMap((prev) => { const next = { ...prev }; delete next[cameraId]; return next })
    if (selected?.cameraId === cameraId) reset()
  }

  const handleToggleSubscribed = async (cameraId: string, subscribed: boolean) => {
    setManaged((prev) => prev.map((e) => e.cameraId === cameraId ? { ...e, subscribed } : e))
    await setSubscribed(cameraId, subscribed)
  }

  const handleToggleArmed = async (cameraId: string, armed: boolean) => {
    setArmedMap((prev) => ({ ...prev, [cameraId]: armed }))
    await setArmed(cameraId, armed)
  }

  const handlePollConfigSaved = (cameraId: string, pollTimeoutSeconds: number, messageLimit: number) => {
    setManaged((prev) => prev.map((e) => e.cameraId === cameraId ? { ...e, pollTimeoutSeconds, messageLimit } : e))
  }

  const managedIds = new Set(managed.map((e) => e.cameraId))
  const available = allCameras.filter((c) => c.onvifConn?.ip && !managedIds.has(c.cameraId))

  if (loading) return <CircularProgress sx={{ m: 4 }} />

  const list = (
    <Box>
      {managed.map((entry) => {
        const armed = armedMap[entry.cameraId] ?? false
        return (
          <SettingsListItem
            key={entry.cameraId}
            selected={selected?.cameraId === entry.cameraId}
            onClick={() => select(entry.cameraId)}
            wrap
            primary={allCamerasById[entry.cameraId]?.name || entry.cameraId}
            secondary={
              <Stack direction="row" spacing={0.5} flexWrap="wrap" component="span" sx={{ mt: 0.25 }}>
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
            }
          />
        )
      })}
      {managed.length === 0 && (
        <Typography variant="body2" color="text.secondary" sx={{ p: 1.5 }}>
          {t('onvifSubs.noCameras')}
        </Typography>
      )}
    </Box>
  )

  return (
    <MasterDetailLayout
      listTitle={t('onvifSubs.listTitle')}
      onAdd={(e) => setAddMenuAnchor(e.currentTarget)}
      addTooltip={t('onvifSubs.addCamera')}
      addDisabled={available.length === 0}
      list={list}
    >
      {selected ? (
        <SubscriptionDetail
          ref={detailRef}
          key={detailKey}
          entry={selected}
          cam={allCamerasById[selected.cameraId]}
          armed={armedMap[selected.cameraId] ?? false}
          onRemove={() => void handleRemove(selected.cameraId)}
          onToggleSubscribed={(subscribed) => void handleToggleSubscribed(selected.cameraId, subscribed)}
          onToggleArmed={(armed) => void handleToggleArmed(selected.cameraId, armed)}
          onPollConfigSaved={(pollTimeoutSeconds, messageLimit) =>
            handlePollConfigSaved(selected.cameraId, pollTimeoutSeconds, messageLimit)}
        />
      ) : (
        <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
          {managed.length > 0 ? t('onvifSubs.selectHint') : ''}
        </Typography>
      )}

      <Menu anchorEl={addMenuAnchor} open={Boolean(addMenuAnchor)} onClose={() => setAddMenuAnchor(null)}>
        {available.map((c) => (
          <MenuItem key={c.cameraId} onClick={() => void handleAddPick(c.cameraId)}>
            {c.name || c.cameraId} ({c.onvifConn!.ip})
          </MenuItem>
        ))}
      </Menu>
    </MasterDetailLayout>
  )
}
