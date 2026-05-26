import { useCallback, useEffect, useState } from 'react'
import {
  Box,
  Button,
  CircularProgress,
  Collapse,
  Divider,
  FormControlLabel,
  IconButton,
  MenuItem,
  Stack,
  Switch,
  Tab,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Tabs,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import KeyboardArrowDownIcon from '@mui/icons-material/KeyboardArrowDown'
import KeyboardArrowUpIcon from '@mui/icons-material/KeyboardArrowUp'
import DeleteIcon from '@mui/icons-material/Delete'
import { useTranslation } from 'react-i18next'
import { useAppSelector } from '../store'
import type { OnvifCameraEntryDto, OnvifEventRecordDto } from '../dto/dtos'
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

async function setArmed(cameraId: string, armed: boolean) {
  await fetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/subscription`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ armed }),
  })
}

async function fetchRecentEvents(cameraId: string): Promise<OnvifEventRecordDto[]> {
  const r = await fetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/recent-events`)
  return r.ok ? (r.json() as Promise<OnvifEventRecordDto[]>) : []
}

function CameraRow({ entry, onRemove, onToggleArmed }: {
  entry: OnvifCameraEntryDto
  onRemove: () => void
  onToggleArmed: (armed: boolean) => void
}) {
  const { t } = useTranslation()
  const allCameras = useAppSelector((s) => s.live.cameraStatesById)
  const cam = allCameras[entry.cameraId]

  const [expanded, setExpanded] = useState(false)
  const [subTab, setSubTab] = useState(0)
  const [events, setEvents] = useState<OnvifEventRecordDto[]>([])
  const [loadingEvents, setLoadingEvents] = useState(false)

  const loadEvents = useCallback(async () => {
    setLoadingEvents(true)
    try { setEvents(await fetchRecentEvents(entry.cameraId)) }
    finally { setLoadingEvents(false) }
  }, [entry.cameraId])

  useEffect(() => {
    if (!expanded || subTab !== 1) return
    void loadEvents()
  }, [expanded, subTab, loadEvents])

  const onvifAddr = cam?.onvifConn ? `${cam.onvifConn.ip}:${cam.onvifConn.port}` : '—'

  return (
    <>
      <TableRow sx={{ '& td': { borderBottom: expanded ? 0 : undefined } }}>
        <TableCell sx={{ width: 32, pr: 0 }}>
          <IconButton size="small" onClick={() => setExpanded((v) => !v)}>
            {expanded ? <KeyboardArrowUpIcon fontSize="small" /> : <KeyboardArrowDownIcon fontSize="small" />}
          </IconButton>
        </TableCell>
        <TableCell>
          <Typography variant="body2" fontWeight={600}>{entry.cameraId}</Typography>
        </TableCell>
        <TableCell>
          <Typography variant="body2" color="text.secondary">{onvifAddr}</Typography>
        </TableCell>
        <TableCell>
          <FormControlLabel
            control={
              <Switch
                size="small"
                checked={entry.subscribed}
                onChange={(e) => onToggleArmed(e.target.checked)}
              />
            }
            label={
              <Typography variant="body2" color={entry.subscribed ? 'success.main' : 'text.secondary'}>
                {entry.subscribed ? 'Armed' : 'Disarmed'}
              </Typography>
            }
          />
        </TableCell>
        <TableCell align="right">
          <Tooltip title={t('common.delete')}>
            <IconButton size="small" color="error" onClick={onRemove}>
              <DeleteIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        </TableCell>
      </TableRow>

      <TableRow>
        <TableCell colSpan={5} sx={{ py: 0 }}>
          <Collapse in={expanded} unmountOnExit>
            <Box sx={{ px: 1, pt: 0.5, pb: 1.5 }}>
              <Tabs
                value={subTab}
                onChange={(_, v: number) => setSubTab(v)}
                sx={{ mb: 1.5, borderBottom: 1, borderColor: 'divider', minHeight: 36 }}
                TabIndicatorProps={{ style: { height: 2 } }}
              >
                <Tab label={t('onvifFilters.tabFilters')} sx={{ minHeight: 36, py: 0.5, fontSize: '0.75rem' }} />
                <Tab label={t('onvifFilters.tabDebug')} sx={{ minHeight: 36, py: 0.5, fontSize: '0.75rem' }} />
              </Tabs>

              {subTab === 0 && <OnvifAlarmFiltersPanel cameraId={entry.cameraId} />}

              {subTab === 1 && (
                <>
                  <Stack direction="row" alignItems="center" spacing={1} mb={1}>
                    <Button size="small" onClick={() => void loadEvents()} disabled={loadingEvents}>
                      {loadingEvents ? <CircularProgress size={12} /> : t('onvifFilters.debugRefresh')}
                    </Button>
                  </Stack>
                  {events.length === 0
                    ? <Typography variant="caption" color="text.secondary">{t('onvifFilters.debugNoEvents')}</Typography>
                    : (
                      <Table size="small">
                        <TableHead>
                          <TableRow>
                            <TableCell>{t('onvifFilters.debugTime')}</TableCell>
                            <TableCell>{t('onvifFilters.debugTopic')}</TableCell>
                          </TableRow>
                        </TableHead>
                        <TableBody>
                          {events.map((e, i) => (
                            <EventRow key={i} event={e} />
                          ))}
                        </TableBody>
                      </Table>
                    )}
                </>
              )}
            </Box>
          </Collapse>
        </TableCell>
      </TableRow>
    </>
  )
}

