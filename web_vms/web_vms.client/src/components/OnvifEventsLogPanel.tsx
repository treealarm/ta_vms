import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Box,
  Button,
  FormControlLabel,
  IconButton,
  MenuItem,
  Stack,
  Switch,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import ContentCopyIcon from '@mui/icons-material/ContentCopy'
import { useTranslation } from 'react-i18next'
import { useAppSelector } from '../store'
import type { OnvifEventRecordDto } from '../dto/dtos'
import { copyToClipboard } from '../utils/copyToClipboard'

function formatXml(xml: string): string {
  try {
    const withBreaks = xml.replace(/(>)(<)(\/*)/g, '$1\n$2$3')
    let pad = 0
    return withBreaks
      .split('\n')
      .map((node) => {
        let indent = 0
        if (/^<\/\w/.test(node)) {
          pad = Math.max(pad - 1, 0)
        } else if (/^<\w[^>]*[^/]>.*$/.test(node) && !/<\/\w/.test(node)) {
          indent = 1
        }
        const line = '  '.repeat(pad) + node
        pad += indent
        return line
      })
      .join('\n')
      .trim()
  } catch {
    return xml
  }
}

function fmtTime(iso: string) {
  try {
    return new Date(iso).toLocaleTimeString(undefined, {
      hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false,
    })
  } catch { return iso }
}

export default function OnvifEventsLogPanel() {
  const { t } = useTranslation()
  const allCameras = useAppSelector((s) => Object.values(s.live.cameraStatesById))
  const cameraStatesById = useAppSelector((s) => s.live.cameraStatesById)

  const [events, setEvents] = useState<OnvifEventRecordDto[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [paused, setPaused] = useState(false)
  const [selected, setSelected] = useState<OnvifEventRecordDto | null>(null)
  const [copied, setCopied] = useState(false)
  const [cameraFilter, setCameraFilter] = useState('')
  const [textFilter, setTextFilter] = useState('')
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const resp = await fetch('/api/onvif/recent-events')
      if (!resp.ok) throw new Error(await resp.text())
      setEvents(await resp.json() as OnvifEventRecordDto[])
    } catch (e) {
      setError(String(e))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { void load() }, [load])

  useEffect(() => {
    if (paused) {
      if (intervalRef.current) clearInterval(intervalRef.current)
    } else {
      intervalRef.current = setInterval(() => void load(), 5000)
    }
    return () => { if (intervalRef.current) clearInterval(intervalRef.current) }
  }, [paused, load])

  const filtered = useMemo(() => {
    const lc = textFilter.toLowerCase()
    return events.filter((e) => {
      if (cameraFilter && e.cameraId !== cameraFilter) return false
      if (lc && !e.topic.toLowerCase().includes(lc) && !e.payloadXml.toLowerCase().includes(lc)) return false
      return true
    })
  }, [events, cameraFilter, textFilter])

  const subscribedCameraIds = useMemo(
    () => [...new Set(events.map((e) => e.cameraId).filter(Boolean) as string[])].sort(),
    [events],
  )

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', overflow: 'hidden' }}>
      {/* Controls */}
      <Stack direction="row" spacing={1} alignItems="center" sx={{ px: 1, py: 0.5, flexShrink: 0 }} flexWrap="wrap">
        <TextField
          select
          size="small"
          value={cameraFilter}
          onChange={(e) => setCameraFilter(e.target.value)}
          sx={{ minWidth: 130 }}
          SelectProps={{ displayEmpty: true }}
        >
          <MenuItem value="">{t('onvifFilters.allCameras')}</MenuItem>
          {subscribedCameraIds.map((id) => (
            <MenuItem key={id} value={id}>{cameraStatesById[id]?.name || id}</MenuItem>
          ))}
        </TextField>

        <TextField
          size="small"
          placeholder={t('onvifFilters.filterHint')}
          value={textFilter}
          onChange={(e) => setTextFilter(e.target.value)}
          sx={{ minWidth: 160 }}
        />

        <FormControlLabel
          control={
            <Switch
              size="small"
              checked={!paused}
              onChange={(e) => setPaused(!e.target.checked)}
            />
          }
          label={<Typography variant="body2">{t('onvifFilters.autoRefresh')}</Typography>}
        />

        <Button size="small" variant="outlined" onClick={() => void load()} disabled={loading}>
          {t('onvifFilters.debugRefresh')}
        </Button>

        {loading && (
          <Typography variant="body2" color="text.secondary">{t('common.loading')}</Typography>
        )}
      </Stack>

      {error && (
        <Typography color="error" variant="caption" sx={{ px: 1 }}>{error}</Typography>
      )}

      {/* Two-column body */}
      <Box sx={{ flex: 1, minHeight: 0, display: 'flex', overflow: 'hidden' }}>
        {/* Left: event list */}
        <Box
          sx={{
            width: 300,
            flexShrink: 0,
            borderRight: 1,
            borderColor: 'divider',
            overflowY: 'auto',
          }}
        >
          {filtered.length === 0 && !loading && (
            <Typography variant="caption" color="text.secondary" sx={{ p: 1, display: 'block' }}>
              {t('onvifFilters.debugNoEvents')}
            </Typography>
          )}
          {filtered.map((ev, i) => (
            <Box
              key={i}
              onClick={() => setSelected(ev)}
              sx={{
                px: 1,
                py: 0.5,
                cursor: 'pointer',
                borderBottom: 1,
                borderColor: 'divider',
                bgcolor: selected === ev ? 'action.selected' : 'transparent',
                '&:hover': { bgcolor: 'action.hover' },
              }}
            >
              <Stack direction="row" spacing={1} alignItems="baseline">
                <Typography variant="caption" color="text.secondary" sx={{ whiteSpace: 'nowrap' }}>
                  {fmtTime(ev.receivedAt)}
                </Typography>
                {ev.cameraId && (
                  <Typography variant="caption" color="primary.main" sx={{ whiteSpace: 'nowrap', fontWeight: 600 }}>
                    {cameraStatesById[ev.cameraId]?.name || ev.cameraId}
                  </Typography>
                )}
              </Stack>
              <Typography
                variant="caption"
                sx={{ display: 'block', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}
              >
                {ev.topic || '—'}
              </Typography>
            </Box>
          ))}
        </Box>

        {/* Right: detail */}
        <Box sx={{ flex: 1, overflowY: 'auto', p: 1 }}>
          {selected ? (
            <Stack spacing={0.5}>
              {selected.cameraId && (
                <Typography variant="body2">
                  <strong>{t('cameras.cameraName')}:</strong> {cameraStatesById[selected.cameraId]?.name || selected.cameraId}
                </Typography>
              )}
              <Typography variant="body2">
                <strong>{t('onvifFilters.debugTime')}:</strong> {selected.receivedAt}
              </Typography>
              <Typography variant="body2" sx={{ wordBreak: 'break-all' }}>
                <strong>{t('onvifFilters.debugTopic')}:</strong> {selected.topic || '—'}
              </Typography>
              <Stack direction="row" alignItems="center" spacing={0.5} sx={{ mt: 0.5 }}>
                <Typography variant="caption" color="text.secondary">
                  {t('onvifFilters.debugPayload')}
                </Typography>
                <Tooltip title={copied ? t('cameraCard.copiedRtsp') : t('cameraCard.copyRtsp')}>
                  <IconButton
                    size="small"
                    onClick={() => {
                      void copyToClipboard(formatXml(selected.payloadXml)).then((ok) => {
                        if (!ok) return
                        setCopied(true)
                        setTimeout(() => setCopied(false), 1500)
                      })
                    }}
                  >
                    <ContentCopyIcon sx={{ fontSize: '0.85rem' }} />
                  </IconButton>
                </Tooltip>
              </Stack>
              <Box
                component="pre"
                sx={{
                  fontSize: '0.68rem',
                  m: 0,
                  whiteSpace: 'pre-wrap',
                  wordBreak: 'break-all',
                  bgcolor: 'action.hover',
                  p: 1,
                  borderRadius: 0.5,
                  overflowX: 'auto',
                }}
              >
                {formatXml(selected.payloadXml) || '—'}
              </Box>
            </Stack>
          ) : (
            <Typography variant="caption" color="text.secondary">
              {allCameras.length > 0 ? t('logs.selectHint') : ''}
            </Typography>
          )}
        </Box>
      </Box>
    </Box>
  )
}
