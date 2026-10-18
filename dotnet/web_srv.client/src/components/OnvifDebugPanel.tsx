import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Box,
  Button,
  Collapse,
  FormControlLabel,
  Stack,
  Switch,
  Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import type { OnvifEventRecordDto } from '../dto/dtos'

type Props = { cameraId: string }

export default function OnvifDebugPanel({ cameraId }: Props) {
  const { t } = useTranslation()
  const [events, setEvents] = useState<OnvifEventRecordDto[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [autoRefresh, setAutoRefresh] = useState(false)
  const [expandedIndex, setExpandedIndex] = useState<number | null>(null)
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const resp = await fetch(
        `/api/onvif/cameras/${encodeURIComponent(cameraId)}/recent-events`,
      )
      if (!resp.ok) throw new Error(await resp.text())
      setEvents(await resp.json())
    } catch (e) {
      setError(String(e))
    } finally {
      setLoading(false)
    }
  }, [cameraId])

  useEffect(() => {
    void load()
  }, [load])

  useEffect(() => {
    if (autoRefresh) {
      intervalRef.current = setInterval(() => void load(), 5000)
    } else {
      if (intervalRef.current) clearInterval(intervalRef.current)
    }
    return () => { if (intervalRef.current) clearInterval(intervalRef.current) }
  }, [autoRefresh, load])

  const formatTime = (iso: string) => {
    try { return new Date(iso).toLocaleTimeString() } catch { return iso }
  }

  return (
    <Stack spacing={1} sx={{ pt: 1 }}>
      <Stack direction="row" spacing={2} alignItems="center">
        <Button variant="outlined" size="small" onClick={() => void load()} disabled={loading}>
          {t('onvifFilters.debugRefresh') ?? t('common.refresh')}
        </Button>
        <FormControlLabel
          control={
            <Switch
              size="small"
              checked={autoRefresh}
              onChange={(e) => setAutoRefresh(e.target.checked)}
            />
          }
          label={<Typography variant="body2">{t('onvifFilters.autoRefresh')}</Typography>}
        />
        {loading && (
          <Typography variant="body2" color="text.secondary">{t('common.loading')}</Typography>
        )}
      </Stack>

      {error && (
        <Typography color="error" variant="body2">{error}</Typography>
      )}

      {!loading && events.length === 0 && !error && (
        <Typography variant="body2" color="text.secondary">
          {t('onvifFilters.debugNoEvents')}
        </Typography>
      )}

      {events.map((ev, i) => (
        <Box
          key={i}
          sx={{
            border: 1,
            borderColor: 'divider',
            borderRadius: 1,
            overflow: 'hidden',
          }}
        >
          <Stack
            direction="row"
            spacing={2}
            alignItems="center"
            sx={{
              px: 1.5,
              py: 0.75,
              cursor: 'pointer',
              '&:hover': { bgcolor: 'action.hover' },
            }}
            onClick={() => setExpandedIndex(expandedIndex === i ? null : i)}
          >
            <Typography variant="caption" color="text.secondary" sx={{ whiteSpace: 'nowrap' }}>
              {formatTime(ev.receivedAt)}
            </Typography>
            <Typography variant="body2" sx={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {ev.topic || '—'}
            </Typography>
          </Stack>
          <Collapse in={expandedIndex === i}>
            <Box sx={{ px: 1.5, pb: 1 }}>
              <Typography variant="caption" color="text.secondary" display="block" gutterBottom>
                {t('onvifFilters.debugPayload')}
              </Typography>
              <Box
                component="pre"
                sx={{
                  fontSize: '0.7rem',
                  overflowX: 'auto',
                  whiteSpace: 'pre-wrap',
                  wordBreak: 'break-all',
                  bgcolor: 'action.hover',
                  p: 1,
                  borderRadius: 0.5,
                  m: 0,
                }}
              >
                {ev.payloadXml || '—'}
              </Box>
            </Box>
          </Collapse>
        </Box>
      ))}
    </Stack>
  )
}
