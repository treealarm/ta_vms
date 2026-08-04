import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  Divider,
  FormControlLabel,
  LinearProgress,
  List,
  ListItemButton,
  MenuItem,
  Stack,
  Switch,
  TextField,
  Typography,
  alpha
} from '@mui/material'
import { useTranslation } from 'react-i18next'

import type { EpisodeDto, SourceOutageDto } from '../dto/dtos'
import { useAppDispatch, useAppSelector } from '../store'
import { getErrorMessage } from '../store/api'
import { fetchRecordingTimeline, selectWallCoverage, wallCoverageKey } from '../store/wallCoverageSlice'
import { complementSegments, type Segment } from '../utils/archiveRanges'
import { msToX } from '../utils/timelineTicks'
import EventOpenInPlayback from './EventOpenInPlayback'
import WallTimelineTrack from './wall/WallTimelineTrack'

const COLUMN_MAX_HEIGHT = 'min(75vh, 900px)'
const AUTO_REFRESH_MS = 15_000
const TRACK_PX = 640
const TRACK_HEIGHT = 22

const WINDOWS = [
  { key: '24h', ms: 24 * 3600_000 },
  { key: '7d', ms: 7 * 24 * 3600_000 },
  { key: '30d', ms: 30 * 24 * 3600_000 },
] as const

/** Context around the outage, so its edges are visible against the recording on either side. */
const PAD_RATIO = 0.25
const MIN_PAD_MS = 60_000

function gapWindow(gap: SourceOutageDto, nowMs: number): Segment {
  const startMs = Date.parse(gap.startedTs)
  const endMs = gap.endedTs ? Date.parse(gap.endedTs) : nowMs
  return { startMs, endMs: Math.max(endMs, startMs + 1000) }
}

function paddedWindow(win: Segment): Segment {
  const pad = Math.max(MIN_PAD_MS, (win.endMs - win.startMs) * PAD_RATIO)
  return { startMs: win.startMs - pad, endMs: win.endMs + pad }
}

function formatDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms < 0) return '—'
  const s = Math.round(ms / 1000)
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const sec = s % 60
  if (h > 0) return `${h}h ${m}m`
  if (m > 0) return `${m}m ${sec}s`
  return `${sec}s`
}

function stateColor(state: string): 'default' | 'success' | 'warning' | 'error' {
  switch (state) {
    case 'filled': return 'success'
    case 'filling': case 'partial': case 'held': return 'warning'
    case 'exhausted': return 'error'
    default: return 'default'
  }
}

