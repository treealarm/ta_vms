import { useEffect, useMemo, useState } from 'react'
import {
  Alert, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogContentText,
  DialogTitle, FormControlLabel, List, Stack, Switch, Table, TableBody, TableCell, TableHead,
  TableRow, TextField, Tooltip, Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { loadCameras } from '../store/liveSlice'
import {
  loadAclMode, loadAllPermissions, loadPermissionUsers, saveUserPermissions, setAclMode,
} from '../store/cameraPermissionsSlice'
import type { CameraPermissionFlagsDto, UserCameraPermissionsDto } from '../dto/dtos'
import DetailToolbar from './settings/DetailToolbar'
import MasterDetailLayout from './settings/MasterDetailLayout'
import SettingsListItem from './settings/SettingsListItem'
import { useDirtyGuard } from './settings/useDirtyGuard'
import type { ApplyIfDirtyHandle } from './settings/useDirtyGuard'
import { useMasterDetailSelection } from './settings/useMasterDetailSelection'

const ACTIONS = ['viewLive', 'viewArchive', 'ptz', 'control', 'export'] as const
type Action = typeof ACTIONS[number]

const NO_FLAGS: CameraPermissionFlagsDto = {
  viewLive: false, viewArchive: false, ptz: false, control: false, export: false,
}

const hasAny = (f: CameraPermissionFlagsDto | null | undefined) =>
  f != null && ACTIONS.some((a) => f[a])

function CameraPermissionsForm({ ref, username }: {
  ref?: React.Ref<ApplyIfDirtyHandle>
  username: string
}) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const { isBusy, status, byUser } = useAppSelector((s) => s.cameraPermissions)
  const cameras = useAppSelector((s) => s.live.cameraStatesById)

  const stored = byUser[username]
  const snapshot = useMemo<UserCameraPermissionsDto>(() => ({
    username,
    allCameras: stored?.allCameras ?? null,
    cameras: stored?.cameras ?? {},
  }), [username, stored])

  const [form, setForm] = useState<UserCameraPermissionsDto>(snapshot)
  const [snap, setSnap] = useState<UserCameraPermissionsDto>(snapshot)
  const [filter, setFilter] = useState('')

  // Remount happens per user (key on the call site), but the stored grants arrive asynchronously —
  // adopt them when they land, as long as the admin hasn't started editing.
  useEffect(() => {
    setForm(snapshot)
    setSnap(snapshot)
  }, [snapshot])

  const dirty = JSON.stringify(form) !== JSON.stringify(snap)

  const cameraList = useMemo(() => {
    const needle = filter.trim().toLowerCase()
    return Object.values(cameras)
      .filter((c) => !needle
        || (c.name ?? '').toLowerCase().includes(needle)
        || c.cameraId.toLowerCase().includes(needle))
      .sort((a, b) => (a.name || a.cameraId).localeCompare(b.name || b.cameraId))
  }, [cameras, filter])

  const blanket = form.allCameras ?? NO_FLAGS

  const setBlanket = (action: Action, value: boolean) =>
    setForm((prev) => {
      const next = { ...(prev.allCameras ?? NO_FLAGS), [action]: value }
      return { ...prev, allCameras: hasAny(next) ? next : null }
    })

  const setCamera = (cameraId: string, action: Action, value: boolean) =>
    setForm((prev) => {
      const current = prev.cameras[cameraId] ?? NO_FLAGS
      const next = { ...current, [action]: value }
      const cameras = { ...prev.cameras }
      // An all-false entry and a missing entry mean the same thing; keep the payload minimal.
      if (hasAny(next)) cameras[cameraId] = next
      else delete cameras[cameraId]
      return { ...prev, cameras }
    })

  // Column header toggle: applies to the cameras currently listed, so a search narrows its reach.
  const setColumn = (action: Action, value: boolean) =>
    setForm((prev) => {
      const cameras = { ...prev.cameras }
      for (const camera of cameraList) {
        const next = { ...(cameras[camera.cameraId] ?? NO_FLAGS), [action]: value }
        if (hasAny(next)) cameras[camera.cameraId] = next
        else delete cameras[camera.cameraId]
      }
      return { ...prev, cameras }
    })

  const applyInternal = async (): Promise<boolean> => {
    if (!dirty) return true
    const result = await dispatch(saveUserPermissions(form))
    if (saveUserPermissions.rejected.match(result) || !result.payload.success) return false
    await dispatch(loadAllPermissions())
    setSnap(form)
    return true
  }

  useDirtyGuard(ref, dirty, applyInternal)

  const grantedCount = Object.keys(form.cameras).length

  return (
    <Box
      component="form"
      onSubmit={(e) => { e.preventDefault(); void applyInternal() }}
      sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}
    >
      <DetailToolbar
        title={username}
        subtitle={hasAny(form.allCameras)
          ? t('permissions.allCameras')
          : t('permissions.nCameras', { count: grantedCount })}
        dirty={dirty}
        busy={isBusy}
        onCancel={() => setForm(snap)}
      />

      <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', p: 2 }}>
        <Stack spacing={2}>
          {status !== 'Ready' && (
            <Alert severity={status.startsWith('Error') || status.startsWith('Fail') ? 'error' : 'success'}>
              {status}
            </Alert>
          )}

          <Box>
            <Typography variant="subtitle2">{t('permissions.allCameras')}</Typography>
            <Typography variant="caption" color="text.secondary" display="block" gutterBottom>
              {t('permissions.allCamerasHelper')}
            </Typography>
            <Stack direction="row" flexWrap="wrap">
              {ACTIONS.map((action) => (
                <FormControlLabel
                  key={action}
                  control={(
                    <Checkbox
                      size="small"
                      checked={blanket[action]}
                      onChange={(e) => setBlanket(action, e.target.checked)}
                    />
                  )}
                  label={t(`permissions.${action}`)}
                />
              ))}
            </Stack>
          </Box>

          <TextField
            label={t('permissions.searchCameras')}
            value={filter}
            onChange={(e) => setFilter(e.target.value)}
            size="small"
            sx={{ maxWidth: 320 }}
          />

          <Box sx={{ overflowX: 'auto' }}>
            <Table size="small" sx={{ minWidth: 560 }}>
              <TableHead>
                <TableRow>
                  <TableCell>{t('common.name')}</TableCell>
                  {ACTIONS.map((action) => (
                    <TableCell key={action} align="center" sx={{ whiteSpace: 'nowrap' }}>
                      <Stack alignItems="center">
                        <span>{t(`permissions.${action}`)}</span>
                        <Tooltip title={t('permissions.toggleColumn')}>
                          <Checkbox
                            size="small"
                            disabled={blanket[action] || cameraList.length === 0}
                            checked={cameraList.length > 0
                              && cameraList.every((c) => form.cameras[c.cameraId]?.[action])}
                            onChange={(e) => setColumn(action, e.target.checked)}
                          />
                        </Tooltip>
                      </Stack>
                    </TableCell>
                  ))}
                </TableRow>
              </TableHead>
              <TableBody>
                {cameraList.map((camera) => (
                  <TableRow key={camera.cameraId} hover>
                    <TableCell>{camera.name || camera.cameraId}</TableCell>
                    {ACTIONS.map((action) => {
                      const grantedByBlanket = blanket[action]
                      const cell = (
                        <Checkbox
                          size="small"
                          // A blanket grant already covers this cell — show it as granted and
                          // frozen instead of letting an unchecked box imply "no access".
                          checked={grantedByBlanket || (form.cameras[camera.cameraId]?.[action] ?? false)}
                          disabled={grantedByBlanket}
                          onChange={(e) => setCamera(camera.cameraId, action, e.target.checked)}
                        />
                      )
                      return (
                        <TableCell key={action} align="center" padding="none">
                          {grantedByBlanket
                            ? <Tooltip title={t('permissions.grantedByAllCameras')}><span>{cell}</span></Tooltip>
                            : cell}
                        </TableCell>
                      )
                    })}
                  </TableRow>
                ))}
                {cameraList.length === 0 && (
                  <TableRow>
                    <TableCell colSpan={ACTIONS.length + 1}>
                      <Typography variant="body2" color="text.secondary">
                        {t('permissions.noCameras')}
                      </Typography>
                    </TableCell>
                  </TableRow>
                )}
              </TableBody>
            </Table>
          </Box>
        </Stack>
      </Box>
    </Box>
  )
}

