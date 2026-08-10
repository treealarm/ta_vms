import { useEffect, useState } from 'react'
import { Alert, Box, Button, IconButton, InputAdornment, List, MenuItem, Stack, TextField, Tooltip, Typography } from '@mui/material'
import ClearIcon from '@mui/icons-material/Clear'
import RestartAltIcon from '@mui/icons-material/RestartAlt'
import { useTranslation } from 'react-i18next'
import { DEFAULT_STUN_URL, isValidStunUrl } from '../utils/iceServers'
import { useAppDispatch, useAppSelector } from '../store'
import { loadMediaMtxServers, removeMediaMtxServer, upsertMediaMtxServer } from '../store/mediaMtxSlice'
import type { MediaMtxServerDto } from '../dto/dtos'
import DetailToolbar from './settings/DetailToolbar'
import MasterDetailLayout from './settings/MasterDetailLayout'
import SettingsListItem from './settings/SettingsListItem'
import { useDirtyGuard } from './settings/useDirtyGuard'
import type { ApplyIfDirtyHandle } from './settings/useDirtyGuard'
import { useMasterDetailSelection } from './settings/useMasterDetailSelection'

const EMPTY: MediaMtxServerDto = {
  id: '', name: '', host: '', webrtcHost: '',
  rtspPort: 8554, webrtcPort: 8889, apiPort: 9997,
  // New routers start on the public STUN (what the client used to hardcode); clearing the field
  // is the explicit "LAN only, no STUN" choice.
  stunUrl: DEFAULT_STUN_URL,
  // Empty means shared: usable by cameras of any node, which is what a single-box install wants.
  serverId: '',
}

type FormProps = {
  ref?: React.Ref<ApplyIfDirtyHandle>
  server: MediaMtxServerDto | null  // null = new
  onSaved: () => void
  onRemoved: () => void
}