export default function ArchiveGapsTab() {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const camerasMap = useAppSelector((s) => s.live.cameraStatesById)
  const cameraIds = useMemo(() => Object.keys(camerasMap).sort(), [camerasMap])

  const [gaps, setGaps] = useState<SourceOutageDto[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [cameraFilter, setCameraFilter] = useState('')
  const [windowKey, setWindowKey] = useState<typeof WINDOWS[number]['key']>('24h')
  // On by default: the tab exists to explain holes in the archive, and outages while nothing was
  // being recorded are camera history rather than lost footage.
  const [recordingOnly, setRecordingOnly] = useState(true)
  const [autoRefresh, setAutoRefresh] = useState(true)
  const [selectedUid, setSelectedUid] = useState<string | null>(null)
  // Re-rendered every tick would move every open outage's end under the operator's cursor; this is
  // sampled once per load instead.
  const [nowMs, setNowMs] = useState(() => Date.now())
  const selectedRef = useRef<string | null>(null)
  selectedRef.current = selectedUid

  const windowMs = WINDOWS.find((w) => w.key === windowKey)?.ms ?? WINDOWS[0].ms

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const now = Date.now()
      const qs = new URLSearchParams()
      qs.set('from', new Date(now - windowMs).toISOString())
      qs.set('to', new Date(now).toISOString())
      if (cameraFilter) qs.set('cameraId', cameraFilter)
      if (recordingOnly) qs.set('recordingOnly', 'true')
      const response = await fetch(`/api/gaps?${qs.toString()}`)
      if (!response.ok) throw new Error(await getErrorMessage(response))
      const rows = (await response.json()) as SourceOutageDto[]
      setNowMs(now)
      setGaps(rows)
      setSelectedUid((prev) =>
        prev && rows.some((r) => r.outageUid === prev) ? prev : rows[0]?.outageUid ?? null)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
      setGaps([])
    } finally {
      setLoading(false)
    }
  }, [cameraFilter, recordingOnly, windowMs])

  useEffect(() => { void load() }, [load])

  useEffect(() => {
    if (!autoRefresh) return
    const id = window.setInterval(() => { void load() }, AUTO_REFRESH_MS)
    return () => window.clearInterval(id)
  }, [autoRefresh, load])

  const selected = useMemo(
    () => gaps.find((g) => g.outageUid === selectedUid) ?? null,
    [gaps, selectedUid]
  )

  // Coverage around the selected outage: what is actually in the archive there, which is also the
  // only honest answer to "how much of this has been patched" — filledMs is a cache and may lag.
  const coverageQuery = useMemo(() => {
    if (!selected) return null
    const padded = paddedWindow(gapWindow(selected, nowMs))
    return {
      cameraIds: [selected.cameraId],
      streamName: selected.streamName || undefined,
      fromUtc: new Date(padded.startMs).toISOString(),
      toUtc: new Date(padded.endMs).toISOString(),
      minGapMs: Math.max(1000, Math.round((padded.endMs - padded.startMs) / TRACK_PX)),
    }
  }, [selected, nowMs])

  const coverageKey = coverageQuery ? wallCoverageKey(coverageQuery) : ''
  const coverageByCamera = useAppSelector(selectWallCoverage(coverageKey))
  const coverage = useMemo(
    () => (selected && coverageByCamera ? coverageByCamera[selected.cameraId] ?? [] : []),
    [coverageByCamera, selected]
  )

  useEffect(() => {
    if (coverageQuery) void dispatch(fetchRecordingTimeline(coverageQuery))
  }, [dispatch, coverageQuery])

  const missing = useMemo(() => {
    if (!selected) return []
    return complementSegments(coverage, gapWindow(selected, nowMs))
  }, [coverage, selected, nowMs])

  const cameraLabel = (id: string) => camerasMap[id]?.name || id

  const renderGap = (gap: SourceOutageDto) => {
    const win = gapWindow(gap, nowMs)
    const ongoing = !gap.endedTs
    return (
      <ListItemButton
        key={gap.outageUid}
        selected={gap.outageUid === selectedUid}
        onClick={() => setSelectedUid(gap.outageUid)}
        sx={{ display: 'block', py: 0.75 }}
      >
        <Stack direction="row" spacing={0.75} alignItems="center">
          <Typography variant="body2" fontWeight="medium" noWrap sx={{ flex: 1, minWidth: 0 }}>
            {cameraLabel(gap.cameraId)}{gap.streamName ? ` / ${gap.streamName}` : ''}
          </Typography>
          {ongoing && (
            <Chip
              label={t('gaps.ongoing')}
              size="small"
              color="error"
              sx={{ height: 18, '& .MuiChip-label': { px: 0.75, fontSize: '0.65rem' } }}
            />
          )}
          <Chip
            label={t(`gaps.state.${gap.state}`, gap.state)}
            size="small"
            variant="outlined"
            color={stateColor(gap.state)}
            sx={{ height: 18, '& .MuiChip-label': { px: 0.75, fontSize: '0.65rem' } }}
          />
        </Stack>
        <Typography variant="caption" color="text.secondary" component="div">
          {new Date(win.startMs).toLocaleString()} · {formatDuration(win.endMs - win.startMs)}
        </Typography>
        <Typography variant="caption" color="text.secondary" component="div">
          {t(`gaps.cause.${gap.cause}`, gap.cause)}
          {!gap.recordingExpected ? ` · ${t('gaps.notRecorded')}` : ''}
        </Typography>
      </ListItemButton>
    )
  }

  // EventOpenInPlayback only needs a camera and an instant; the outage is handed to it in that
  // shape rather than duplicating the monitor-picking logic here.
  const asEpisode: EpisodeDto | null = selected
    ? {
        eventUuid: selected.outageUid,
        eventId: selected.outageUid,
        cameraId: selected.cameraId,
        streamName: selected.streamName,
        alarmStartTs: selected.startedTs,
        alarmEndTs: selected.endedTs,
      }
    : null

  const renderTimeline = () => {
    if (!selected) return null
    const win = gapWindow(selected, nowMs)
    const padded = paddedWindow(win)
    const view = { startMs: padded.startMs, endMs: padded.endMs }
    const band = (seg: Segment, color: string, hatched: boolean, key: string) => {
      const left = msToX(Math.max(seg.startMs, view.startMs), view, TRACK_PX)
      const right = msToX(Math.min(seg.endMs, view.endMs), view, TRACK_PX)
      if (right <= 0 || left >= TRACK_PX) return null
      return (
        <Box
          key={key}
          sx={{
            position: 'absolute',
            top: 0,
            bottom: 0,
            left: `${left}px`,
            width: `${Math.max(1, right - left)}px`,
            // Hatching, not a fourth colour: primary and warning already mean something on this
            // timeline, and one more flat shade would be read as another kind of coverage.
            backgroundColor: (th) => alpha(th.palette[color === 'error' ? 'error' : 'warning'].main, 0.25),
            backgroundImage: hatched
              ? 'repeating-linear-gradient(45deg, rgba(255,255,255,.35) 0 3px, transparent 3px 6px)'
              : undefined,
          }}
        />
      )
    }

    const coveredInGap = Math.max(0, (win.endMs - win.startMs) - missing.reduce((a, s) => a + (s.endMs - s.startMs), 0))
    const pct = win.endMs > win.startMs ? Math.round((coveredInGap / (win.endMs - win.startMs)) * 100) : 0

    return (
      <Stack spacing={1}>
        <Box sx={{ width: TRACK_PX, maxWidth: '100%' }}>
          <WallTimelineTrack
            label={cameraLabel(selected.cameraId)}
            segments={coverage}
            win={view}
            pxWidth={TRACK_PX}
            height={TRACK_HEIGHT}
            emphasis
          >
            {band(win, 'warning', false, 'outage')}
            {missing.map((s, i) => band(s, 'error', true, `missing-${i}`))}
          </WallTimelineTrack>
        </Box>
        <Stack direction="row" spacing={2}>
          <Typography variant="caption" color="text.secondary">
            {new Date(view.startMs).toLocaleTimeString()}
          </Typography>
          <Box sx={{ flex: 1 }} />
          <Typography variant="caption" color="text.secondary">
            {new Date(view.endMs).toLocaleTimeString()}
          </Typography>
        </Stack>
        <Typography variant="body2">{t('gaps.covered', { pct })}</Typography>
      </Stack>
    )
  }

  return (
    <Card variant="outlined">
      <CardContent sx={{ p: 2 }}>
        <Stack spacing={2}>
          <Typography variant="h6">{t('gaps.title')}</Typography>
          {loading && gaps.length === 0 && <LinearProgress />}
          {error && (
            <Typography color="error">
              {error} <Button size="small" onClick={() => void load()}>{t('events.retry')}</Button>
            </Typography>
          )}

          <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} alignItems="flex-start">
            <Box sx={{
              width: { xs: '100%', md: 280 }, flexShrink: 0,
              display: 'flex', flexDirection: 'column',
              maxHeight: { md: COLUMN_MAX_HEIGHT }
            }}>
              <Stack direction="row" spacing={1} sx={{ mb: 1, flexShrink: 0 }}>
                <TextField
                  select
                  size="small"
                  value={cameraFilter}
                  onChange={(e) => setCameraFilter(e.target.value)}
                  sx={{ flex: 1, minWidth: 0 }}
                  SelectProps={{ displayEmpty: true }}
                >
                  <MenuItem value="">{t('fsActivity.allCameras')}</MenuItem>
                  {cameraIds.map((id) => (
                    <MenuItem key={id} value={id}>{camerasMap[id]?.name || id}</MenuItem>
                  ))}
                </TextField>
                <TextField
                  select
                  size="small"
                  value={windowKey}
                  onChange={(e) => setWindowKey(e.target.value as typeof windowKey)}
                  sx={{ width: 100 }}
                >
                  {WINDOWS.map((w) => (
                    <MenuItem key={w.key} value={w.key}>{t(`gaps.window.${w.key}`)}</MenuItem>
                  ))}
                </TextField>
              </Stack>

              <Box sx={{
                border: 1, borderColor: 'divider', borderRadius: 1,
                flex: 1, minHeight: 0, maxHeight: { xs: 320, md: 'none' }, overflow: 'auto'
              }}>
                <List dense disablePadding>
                  {gaps.map(renderGap)}
                </List>
                {gaps.length === 0 && !loading && (
                  <Typography variant="body2" color="text.secondary" sx={{ py: 2, px: 1 }}>
                    {t('gaps.noRows')}
                  </Typography>
                )}
              </Box>

              <Stack direction="row" spacing={1} alignItems="center" sx={{ mt: 1, flexShrink: 0 }}>
                <Button variant="outlined" size="small" disabled={loading} onClick={() => void load()}>
                  {t('events.refresh')}
                </Button>
                {loading && gaps.length > 0 && <LinearProgress sx={{ flex: 1, minWidth: 60 }} />}
              </Stack>

              <Stack sx={{ mt: 0.5, flexShrink: 0 }}>
                <FormControlLabel
                  control={<Switch size="small" checked={recordingOnly} onChange={(e) => setRecordingOnly(e.target.checked)} />}
                  label={<Typography variant="caption" color="text.secondary">{t('gaps.recordingOnly')}</Typography>}
                />
                <FormControlLabel
                  control={<Switch size="small" checked={autoRefresh} onChange={(e) => setAutoRefresh(e.target.checked)} />}
                  label={<Typography variant="caption" color="text.secondary">{t('events.autoRefresh', 'Auto-refresh')}</Typography>}
                />
              </Stack>
            </Box>

            <Box sx={{ flex: 1, minWidth: 0, width: '100%' }}>
              {selected ? (
                <Stack spacing={2}>
                  <Stack direction="row" alignItems="center" spacing={1}>
                    <Typography variant="subtitle1" sx={{ flex: 1, minWidth: 0 }} noWrap>
                      {cameraLabel(selected.cameraId)}
                      {selected.streamName ? ` / ${selected.streamName}` : ''}
                    </Typography>
                    <EventOpenInPlayback episode={asEpisode} />
                  </Stack>

                  {renderTimeline()}

                  <Divider />

                  <Stack spacing={0.5}>
                    <Typography variant="body2">
                      {t('gaps.from')}: {new Date(selected.startedTs).toLocaleString()}
                    </Typography>
                    <Typography variant="body2">
                      {t('gaps.to')}: {selected.endedTs ? new Date(selected.endedTs).toLocaleString() : t('gaps.ongoing')}
                    </Typography>
                    <Typography variant="body2">
                      {t('gaps.causeLabel')}: {t(`gaps.cause.${selected.cause}`, selected.cause)}
                    </Typography>
                    <Typography variant="body2">
                      {t('gaps.stateLabel')}: {t(`gaps.state.${selected.state}`, selected.state)}
                      {selected.attempts > 0 ? ` (${t('gaps.attempts', { count: selected.attempts })})` : ''}
                    </Typography>
                    {!selected.recordingExpected && (
                      <Typography variant="body2" color="text.secondary">{t('gaps.notRecordedHint')}</Typography>
                    )}
                    {selected.message && (
                      <Typography variant="body2" color="text.secondary">{selected.message}</Typography>
                    )}
                  </Stack>
                </Stack>
              ) : (
                <Typography variant="body2" color="text.secondary">{t('gaps.noSelection')}</Typography>
              )}
            </Box>
          </Stack>
        </Stack>
      </CardContent>
    </Card>
  )
}