function EventRow({ event }: { event: OnvifEventRecordDto }) {
  const [open, setOpen] = useState(false)
  return (
    <>
      <TableRow hover onClick={() => setOpen((v) => !v)} sx={{ cursor: 'pointer' }}>
        <TableCell sx={{ whiteSpace: 'nowrap', fontSize: '0.75rem' }}>
          {new Date(event.receivedAt).toLocaleTimeString()}
        </TableCell>
        <TableCell sx={{ fontSize: '0.75rem' }}>{event.topic}</TableCell>
      </TableRow>
      {open && (
        <TableRow>
          <TableCell colSpan={2} sx={{ py: 0.5 }}>
            <Box component="pre" sx={{ fontSize: '0.65rem', m: 0, whiteSpace: 'pre-wrap', wordBreak: 'break-all' }}>
              {event.payloadXml}
            </Box>
          </TableCell>
        </TableRow>
      )}
    </>
  )
}

export default function OnvifSubscriptionsPanel() {
  const { t } = useTranslation()
  const allCameras = useAppSelector((s) => Object.values(s.live.cameraStatesById))
  const [managed, setManaged] = useState<OnvifCameraEntryDto[]>([])
  const [loading, setLoading] = useState(true)
  const [addId, setAddId] = useState('')

  const reload = useCallback(async () => {
    setLoading(true)
    try { setManaged(await fetchManaged()) }
    finally { setLoading(false) }
  }, [])

  useEffect(() => { void reload() }, [reload])

  const handleAdd = async () => {
    if (!addId) return
    await addCamera(addId)
    setAddId('')
    await reload()
  }

  const handleRemove = async (cameraId: string) => {
    await removeCamera(cameraId)
    setManaged((prev) => prev.filter((e) => e.cameraId !== cameraId))
  }

  const handleToggleArmed = async (cameraId: string, armed: boolean) => {
    setManaged((prev) => prev.map((e) => e.cameraId === cameraId ? { ...e, subscribed: armed } : e))
    await setArmed(cameraId, armed)
  }

  const managedIds = new Set(managed.map((e) => e.cameraId))
  const available = allCameras.filter((c) => c.onvifConn?.ip && !managedIds.has(c.cameraId))

  if (loading) return <CircularProgress sx={{ m: 4 }} />

  return (
    <Box>
      <Typography variant="h6" gutterBottom>{t('nav.onvif')}</Typography>
      <Typography variant="body2" color="text.secondary" mb={2}>
        {t('onvifSubs.description')}
      </Typography>

      <Table size="small">
        <TableHead>
          <TableRow>
            <TableCell sx={{ width: 32 }} />
            <TableCell>{t('cameras.cameraId')}</TableCell>
            <TableCell>{t('cameras.onvifIp')}</TableCell>
            <TableCell>Guard</TableCell>
            <TableCell />
          </TableRow>
        </TableHead>
        <TableBody>
          {managed.map((entry) => (
            <CameraRow
              key={entry.cameraId}
              entry={entry}
              onRemove={() => void handleRemove(entry.cameraId)}
              onToggleArmed={(armed) => void handleToggleArmed(entry.cameraId, armed)}
            />
          ))}
        </TableBody>
      </Table>

      {managed.length === 0 && (
        <Typography variant="body2" color="text.secondary" sx={{ mt: 2, mb: 1 }}>
          {t('onvifSubs.noCameras')}
        </Typography>
      )}

      {available.length > 0 && (
        <>
          <Divider sx={{ my: 2 }} />
          <Stack direction="row" spacing={1} alignItems="center">
            <TextField
              select
              size="small"
              label={t('onvifSubs.addCamera')}
              value={addId}
              onChange={(e) => setAddId(e.target.value)}
              sx={{ minWidth: 200 }}
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
        </>
      )}
    </Box>
  )
}
