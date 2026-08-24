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
import { useCameraDeepLink } from './settings/useCameraDeepLink'

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
  onRemove: () => void
  onToggleSubscribed: (subscribed: boolean) => void
  onPollConfigSaved: (pollTimeoutSeconds: number, messageLimit: number) => void
}

function SubscriptionDetail({ ref, entry, cam, onRemove, onToggleSubscribed, onPollConfigSaved }: DetailProps) {
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
            label={t('onvifSubs.subscription')}
          />
        }
      />

      <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', p: 2 }}>
        <Stack spacing={2}>
          {/* Arming is deliberately not here. It is camera-wide and shared by every source, so it
              belongs to the camera (its Guard tab) and to the operator's card, not to the page of
              one source. Filters below say what is worth keeping a clip for; whether anything is
              kept at all is arming's answer, given elsewhere. */}

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
  const [loading, setLoading] = useState(true)
  const [addMenuAnchor, setAddMenuAnchor] = useState<HTMLElement | null>(null)

  const deepLink = useCameraDeepLink()
  const { selected, detailRef, detailKey, select, setId, reset, guard } =
    useMasterDetailSelection(ref, managed, (e) => e.cameraId, deepLink.cameraId)

  const reload = useCallback(async () => {
    setLoading(true)
    try {
      setManaged(await fetchManaged())
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
    if (selected?.cameraId === cameraId) reset()
  }

  const handleToggleSubscribed = async (cameraId: string, subscribed: boolean) => {
    setManaged((prev) => prev.map((e) => e.cameraId === cameraId ? { ...e, subscribed } : e))
    await setSubscribed(cameraId, subscribed)
  }

  const handlePollConfigSaved = (cameraId: string, pollTimeoutSeconds: number, messageLimit: number) => {
    setManaged((prev) => prev.map((e) => e.cameraId === cameraId ? { ...e, pollTimeoutSeconds, messageLimit } : e))
  }

  const managedIds = new Set(managed.map((e) => e.cameraId))
  const available = allCameras.filter((c) => c.onvifConn?.ip && !managedIds.has(c.cameraId))

  if (loading) return <CircularProgress sx={{ m: 4 }} />

  const list = (
    <Box>
      {managed.map((entry) => (
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
              </Stack>
            }
          />
      ))}
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
          onRemove={() => void handleRemove(selected.cameraId)}
          onToggleSubscribed={(subscribed) => void handleToggleSubscribed(selected.cameraId, subscribed)}
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
