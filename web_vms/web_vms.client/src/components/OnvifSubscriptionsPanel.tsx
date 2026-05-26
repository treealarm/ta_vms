import { useCallback, useEffect, useState } from 'react'
import {
  Box,
  Button,
  Chip,
  CircularProgress,
  Collapse,
  IconButton,
  Stack,
  Tab,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Tabs,
  Typography,
} from '@mui/material'
import KeyboardArrowDownIcon from '@mui/icons-material/KeyboardArrowDown'
import KeyboardArrowUpIcon from '@mui/icons-material/KeyboardArrowUp'
import { useTranslation } from 'react-i18next'
import { useAppSelector } from '../store'
import type { CameraSetupDto, OnvifEventRecordDto } from '../dto/dtos'
import OnvifAlarmFiltersPanel from './OnvifAlarmFiltersPanel'

async function fetchSubscriptions(): Promise<string[]> {
  const r = await fetch('/api/onvif/subscriptions')
  return r.ok ? (r.json() as Promise<string[]>) : []
}

async function subscribe(cameraId: string) {
  await fetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/subscription`, { method: 'POST' })
}

async function unsubscribe(cameraId: string) {
  await fetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/subscription`, { method: 'DELETE' })
}

async function fetchRecentEvents(cameraId: string): Promise<OnvifEventRecordDto[]> {
  const r = await fetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/recent-events`)
  return r.ok ? (r.json() as Promise<OnvifEventRecordDto[]>) : []
}

function CameraRow({ camera, subscribed, onToggle }: {
  camera: CameraSetupDto
  subscribed: boolean
  onToggle: () => void
}) {
  const { t } = useTranslation()
  const [expanded, setExpanded] = useState(false)
  const [subTab, setSubTab] = useState(0)
  const [events, setEvents] = useState<OnvifEventRecordDto[]>([])
  const [loadingEvents, setLoadingEvents] = useState(false)

  const loadEvents = useCallback(async () => {
    setLoadingEvents(true)
    try { setEvents(await fetchRecentEvents(camera.cameraId)) }
    finally { setLoadingEvents(false) }
  }, [camera.cameraId])

  useEffect(() => {
    if (!expanded || !subscribed || subTab !== 1) return
    void loadEvents()
  }, [expanded, subscribed, subTab, loadEvents])

  const hasOnvif = !!camera.onvifConn?.ip

  return (
    <>
      <TableRow sx={{ '& td': { borderBottom: expanded ? 0 : undefined } }}>
        <TableCell sx={{ width: 32, pr: 0 }}>
          {subscribed && (
            <IconButton size="small" onClick={() => setExpanded((v) => !v)}>
              {expanded ? <KeyboardArrowUpIcon fontSize="small" /> : <KeyboardArrowDownIcon fontSize="small" />}
            </IconButton>
          )}
        </TableCell>
        <TableCell>
          <Typography variant="body2" fontWeight={600}>{camera.cameraId}</Typography>
        </TableCell>
        <TableCell>
          <Typography variant="body2" color="text.secondary">
            {camera.onvifConn ? `${camera.onvifConn.ip}:${camera.onvifConn.port}` : '—'}
          </Typography>
        </TableCell>
        <TableCell>
          {subscribed
            ? <Chip label="Subscribed" size="small" color="success" variant="outlined" />
            : <Chip label="Not subscribed" size="small" variant="outlined" />}
        </TableCell>
        <TableCell align="right">
          <Button
            size="small"
            variant={subscribed ? 'outlined' : 'contained'}
            color={subscribed ? 'error' : 'primary'}
            disabled={!hasOnvif && !subscribed}
            onClick={onToggle}
          >
            {subscribed ? 'Unsubscribe' : 'Subscribe'}
          </Button>
        </TableCell>
      </TableRow>

      {subscribed && (
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

                {subTab === 0 && (
                  <OnvifAlarmFiltersPanel cameraId={camera.cameraId} />
                )}

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
      )}
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
  const cameras = useAppSelector((s) => Object.values(s.live.cameraStatesById))
  const [subscribed, setSubscribed] = useState<Set<string>>(new Set())
  const [loading, setLoading] = useState(true)

  const reload = useCallback(async () => {
    setLoading(true)
    try {
      const ids = await fetchSubscriptions()
      setSubscribed(new Set(ids))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { void reload() }, [reload])

  const toggle = async (camera: CameraSetupDto) => {
    if (subscribed.has(camera.cameraId)) {
      await unsubscribe(camera.cameraId)
      setSubscribed((s) => { const n = new Set(s); n.delete(camera.cameraId); return n })
    } else {
      await subscribe(camera.cameraId)
      setSubscribed((s) => new Set(s).add(camera.cameraId))
    }
  }

  const onvifCameras = cameras.filter((c) => c.onvifConn?.ip)
  const otherCameras = cameras.filter((c) => !c.onvifConn?.ip)
  const sorted = [...onvifCameras, ...otherCameras]

  if (loading) return <CircularProgress sx={{ m: 4 }} />

  return (
    <Box>
      <Typography variant="h6" gutterBottom>ONVIF event subscriptions</Typography>
      <Typography variant="body2" color="text.secondary" mb={2}>
        Subscribed cameras receive ONVIF events. Alarm filters trigger retention recordings.
      </Typography>
      <Table size="small">
        <TableHead>
          <TableRow>
            <TableCell sx={{ width: 32 }} />
            <TableCell>Camera</TableCell>
            <TableCell>ONVIF address</TableCell>
            <TableCell>Status</TableCell>
            <TableCell />
          </TableRow>
        </TableHead>
        <TableBody>
          {sorted.map((cam) => (
            <CameraRow
              key={cam.cameraId}
              camera={cam}
              subscribed={subscribed.has(cam.cameraId)}
              onToggle={() => void toggle(cam)}
            />
          ))}
        </TableBody>
      </Table>
    </Box>
  )
}