function MediaMtxServerForm({ ref, server, onSaved, onRemoved }: FormProps) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const { isBusy, status } = useAppSelector((s) => s.mediaMtx)
  // The nodes a router can be pinned to — same list the camera form offers.
  const mediaServers = useAppSelector((s) => s.live.servers)
  const isNew = server === null

  const [form, setForm] = useState<MediaMtxServerDto>(server ?? { ...EMPTY })
  // Snapshot of the saved state — used for dirty detection and Cancel
  const [snap, setSnap] = useState<MediaMtxServerDto>(server ?? { ...EMPTY })

  const dirty = !isNew && (
    form.name !== snap.name
    || form.host !== snap.host
    || form.webrtcHost !== snap.webrtcHost
    || form.rtspPort !== snap.rtspPort
    || form.webrtcPort !== snap.webrtcPort
    || form.apiPort !== snap.apiPort
    || form.stunUrl !== snap.stunUrl
    || form.serverId !== snap.serverId
  )

  // Empty is a valid choice ("no STUN, LAN only"); anything else has to be a URL the browser's
  // RTCPeerConnection can actually parse, otherwise live playback dies on every camera behind
  // this router.
  const stunUrlInvalid = Boolean(form.stunUrl.trim()) && !isValidStunUrl(form.stunUrl)

  const applyInternal = async (): Promise<boolean> => {
    if (stunUrlInvalid) return false
    if (!isNew && !dirty) return true
    const result = await dispatch(upsertMediaMtxServer(form))
    if (upsertMediaMtxServer.rejected.match(result) || !result.payload.success) return false
    await dispatch(loadMediaMtxServers())
    setSnap({ ...form })
    onSaved()
    return true
  }

  const handleApply = async (e: React.FormEvent) => {
    e.preventDefault()
    await applyInternal()
  }

  const handleCancel = () => setForm({ ...snap })

  const handleRemove = async () => {
    const result = await dispatch(removeMediaMtxServer(form.id))
    // A fulfilled-but-unsuccessful delete must keep the form (and its status
    // Alert) mounted — otherwise the failure message is never seen
    if (removeMediaMtxServer.rejected.match(result) || !result.payload.success) return
    await dispatch(loadMediaMtxServers())
    onRemoved()
  }

  // Fills in everything that has a sensible default (ports + public STUN) and leaves the fields
  // that identify this particular router — name, host, WebRTC host — alone.
  const applyDefaults = () => setForm((prev) => ({
    ...prev,
    rtspPort: EMPTY.rtspPort,
    webrtcPort: EMPTY.webrtcPort,
    apiPort: EMPTY.apiPort,
    stunUrl: DEFAULT_STUN_URL,
  }))

  const setPort = (field: 'rtspPort' | 'webrtcPort' | 'apiPort', raw: string) => {
    const v = parseInt(raw, 10)
    if (!Number.isNaN(v) && v > 0 && v < 65536)
      setForm((prev) => ({ ...prev, [field]: v }))
  }

  // For a new form "dirty" means "complete enough to save" — navigation then
  // auto-creates it (matching the pre-refactor behavior) instead of silently
  // discarding the entered data
  const canApply = (isNew ? Boolean(form.name.trim() && form.host.trim()) : dirty) && !stunUrlInvalid

  useDirtyGuard(ref, canApply, applyInternal)

  return (
    <Box component="form" onSubmit={(e) => void handleApply(e)} sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      <DetailToolbar
        title={isNew ? t('mediaMtx.newServer') : server.name}
        subtitle={isNew ? undefined : `${server.host}:${server.rtspPort}`}
        dirty={canApply}
        busy={isBusy}
        applyLabel={isNew ? t('common.add') : undefined}
        onCancel={isNew ? undefined : handleCancel}
        onDelete={isNew ? undefined : () => void handleRemove()}
        deleteLabel={t('mediaMtx.deleteServer')}
      />

      <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', p: 2 }}>
        <Stack spacing={2} sx={{ maxWidth: 420 }}>
          {status !== 'Ready' && (
            <Alert severity={status.startsWith('Error') || status.startsWith('Fail') ? 'error' : 'success'}>
              {status}
            </Alert>
          )}

          <TextField
            label={t('common.name')}
            value={form.name}
            onChange={(e) => setForm((prev) => ({ ...prev, name: e.target.value }))}
            size="small"
            required
          />
          <TextField
            label={t('mediaMtx.host')}
            placeholder="192.168.1.100"
            value={form.host}
            onChange={(e) => setForm((prev) => ({ ...prev, host: e.target.value }))}
            size="small"
            required
          />
          <TextField
            label={t('mediaMtx.webrtcHost')}
            placeholder="192.168.1.100"
            value={form.webrtcHost}
            onChange={(e) => setForm((prev) => ({ ...prev, webrtcHost: e.target.value }))}
            size="small"
            helperText={t('mediaMtx.webrtcHostHelper')}
          />
          <TextField
            select
            label={t('mediaMtx.nodeServerId')}
            value={form.serverId}
            onChange={(e) => setForm((prev) => ({ ...prev, serverId: e.target.value }))}
            size="small"
            helperText={t('mediaMtx.nodeServerIdHelper')}
          >
            <MenuItem value="">{t('mediaMtx.nodeServerIdShared')}</MenuItem>
            {mediaServers.map((s) => (
              <MenuItem key={s.serverId} value={s.serverId}>{s.serverId}</MenuItem>
            ))}
          </TextField>
          <TextField
            label={t('mediaMtx.stunUrl')}
            value={form.stunUrl}
            onChange={(e) => setForm((prev) => ({ ...prev, stunUrl: e.target.value }))}
            size="small"
            error={stunUrlInvalid}
            helperText={
              stunUrlInvalid
                ? t('mediaMtx.stunUrlInvalid')
                : form.stunUrl.trim() ? t('mediaMtx.stunUrlHelper') : t('mediaMtx.stunUrlEmptyHelper')
            }
            InputProps={{
              endAdornment: form.stunUrl ? (
                <InputAdornment position="end">
                  <Tooltip title={t('mediaMtx.stunUrlClear')}>
                    <IconButton size="small" onClick={() => setForm((prev) => ({ ...prev, stunUrl: '' }))}>
                      <ClearIcon fontSize="small" />
                    </IconButton>
                  </Tooltip>
                </InputAdornment>
              ) : undefined,
            }}
          />
          <Stack direction="row" spacing={1}>
            <TextField
              label={t('mediaMtx.rtspPort')}
              value={form.rtspPort}
              onChange={(e) => setPort('rtspPort', e.target.value)}
              size="small"
              sx={{ width: 120 }}
              type="number"
            />
            <TextField
              label={t('mediaMtx.webrtcPort')}
              value={form.webrtcPort}
              onChange={(e) => setPort('webrtcPort', e.target.value)}
              size="small"
              sx={{ width: 120 }}
              type="number"
            />
            <TextField
              label={t('mediaMtx.apiPort')}
              value={form.apiPort}
              onChange={(e) => setPort('apiPort', e.target.value)}
              size="small"
              sx={{ width: 120 }}
              type="number"
            />
          </Stack>

          <Box>
            <Button size="small" variant="outlined" startIcon={<RestartAltIcon />} onClick={applyDefaults}>
              {t('mediaMtx.setDefaults')}
            </Button>
            <Typography variant="caption" color="text.secondary" display="block" sx={{ mt: 0.5 }}>
              {t('mediaMtx.setDefaultsHelper')}
            </Typography>
          </Box>
        </Stack>
      </Box>
    </Box>
  )
}

export default function MediaMtxServersPanel({ ref }: { ref?: React.Ref<ApplyIfDirtyHandle> }) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const servers = useAppSelector((s) => s.mediaMtx.servers)

  const { isNew, selected, detailRef, detailKey, select, selectNew, reset } =
    useMasterDetailSelection(ref, servers, (s) => s.id)

  useEffect(() => {
    dispatch(loadMediaMtxServers())
  }, [dispatch])

  const list = (
    <List dense disablePadding>
      {servers.map((s) => (
        <SettingsListItem
          key={s.id}
          selected={selected?.id === s.id}
          onClick={() => select(s.id)}
          primary={s.name}
          secondary={`${s.host}:${s.rtspPort}`}
        />
      ))}
      {servers.length === 0 && (
        <Typography variant="body2" color="text.secondary" sx={{ p: 1.5 }}>
          {t('mediaMtx.noServers')}
        </Typography>
      )}
    </List>
  )

  return (
    <MasterDetailLayout
      listTitle={t('mediaMtx.title')}
      onAdd={selectNew}
      addTooltip={t('mediaMtx.addServer')}
      list={list}
    >
      {isNew || selected ? (
        <MediaMtxServerForm
          ref={detailRef}
          key={detailKey}
          server={isNew ? null : selected}
          onSaved={() => { if (isNew) reset() }}
          onRemoved={reset}
        />
      ) : (
        <Typography color="text.secondary" sx={{ p: 2 }}>
          {t('mediaMtx.selectHint')}
        </Typography>
      )}
    </MasterDetailLayout>
  )
}
