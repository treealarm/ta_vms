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

import type {
  EdgeImportJobDto,
  EdgeIntervalsDto,
  EpisodeDto,
  SourceOutageDto
} from '../dto/dtos'
import { useAppDispatch, useAppSelector } from '../store'
import { getErrorMessage } from '../store/api'
import { fetchRecordingTimeline, selectWallCoverage, wallCoverageKey } from '../store/wallCoverageSlice'
import { complementSegments, intersectSegments, type Segment } from '../utils/archiveRanges'
import EdgeMediaPreview, { type EdgePreviewTarget } from './EdgeMediaPreview'
import EventOpenInPlayback from './EventOpenInPlayback'

// Fixed, not a maximum: with a max the list shrinks to its content and drags the controls beneath
// it up and down as rows come and go. The controls belong in one place regardless of what is in the
// list, so the column keeps its height and the list scrolls inside it.
const COLUMN_HEIGHT = 'min(75vh, 900px)'
const AUTO_REFRESH_MS = 15_000
// Nominal width the band is quantised against: gaps thinner than one pixel of it are not worth
// drawing, and asking the server for them only makes the answer bigger.
const BAND_PX = 640

const IMPORT_POLL_MS = 2000
// Asked for slightly wider than the hole. The camera starts a replay at the keyframe it can, and a
// piece cut exactly to the boundary loses the frames around the seam; a second of overlap with the
// live footage either side costs nothing, since clips are played in order of their own start.
const IMPORT_MARGIN_MS = 1000

const WINDOWS = [
  { key: '24h', ms: 24 * 3600_000 },
  { key: '7d', ms: 7 * 24 * 3600_000 },
  { key: '30d', ms: 30 * 24 * 3600_000 },
] as const

