import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Box,
  Button,
  Chip,
  FormControlLabel,
  Stack,
  Switch,
  Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppSelector } from '../store'
import type { AnalyticsActivityEntryDto } from '../dto/dtos'

// Debug feed of recent analytics detections (in-memory ring buffer in VmsAnalytics, mirrors
// FsActivityPanel): bbox + confidence per event, plus the detection JPEG when the watch has
// "debug crops" enabled. Nothing here is persisted.
export default function AnalyticsActivityPanel() {
  const { t } = useTranslation()
  const camerasMap = useAppSelector((s) => s.live.cameraStatesById)

  const [events, setEvents] = useState<AnalyticsActivityEntryDto[]>([])
  const [error, setError] = useState<string | null>(null)
  const [autoRefresh, setAutoRefresh] = useState(true)
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null)

  const fetchEvents = useCallback(async () => {
    try {
      const r = await fetch('/api/analytics-watches/activity?limit=100')
      if (!r.ok) throw new Error(`HTTP ${r.status}`)
      setEvents(await r.json() as AnalyticsActivityEntryDto[])
      setError(null)
    } catch (e) {
      setError(String(e))
    }
  }, [])

  useEffect(() => { void fetchEvents() }, [fetchEvents])

  useEffect(() => {
    if (autoRefresh) {
      timerRef.current = setInterval(() => void fetchEvents(), 3000)
    }
    return () => {
      if (timerRef.current) clearInterval(timerRef.current)
      timerRef.current = null
    }
  }, [autoRefresh, fetchEvents])

  const cameraName = (cameraId: string) => camerasMap[cameraId]?.name || cameraId
  const fmtBbox = (e: AnalyticsActivityEntryDto) =>
    `${e.bboxX.toFixed(2)},${e.bboxY.toFixed(2)} ${e.bboxWidth.toFixed(2)}×${e.bboxHeight.toFixed(2)}`

  return (
    <Box sx={{ height: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
      <Stack direction="row" spacing={2} alignItems="center" sx={{ px: 1, py: 0.5, borderBottom: 1, borderColor: 'divider' }}>
        <FormControlLabel
          control={<Switch size="small" checked={autoRefresh} onChange={(_, v) => setAutoRefresh(v)} />}
          label={<Typography variant="caption">{t('analyticsActivity.autoRefresh')}</Typography>}
        />
        <Button size="small" onClick={() => void fetchEvents()}>
          {t('common.refresh')}
        </Button>
        {error && <Typography variant="caption" color="error">{error}</Typography>}
      </Stack>

      <Box sx={{ flex: 1, overflowY: 'auto', px: 1 }}>
        {events.length === 0 && (
          <Typography variant="body2" color="text.secondary" sx={{ p: 1 }}>
            {t('analyticsActivity.noEvents')}
          </Typography>
        )}
        {events.map((e, i) => (
          <Stack
            key={`${e.cameraId}-${e.trackId}-${e.detectedAt}-${i}`}
            direction="row"
            spacing={1}
            alignItems="center"
            sx={{ py: 0.5, borderBottom: 1, borderColor: 'divider', opacity: e.persisted ? 1 : 0.55 }}
          >
            {e.cropJpegBase64 ? (
              <Box
                component="img"
                src={`data:image/jpeg;base64,${e.cropJpegBase64}`}
                alt={e.kind}
                sx={{ height: 56, maxWidth: 96, objectFit: 'contain', borderRadius: 0.5, flexShrink: 0 }}
              />
            ) : (
              <Box sx={{ height: 56, width: 56, flexShrink: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', bgcolor: 'action.hover', borderRadius: 0.5 }}>
                <Typography variant="caption" color="text.disabled">—</Typography>
              </Box>
            )}
            <Stack sx={{ minWidth: 0, flex: 1 }}>
              <Stack direction="row" spacing={0.5} alignItems="center" flexWrap="wrap">
                <Chip label={t(`analytics.class.${e.kind}`, e.kind)} size="small" />
                <Typography variant="body2" noWrap>{cameraName(e.cameraId)}</Typography>
                <Typography variant="caption" color="text.secondary">
                  {(e.confidence * 100).toFixed(0)}% · track {e.trackId}
                </Typography>
                {e.plateText && <Chip label={e.plateText} size="small" color="primary" variant="outlined" />}
                {!e.persisted && (
                  <Chip label={t('analyticsActivity.debounced')} size="small" variant="outlined" />
                )}
              </Stack>
              <Typography variant="caption" color="text.secondary" noWrap>
                {new Date(e.detectedAt).toLocaleTimeString()} · bbox {fmtBbox(e)}
              </Typography>
            </Stack>
          </Stack>
        ))}
      </Box>
    </Box>
  )
}
