import { useEffect, useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import {
  Alert,
  Box,
  Card,
  CardContent,
  IconButton,
  Menu,
  MenuItem,
  Stack,
  Tab,
  Tabs,
  ToggleButton,
  ToggleButtonGroup,
  Tooltip,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import EditIcon from '@mui/icons-material/Edit'
import DeleteIcon from '@mui/icons-material/Delete'
import ShareIcon from '@mui/icons-material/Share'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import {
  copyMonitorTo,
  loadMonitors,
  removeMonitor,
  setMonitorCameras,
  setMonitorSlot,
  upsertMonitor,
} from '../store/monitorsSlice'
import type { MonitorKind, MonitorLayout } from '../dto/dtos'
import { MONITOR_LAYOUTS } from '../dto/dtos'
import MonitorGrid from './MonitorGrid'
import MonitorEditDialog from './MonitorEditDialog'
import WallPlaybackPanel from './wall/WallPlaybackPanel'

type Props = {
  readOnly?: boolean
  /**
   * 'wall' is the shared, admin-defined video wall. 'investigation' is the operator's own
   * workspace: boards are created and thrown away by the operator, always sit in playback, and
   * take cameras from the whole system rather than from a pre-assigned pool.
   */
  kind?: MonitorKind
}

export default function MonitorsView({ readOnly = false, kind = 'wall' }: Props) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const allMonitors = useAppSelector((state) => state.monitors.monitors)
  const status = useAppSelector((state) => state.monitors.status)
  const allCameras = useAppSelector((state) => Object.values(state.live.cameraStatesById))
  const [searchParams] = useSearchParams()

  const investigation = kind === 'investigation'
  const monitors = useMemo(() => allMonitors.filter((m) => m.kind === kind), [allMonitors, kind])

  // Seeded from ?monitorId=<id> when arriving via Square's Monitor tab ("Open in vms_rec" link) or
  // from an event — falls back to the usual auto-select-first behavior below if the id doesn't
  // match any monitor.
  const [selectedMonitorId, setSelectedMonitorId] = useState<string | null>(() => searchParams.get('monitorId'))
  const [dialogOpen, setDialogOpen] = useState(false)
  const [editingMonitorId, setEditingMonitorId] = useState<string | null>(null)
  const [addAnchor, setAddAnchor] = useState<HTMLElement | null>(null)
  const [shareAnchor, setShareAnchor] = useState<HTMLElement | null>(null)
  const [users, setUsers] = useState<string[]>([])

  useEffect(() => {
    void dispatch(loadMonitors())
  }, [dispatch])

  // Recipients for "send a copy". Empty without Keycloak — there is nobody to name, and the share
  // control stays hidden.
  useEffect(() => {
    if (!investigation) return
    fetch('/api/auth/users')
      .then((r) => (r.ok ? (r.json() as Promise<{ username: string }[]>) : []))
      .then((list) => setUsers(list.map((u) => u.username)))
      .catch(() => setUsers([]))
  }, [investigation])

  // Auto-select first monitor when list loads or changes
  useEffect(() => {
    if (monitors.length > 0 && (selectedMonitorId === null || !monitors.some((m) => m.monitorId === selectedMonitorId))) {
      setSelectedMonitorId(monitors[0].monitorId)
    } else if (monitors.length === 0) {
      setSelectedMonitorId(null)
    }
  }, [monitors, selectedMonitorId])

  const selectedMonitor = monitors.find((m) => m.monitorId === selectedMonitorId) ?? null
  const editingMonitor = editingMonitorId ? (monitors.find((m) => m.monitorId === editingMonitorId) ?? null) : null

  const handleOpenCreate = () => {
    setEditingMonitorId(null)
    setDialogOpen(true)
  }

  const handleOpenEdit = (monitorId: string) => {
    setEditingMonitorId(monitorId)
    setDialogOpen(true)
  }

  const handleDialogSave = async (name: string, layout: MonitorLayout, cameraIds: string[]) => {
    setDialogOpen(false)
    const existing = editingMonitorId ? monitors.find((m) => m.monitorId === editingMonitorId) : null
    const result = await dispatch(
      upsertMonitor({
        monitorId: existing?.monitorId ?? '',
        name,
        layout,
        cameraIds: existing?.cameraIds ?? [],
        slots: existing?.slots ?? [],
        kind,
        owner: '',
      })
    )

    if (upsertMonitor.fulfilled.match(result)) {
      const savedId = result.payload.monitorId
      setSelectedMonitorId(savedId)
      if (cameraIds.join(',') !== (existing?.cameraIds ?? []).join(',')) {
        await dispatch(setMonitorCameras({ monitorId: savedId, cameraIds }))
      }
    }
  }

  const handleDelete = async (monitorId: string) => {
    await dispatch(removeMonitor(monitorId))
  }

  // Investigation boards are never configured up front: the board is created by the act of putting
  // the first camera on it, so there is no empty-board state to explain or clean up.
  const handleCreateBoard = async (cameraId: string) => {
    setAddAnchor(null)
    const used = new Set(monitors.map((m) => m.name))
    let index = 1
    while (used.has(t('monitors.boardName', { index }))) index += 1
    const name = t('monitors.boardName', { index })

    const result = await dispatch(
      upsertMonitor({ monitorId: '', name, layout: '1x1', cameraIds: [], slots: [], kind, owner: '' })
    )
    if (!upsertMonitor.fulfilled.match(result)) return

    const boardId = result.payload.monitorId
    setSelectedMonitorId(boardId)
    await dispatch(setMonitorCameras({ monitorId: boardId, cameraIds: [cameraId] }))
    await dispatch(setMonitorSlot({ monitorId: boardId, slotIndex: 0, cameraId }))
  }

  const handleLayoutChange = async (layout: MonitorLayout) => {
    if (!selectedMonitor) return
    await dispatch(upsertMonitor({ ...selectedMonitor, layout }))
  }

  const handleShare = async (username: string) => {
    setShareAnchor(null)
    if (!selectedMonitor) return
    await dispatch(copyMonitorTo({ monitorId: selectedMonitor.monitorId, username }))
  }

  return (
    <Stack spacing={2} sx={{ flex: 1, minHeight: 0 }}>
      <Card sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
        <CardContent
          sx={{
            flex: 1,
            minHeight: 0,
            display: 'flex',
            flexDirection: 'column',
            overflow: 'hidden',
            pt: 1,
            '&:last-child': { pb: 2 },
          }}
        >
          {/* Monitor tabs bar */}
          <Box sx={{ display: 'flex', alignItems: 'center', borderBottom: 1, borderColor: 'divider', mb: 2, flexShrink: 0 }}>
            <Tabs
              value={selectedMonitorId ?? false}
              onChange={(_, v: string) => setSelectedMonitorId(v)}
              variant="scrollable"
              scrollButtons="auto"
              sx={{ flex: 1, minWidth: 0 }}
            >
              {monitors.map((m) => (
                <Tab key={m.monitorId} value={m.monitorId} label={m.name} />
              ))}
            </Tabs>

            {/* Boards belong to the operator, so creating and deleting them is never read-only. */}
            {(!readOnly || investigation) && (
              <Tooltip title={investigation ? t('monitors.newBoard') : t('monitors.newMonitor')}>
                <IconButton
                  size="small"
                  onClick={(e) => (investigation ? setAddAnchor(e.currentTarget) : handleOpenCreate())}
                  sx={{ mx: 1 }}
                >
                  <AddIcon />
                </IconButton>
              </Tooltip>
            )}

            {investigation && selectedMonitor && (
              <>
                <ToggleButtonGroup
                  size="small"
                  exclusive
                  value={selectedMonitor.layout}
                  onChange={(_, v: MonitorLayout | null) => v && void handleLayoutChange(v)}
                  sx={{ mr: 1, '& .MuiToggleButton-root': { px: 0.75, py: 0.25, fontSize: '0.7rem' } }}
                >
                  {MONITOR_LAYOUTS.map((l) => (
                    <ToggleButton key={l} value={l}>{l}</ToggleButton>
                  ))}
                </ToggleButtonGroup>

                {users.length > 0 && (
                  <Tooltip title={t('monitors.shareBoard')}>
                    <IconButton size="small" onClick={(e) => setShareAnchor(e.currentTarget)}>
                      <ShareIcon fontSize="small" />
                    </IconButton>
                  </Tooltip>
                )}
              </>
            )}

            {!readOnly && !investigation && selectedMonitor && (
              <Tooltip title={t('monitors.editMonitor')}>
                <IconButton size="small" onClick={() => handleOpenEdit(selectedMonitor.monitorId)}>
                  <EditIcon fontSize="small" />
                </IconButton>
              </Tooltip>
            )}

            {(!readOnly || investigation) && selectedMonitor && (
              <Tooltip title={investigation ? t('monitors.deleteBoard') : t('monitors.deleteMonitor')}>
                <IconButton size="small" onClick={() => void handleDelete(selectedMonitor.monitorId)} color="error">
                  <DeleteIcon fontSize="small" />
                </IconButton>
              </Tooltip>
            )}
          </Box>

          {/* Monitor grid */}
          {selectedMonitor ? (
            <>
              <MonitorGrid monitor={selectedMonitor} openCameraPool={investigation} />
              <WallPlaybackPanel monitor={selectedMonitor} fixedPlayback={investigation} />
            </>
          ) : (
            <Box sx={{ flex: 1, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
              <Stack alignItems="center" spacing={1}>
                <Typography color="text.secondary">
                  {investigation ? t('monitors.noBoards') : t('monitors.noMonitors')}
                </Typography>
                {investigation ? (
                  <Typography variant="body2" color="text.disabled">{t('monitors.boardHint')}</Typography>
                ) : (
                  !readOnly && (
                    <Typography variant="body2" color="text.disabled">{t('monitors.createHint')}</Typography>
                  )
                )}
              </Stack>
            </Box>
          )}
        </CardContent>
      </Card>

      {status !== 'Ready' && (
        <Alert severity={status.startsWith('Error') ? 'error' : 'info'} sx={{ flexShrink: 0 }}>
          {status}
        </Alert>
      )}

      {/* Picking a camera here is what creates the board */}
      <Menu anchorEl={addAnchor} open={Boolean(addAnchor)} onClose={() => setAddAnchor(null)}>
        {allCameras.map((c) => (
          <MenuItem key={c.cameraId} onClick={() => void handleCreateBoard(c.cameraId)}>
            {c.name || c.cameraId}
          </MenuItem>
        ))}
      </Menu>

      <Menu anchorEl={shareAnchor} open={Boolean(shareAnchor)} onClose={() => setShareAnchor(null)}>
        {users.map((u) => (
          <MenuItem key={u} onClick={() => void handleShare(u)}>{u}</MenuItem>
        ))}
      </Menu>

      {!readOnly && !investigation && (
        <MonitorEditDialog
          open={dialogOpen}
          monitor={editingMonitor}
          allCameras={allCameras}
          onSave={(name, layout, cameraIds) => void handleDialogSave(name, layout, cameraIds)}
          onClose={() => setDialogOpen(false)}
        />
      )}
    </Stack>
  )
}