function AclModeSwitch() {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const enforced = useAppSelector((s) => s.cameraPermissions.aclEnforced)
  const [confirming, setConfirming] = useState(false)

  const apply = async (next: boolean) => {
    const result = await dispatch(setAclMode(next))
    if (!setAclMode.rejected.match(result)) await dispatch(loadAclMode())
  }

  return (
    <Box sx={{ px: 1 }}>
      <FormControlLabel
        control={(
          <Switch
            checked={enforced}
            // Turning it on can lock everyone out at once, so it asks first; turning it off only
            // widens access and needs no ceremony.
            onChange={(e) => (e.target.checked ? setConfirming(true) : void apply(false))}
          />
        )}
        label={t('permissions.enforce')}
      />
      <Typography variant="caption" color="text.secondary" display="block">
        {enforced ? t('permissions.enforceOnHelper') : t('permissions.enforceOffHelper')}
      </Typography>

      <Dialog open={confirming} onClose={() => setConfirming(false)}>
        <DialogTitle>{t('permissions.enforceConfirm')}</DialogTitle>
        <DialogContent>
          <DialogContentText>{t('permissions.enforceOnWarning')}</DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setConfirming(false)}>{t('common.cancel')}</Button>
          <Button
            color="warning"
            variant="contained"
            onClick={() => { setConfirming(false); void apply(true) }}
          >
            {t('common.apply')}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  )
}