function gapWindow(gap: SourceOutageDto, nowMs: number): Segment {
  const startMs = Date.parse(gap.startedTs)
  const endMs = gap.endedTs ? Date.parse(gap.endedTs) : nowMs
  return { startMs, endMs: Math.max(endMs, startMs + 1000) }
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


/** What the camera still holds inside a window, as plain intervals plus the token to replay them. */
type CameraSpan = Segment & { recordingToken: string }

async function fetchCameraSpans(
  cameraId: string,
  win: Segment,
  signal: AbortSignal
): Promise<CameraSpan[]> {
  const qs = new URLSearchParams({
    from: new Date(win.startMs).toISOString(),
    to: new Date(win.endMs).toISOString(),
    maxResults: '200',
  })
  const response = await fetch(
    `/api/edge-archive/cameras/${encodeURIComponent(cameraId)}/intervals?${qs}`, { signal })
  if (!response.ok) throw new Error(await getErrorMessage(response))
  const body = (await response.json()) as EdgeIntervalsDto
  return (body.intervals ?? [])
    .map((i) => ({
      startMs: Date.parse(i.startTime),
      endMs: Date.parse(i.endTime),
      recordingToken: i.recordingToken,
    }))
    .filter((s) => Number.isFinite(s.startMs) && Number.isFinite(s.endMs) && s.endMs > s.startMs)
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
  const [autoRefresh, setAutoRefresh] = useState(true)
  const [selectedUid, setSelectedUid] = useState<string | null>(null)
  // Re-rendered every tick would move every open outage's end under the operator's cursor; this is
  // sampled once per load instead.
  const [nowMs, setNowMs] = useState(() => Date.now())
  const [refill, setRefill] =
    useState<{ done: number; total: number; label: string; fraction: number } | null>(null)
  const [refillError, setRefillError] = useState<string | null>(null)
  const [preview, setPreview] = useState<EdgePreviewTarget | null>(null)
  // The import the scheduler is running right now, if any. A run started here reports its own
  // progress; one started by automation is only visible as the job on the media server, so the tab
  // watches that instead of showing a bare "Filling" and nothing else.
  const [autoJob, setAutoJob] = useState<EdgeImportJobDto | null>(null)
  // Set by the Cancel button and read between pieces; the job already running is cancelled through
  // the API, and the loop stops rather than starting the next one.
  const cancelRefill = useRef(false)

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
  }, [cameraFilter, windowMs])

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
    const win = gapWindow(selected, nowMs)
    return {
      cameraIds: [selected.cameraId],
      streamName: selected.streamName || undefined,
      fromUtc: new Date(win.startMs).toISOString(),
      toUtc: new Date(win.endMs).toISOString(),
      minGapMs: Math.max(1000, Math.round((win.endMs - win.startMs) / BAND_PX)),
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

  /**
   * Pulls the parts of the gap the archive is missing back from the camera, one at a time.
   *
   * What is still missing is recomputed from the archive before every piece, so an interrupted run
   * resumes where it stopped instead of starting over — clips already written are simply no longer
   * missing. Intersecting with what the camera reports also stops the job from asking for footage
   * the card has already overwritten.
   */
  const runRefill = useCallback(async () => {
    if (!selected) return
    cancelRefill.current = false
    setRefillError(null)

    const controller = new AbortController()
    const win = gapWindow(selected, Date.now())
    try {
      // Nothing missing: no reason to ask the camera anything, but the row still has to be told —
      // the outcome is written in the finally below, which is what turns "not filled" into "filled".
      if (missing.length === 0)
        return

      // Visible to anyone else looking at the tab, and the marker automation will read before
      // taking a gap of its own accord.
      await fetch(`/api/gaps/${encodeURIComponent(selected.outageUid)}/filling`, { method: 'POST' })
        .catch(() => undefined)

      const available = await fetchCameraSpans(selected.cameraId, win, controller.signal)
      if (available.length === 0) {
        setRefillError(t('gaps.nothingOnCamera'))
        return
      }

      // Planned once for the progress count; each piece is re-checked against the archive below.
      const planned = intersectSegments(complementSegments(coverage, win), available)
      if (planned.length === 0) {
        setRefillError(t('gaps.alreadyComplete'))
        return
      }

      for (let i = 0; i < planned.length; i += 1) {
        if (cancelRefill.current) break
        setRefill({
          done: i,
          total: planned.length,
          label: new Date(planned[i].startMs).toLocaleTimeString(),
          fraction: 0,
        })

        // Fresh coverage, so a piece finished by a previous run — or partially written before a
        // failure — is not pulled again.
        const fresh = await dispatch(fetchRecordingTimeline({
          cameraIds: [selected.cameraId],
          streamName: selected.streamName || undefined,
          fromUtc: new Date(planned[i].startMs).toISOString(),
          toUtc: new Date(planned[i].endMs).toISOString(),
          minGapMs: 1000,
        })).unwrap()
        const have: Segment[] = (fresh.tracks ?? [])
          .flatMap((track) => track.intervals ?? [])
          .map((iv) => ({ startMs: Date.parse(iv.startTime), endMs: Date.parse(iv.endTime) }))
          .filter((seg) => Number.isFinite(seg.startMs) && Number.isFinite(seg.endMs))
        const todo = intersectSegments(complementSegments(have, planned[i]), available)
        if (todo.length === 0) continue

        for (const piece of todo) {
          if (cancelRefill.current) break
          const token = available.find((a) => a.startMs <= piece.startMs && a.endMs >= piece.endMs)
            ?? available.find((a) => a.endMs > piece.startMs && a.startMs < piece.endMs)
          if (!token) continue

          const started = await fetch(
            `/api/edge-archive/cameras/${encodeURIComponent(selected.cameraId)}/imports`,
            {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({
                recordingToken: token.recordingToken,
                streamName: selected.streamName || undefined,
                startTime: new Date(piece.startMs - IMPORT_MARGIN_MS).toISOString(),
                endTime: new Date(piece.endMs + IMPORT_MARGIN_MS).toISOString(),
                exportToFile: false,
              }),
            })
          if (!started.ok) {
            setRefillError(await getErrorMessage(started))
            return
          }
          const { jobId } = (await started.json()) as { jobId: string }
          if (!(await awaitJob(selected.cameraId, jobId, piece))) return
        }
      }
    } catch (e) {
      if ((e as Error).name !== 'AbortError') {
        setRefillError(e instanceof Error ? e.message : String(e))
      }
    } finally {
      setRefill(null)
      // What the run achieved is judged from the archive and the camera, not from "the job said OK":
      // a job can finish successfully and still have recovered nothing. Without this the gap would
      // stay "not filled" forever, and automation would keep retrying footage the card has already
      // overwritten.
      await fetch(`/api/gaps/${encodeURIComponent(selected.outageUid)}/refresh`, { method: 'POST' })
        .catch(() => undefined)
      if (cancelRefill.current) {
        // Cancelling is a decision, not a failure: the gap is parked until the operator hands it
        // back, so automation does not simply pick it up again on the next tick.
        await fetch(`/api/gaps/${encodeURIComponent(selected.outageUid)}/hold`, { method: 'POST' })
          .catch(() => undefined)
      }
      void load()
      // The coverage behind the band is cached by query, and a closed gap's query does not change —
      // without an explicit refetch the bar would keep showing what was there before the import.
      if (coverageQuery) void dispatch(fetchRecordingTimeline(coverageQuery))
    }
  }, [selected, coverage, coverageQuery, dispatch, load, t])

  /** Waits for one import to finish, reporting its progress. False = failed, cancelled or aborted. */
  const awaitJob = useCallback(async (
    cameraId: string, jobId: string, piece: Segment): Promise<boolean> => {
    for (;;) {
      if (cancelRefill.current)
      {
        // The device runs one import at a time; leaving it running would block everything after.
        await fetch(`/api/edge-archive/cameras/${encodeURIComponent(cameraId)}/imports/${jobId}`,
          { method: 'DELETE' }).catch(() => undefined)
        return false
      }
      await new Promise((resolve) => setTimeout(resolve, IMPORT_POLL_MS))
      const response = await fetch(`/api/edge-archive/cameras/${encodeURIComponent(cameraId)}/imports`)
      if (!response.ok) return false
      const jobs = (await response.json()) as EdgeImportJobDto[]
      const job = jobs.find((j) => j.jobId === jobId)
      if (!job) return false
      if (job.state === 'running')
      {
        // How far into this piece the pull has got. Without it a long stretch would show a bar
        // pinned at whatever the piece counter says for minutes on end.
        const at = job.position ? Date.parse(job.position) : NaN
        if (Number.isFinite(at) && piece.endMs > piece.startMs)
        {
          const fraction = Math.min(1, Math.max(0, (at - piece.startMs) / (piece.endMs - piece.startMs)))
          setRefill((prev) => (prev ? { ...prev, fraction } : prev))
        }
        continue
      }
      if (job.state === 'completed') return true
      setRefillError(job.message || t('gaps.refillFailed'))
      return false
    }
  }, [t])

  useEffect(() => {
    if (!selected || selected.state !== 'filling' || refill) {
      setAutoJob(null)
      return
    }

    let dropped = false
    const win = gapWindow(selected, nowMs)
    const poll = async () => {
      try {
        const response = await fetch(
          `/api/edge-archive/cameras/${encodeURIComponent(selected.cameraId)}/imports`)
        if (!response.ok) return
        const jobs = (await response.json()) as EdgeImportJobDto[]
        // Matched by overlap rather than by id: the row does not record which job is filling it,
        // and the camera runs one import at a time anyway.
        const running = jobs.find((j) => j.state === 'running'
          && j.startTime && j.endTime
          && Date.parse(j.endTime) > win.startMs && Date.parse(j.startTime) < win.endMs)
        if (!dropped) setAutoJob(running ?? null)
      } catch { /* the next tick tries again */ }
    }

    void poll()
    const timer = window.setInterval(() => void poll(), IMPORT_POLL_MS)
    return () => { dropped = true; window.clearInterval(timer) }
  }, [selected, refill, nowMs])

  /** Stops the scheduler's run and parks the gap, so it is not picked up again on the next tick. */
  const cancelAutoJob = useCallback(async () => {
    if (!selected || !autoJob) return
    await fetch(
      `/api/edge-archive/cameras/${encodeURIComponent(selected.cameraId)}/imports/${autoJob.jobId}`,
      { method: 'DELETE' }).catch(() => undefined)
    await fetch(`/api/gaps/${encodeURIComponent(selected.outageUid)}/hold`, { method: 'POST' })
      .catch(() => undefined)
    setAutoJob(null)
    void load()
  }, [selected, autoJob, load])

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

  const renderBand = () => {
    if (!selected) return null
    const win = gapWindow(selected, nowMs)
    const span = Math.max(1, win.endMs - win.startMs)
    // Only what lies inside the gap. The surrounding archive is deliberately not drawn: it answers a
    // question this tab does not ask (the monitor shows it), and mixing it in made the band read as
    // "recording coverage" rather than "how much of this hole has been filled".
    const filled = intersectSegments(coverage, [win])
    const pct = Math.round((filled.reduce((a, sg) => a + (sg.endMs - sg.startMs), 0) / span) * 100)

    const piece = (seg: Segment, key: string, patched: boolean) => (
      <Box
        key={key}
        onClick={patched ? () => setPreview({
          kind: 'archive',
          cameraId: selected.cameraId,
          from: new Date(seg.startMs).toISOString(),
          to: new Date(seg.endMs).toISOString(),
          label: `${new Date(seg.startMs).toLocaleTimeString()} — ${new Date(seg.endMs).toLocaleTimeString()}`,
        }) : undefined}
        title={patched ? t('gaps.playPatched') : undefined}
        sx={{
          position: 'absolute',
          top: 0,
          bottom: 0,
          left: `${((Math.max(seg.startMs, win.startMs) - win.startMs) / span) * 100}%`,
          width: `${Math.max(0.5, ((Math.min(seg.endMs, win.endMs) - Math.max(seg.startMs, win.startMs)) / span) * 100)}%`,
          bgcolor: (th) => alpha(patched ? th.palette.success.main : th.palette.error.main, patched ? 0.7 : 0.25),
          backgroundImage: patched
            ? undefined
            : 'repeating-linear-gradient(45deg, rgba(255,255,255,.35) 0 3px, transparent 3px 6px)',
          cursor: patched ? 'pointer' : 'default',
        }}
      />
    )

    return (
      <Stack spacing={0.75}>
        <Box sx={{
          position: 'relative',
          height: 26,
          borderRadius: '3px',
          overflow: 'hidden',
          bgcolor: (th) => alpha(th.palette.text.primary, 0.06),
        }}>
          {missing.map((sg, i) => piece(sg, `missing-${i}`, false))}
          {filled.map((sg, i) => piece(sg, `filled-${i}`, true))}
        </Box>

        <Stack direction="row" spacing={1}>
          <Typography variant="caption" color="text.secondary">
            {new Date(win.startMs).toLocaleString()}
          </Typography>
          <Box sx={{ flex: 1 }} />
          <Typography variant="caption" color="text.secondary">
            {selected.endedTs ? new Date(win.endMs).toLocaleString() : t('gaps.ongoing')}
          </Typography>
        </Stack>

        {/* Named rather than left to colour alone: the band shows two things and only two. */}
        <Stack direction="row" spacing={2} alignItems="center" flexWrap="wrap">
          <Stack direction="row" spacing={0.5} alignItems="center">
            <Box sx={{ width: 12, height: 12, borderRadius: '2px',
              bgcolor: (th) => alpha(th.palette.success.main, 0.7) }} />
            <Typography variant="caption" color="text.secondary">{t('gaps.legendPatched')}</Typography>
          </Stack>
          <Stack direction="row" spacing={0.5} alignItems="center">
            <Box sx={{
              width: 12, height: 12, borderRadius: '2px',
              bgcolor: (th) => alpha(th.palette.error.main, 0.25),
              backgroundImage: 'repeating-linear-gradient(45deg, rgba(255,255,255,.35) 0 3px, transparent 3px 6px)',
            }} />
            <Typography variant="caption" color="text.secondary">{t('gaps.legendMissing')}</Typography>
          </Stack>
          <Box sx={{ flex: 1 }} />
          <Typography variant="body2">
            {t('gaps.covered', { pct })} · {formatDuration(span)}
          </Typography>
        </Stack>
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
              height: { xs: 'auto', md: COLUMN_HEIGHT }
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
                flex: 1, minHeight: 0, height: { xs: 320, md: 'auto' }, overflow: 'auto'
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
                    {refill ? (
                      <Button size="small" color="warning" onClick={() => { cancelRefill.current = true }}>
                        {t('gaps.cancelRefill')}
                      </Button>
                    ) : autoJob ? (
                      <Button size="small" color="warning" onClick={() => void cancelAutoJob()}>
                        {t('gaps.cancelRefill')}
                      </Button>
                    ) : selected.state === 'held' ? (
                      <Button
                        size="small"
                        variant="outlined"
                        onClick={() => void (async () => {
                          await fetch(`/api/gaps/${encodeURIComponent(selected.outageUid)}/resume`,
                            { method: 'POST' }).catch(() => undefined)
                          void load()
                        })()}
                      >
                        {t('gaps.resume')}
                      </Button>
                    ) : (
                      <Button
                        size="small"
                        variant="contained"
                        // Enabled even with nothing missing: pressing it then only re-reads the
                        // state, which is how a gap filled by an earlier run stops claiming it is
                        // not. Exhausted is terminal — the footage is gone from the card for good.
                        disabled={selected.state === 'exhausted'}
                        onClick={() => void runRefill()}
                      >
                        {missing.length === 0 ? t('gaps.recheck') : t('gaps.refill')}
                      </Button>
                    )}
                    <EventOpenInPlayback episode={asEpisode} />
                  </Stack>

                  {refill && (
                    <Stack spacing={0.5}>
                      <Typography variant="caption" color="text.secondary">
                        {t('gaps.refilling', { done: refill.done + 1, total: refill.total, at: refill.label })}
                      </Typography>
                      <LinearProgress
                        variant="determinate"
                        value={Math.round(((refill.done + refill.fraction) / Math.max(1, refill.total)) * 100)}
                      />
                    </Stack>
                  )}
                  {!refill && autoJob && (() => {
                    const from = autoJob.startTime ? Date.parse(autoJob.startTime) : NaN
                    const to = autoJob.endTime ? Date.parse(autoJob.endTime) : NaN
                    const at = autoJob.position ? Date.parse(autoJob.position) : NaN
                    const pct = Number.isFinite(from) && Number.isFinite(to) && to > from && Number.isFinite(at)
                      ? Math.min(100, Math.max(0, Math.round(((at - from) / (to - from)) * 100)))
                      : 0
                    return (
                      <Stack spacing={0.5}>
                        <Typography variant="caption" color="text.secondary">
                          {t('gaps.autoRefilling', {
                            at: Number.isFinite(at) ? new Date(at).toLocaleTimeString() : '—',
                          })}
                        </Typography>
                        <LinearProgress variant="determinate" value={pct} />
                      </Stack>
                    )
                  })()}
                  {refillError && <Typography variant="body2" color="error">{refillError}</Typography>}

                  {renderBand()}

                  <Divider />

                  <Stack spacing={0.5}>
                    <Typography variant="body2">
                      {t('gaps.from')}: {new Date(selected.startedTs).toLocaleString()}
                    </Typography>
                    <Typography variant="body2">
                      {t('gaps.to')}: {selected.endedTs ? new Date(selected.endedTs).toLocaleString() : t('gaps.ongoing')}
                    </Typography>
                    <Typography variant="body2">
                      {t('gaps.duration')}: {formatDuration(
                        gapWindow(selected, nowMs).endMs - gapWindow(selected, nowMs).startMs)}
                    </Typography>
                    <Typography variant="body2">
                      {t('gaps.causeLabel')}: {t(`gaps.cause.${selected.cause}`, selected.cause)}
                    </Typography>
                    <Typography variant="body2">
                      {t('gaps.stateLabel')}: {t(`gaps.state.${selected.state}`, selected.state)}
                      {selected.attempts > 0 ? ` (${t('gaps.attempts', { count: selected.attempts })})` : ''}
                    </Typography>
                    {selected.message && (
                      <Typography variant="body2" color="text.secondary">{selected.message}</Typography>
                    )}
                  </Stack>
                </Stack>
              ) : (
                <Typography variant="body2" color="text.secondary">{t('gaps.noSelection')}</Typography>
              )}
            </Box>

            {/* Right: what has actually been recovered, played back without leaving the page. */}
            <Box sx={{
              width: { xs: '100%', md: 380 }, flexShrink: 0,
              maxHeight: { xs: 'none', md: COLUMN_HEIGHT }, overflow: 'auto'
            }}>
              {preview ? (
                <Stack spacing={1}>
                  <Typography variant="subtitle2">{preview.label}</Typography>
                  <EdgeMediaPreview target={preview} />
                </Stack>
              ) : (
                <Typography variant="body2" color="text.secondary">{t('gaps.previewPlaceholder')}</Typography>
              )}
            </Box>
          </Stack>
        </Stack>
      </CardContent>
    </Card>
  )
}