export default function CameraPermissionsPanel({ ref }: { ref?: React.Ref<ApplyIfDirtyHandle> }) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const { usernames, byUser } = useAppSelector((s) => s.cameraPermissions)

  const users = useMemo(() => usernames.map((username) => ({ username })), [usernames])
  const { selected, detailRef, detailKey, select } =
    useMasterDetailSelection(ref, users, (u) => u.username)

  useEffect(() => {
    void dispatch(loadPermissionUsers())
    void dispatch(loadAllPermissions())
    void dispatch(loadAclMode())
    // The grid needs every camera, and this panel can be the first one opened in a session.
    void dispatch(loadCameras())
  }, [dispatch])

  const summaryFor = (username: string) => {
    const grants = byUser[username]
    if (hasAny(grants?.allCameras)) return t('permissions.allCameras')
    const count = Object.keys(grants?.cameras ?? {}).length
    return count > 0 ? t('permissions.nCameras', { count }) : t('permissions.noGrants')
  }

  const list = (
    <List dense disablePadding>
      {users.map((u) => (
        <SettingsListItem
          key={u.username}
          selected={selected?.username === u.username}
          onClick={() => select(u.username)}
          primary={u.username}
          secondary={summaryFor(u.username)}
        />
      ))}
      {users.length === 0 && (
        <Typography variant="body2" color="text.secondary" sx={{ p: 1.5 }}>
          {t('permissions.noUsers')}
        </Typography>
      )}
    </List>
  )

  return (
    <MasterDetailLayout
      listTitle={t('permissions.users')}
      list={list}
      listWidth={240}
      footer={<AclModeSwitch />}
    >
      {selected ? (
        <CameraPermissionsForm ref={detailRef} key={detailKey} username={selected.username} />
      ) : (
        <Typography color="text.secondary" sx={{ p: 2 }}>
          {t('permissions.selectHint')}
        </Typography>
      )}
    </MasterDetailLayout>
  )
}
