import { useCallback, useEffect, useMemo, useState } from 'react'
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
  EpisodeDto,
  RecordingDto,
  RecordingsResponseDto,
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
// Slivers below this are not holes: clip boundaries land on keyframes while the outage came from a
// report, so the two never agree to the millisecond. Same tolerance the backend applies.
const FILL_TOLERANCE_MS = 1000

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
  const [refillError, setRefillError] = useState<string | null>(null)
  const [preview, setPreview] = useState<EdgePreviewTarget | null>(null)
  // The import the scheduler is running right now, if any. A run started here reports its own
  // progress; one started by automation is only visible as the job on the media server, so the tab
  // watches that instead of showing a bare "Filling" and nothing else.
  const [autoJob, setAutoJob] = useState<EdgeImportJobDto | null>(null)
  // The clips that were actually written for this gap. Shown by name because "restored" is easier
  // to trust when you can see what it produced and where it went.
  const [recovered, setRecovered] = useState<RecordingDto[]>([])
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

  // Only footage recovered from the camera closes a gap — the live clips inside one are what the
  // failing source produced, so they are the reason it exists rather than a repair for it. Same
  // rule the scheduler and the gateway apply, or the button and the automation would disagree
  // about what is left to do.
  const missing = useMemo(() => {
    if (!selected) return []
    return complementSegments(
      coverage.filter((seg) => seg.origin === 'edge'), gapWindow(selected, nowMs))
      .filter((seg) => seg.endMs - seg.startMs >= FILL_TOLERANCE_MS)
  }, [coverage, selected, nowMs])

  /**
   * Asks for the missing footage to be fetched — by handing the gap to the scheduler, not by
   * pulling anything from here.
   *
   * This used to drive the whole recovery from the browser: ask the camera what it holds, plan the
   * pieces, re-check coverage before each one, start an import and poll it. That is the scheduler's
   * job, and doing it twice meant two routes racing for the device's single import slot — whoever
   * lost was refused, so the button worked or not depending on timing nobody could see. Work started
   * from a click was also just a live request: close the tab or restart a service mid-pull and it
   * was gone, while a queued job survives on an expired lease.
   *
   * Everything that made the old loop careful still happens, one layer down: what is missing is
   * recomputed from the archive before each piece and intersected with what the camera still holds.
   */
  const runRefill = useCallback(async () => {
    if (!selected) return
    setRefillError(null)
    try {
      const response = await fetch(
        `/api/gaps/${encodeURIComponent(selected.outageUid)}/refill`, { method: 'POST' })
      if (!response.ok) {
        setRefillError(await getErrorMessage(response))
        return
      }
    } catch (e) {
      setRefillError(e instanceof Error ? e.message : String(e))
    } finally {
      void load()
    }
  }, [selected, load])


  useEffect(() => {
    if (!selected || selected.state !== 'filling') {
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
  }, [selected, nowMs])

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

  useEffect(() => {
    if (!selected) {
      setRecovered([])
      return
    }

    let dropped = false
    const win = gapWindow(selected, nowMs)
    void (async () => {
      try {
        const response = await fetch('/api/fs/recordings', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            cameraId: selected.cameraId,
            streamName: selected.streamName || undefined,
            // Widened by the same margin the import asks for, or the clip that starts just before
            // the gap — which is most of them — would not be listed as recovered at all.
            fromUtc: new Date(win.startMs - IMPORT_MARGIN_MS * 2).toISOString(),
            toUtc: new Date(win.endMs + IMPORT_MARGIN_MS * 2).toISOString(),
            limit: 50,
          }),
        })
        if (!response.ok) return
        const body = (await response.json()) as RecordingsResponseDto
        if (!dropped) {
          setRecovered((body.recordings ?? []).filter((r) => r.origin === 'edge'))
        }
      } catch { /* the panel simply shows nothing */ }
    })()
    return () => { dropped = true }
  }, [selected, nowMs])

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
    // Only two things matter here: what the import brought back, and what is still missing. Footage
    // that happened to be inside the window from ordinary recording is not a repair — and since a
    // gap is closed by recovered material alone, it counts as missing and is drawn that way.
    // Taken from the same coverage the verdict is computed from, not from a second query. Two
    // sources answering the same question is how the bar came to disagree with the state: a clip
    // recovered with the import's margin starts before the gap and fell outside the other lookup,
    // so its footage was drawn as if it had always been there.
    const fromCamera = intersectSegments(coverage.filter((seg) => seg.origin === 'edge'), [win])
    const restoredPct = Math.round(
      (fromCamera.reduce((a, sg) => a + (sg.endMs - sg.startMs), 0) / span) * 100)

    const piece = (seg: Segment, key: string, kind: 'restored' | 'missing') => {
      const patched = kind !== 'missing'
      return (
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
          bgcolor: (th) => alpha(
            kind === 'restored' ? th.palette.success.main : th.palette.error.main,
            kind === 'restored' ? 0.75 : 0.25),
          backgroundImage: kind === 'missing'
            ? 'repeating-linear-gradient(45deg, rgba(255,255,255,.35) 0 3px, transparent 3px 6px)'
            : undefined,
          cursor: patched ? 'pointer' : 'default',
        }}
      />
      )
    }

    return (
      <Stack spacing={0.75}>
        <Box sx={{
          position: 'relative',
          height: 26,
          borderRadius: '3px',
          overflow: 'hidden',
          bgcolor: (th) => alpha(th.palette.text.primary, 0.06),
        }}>
          {missing.map((sg, i) => piece(sg, `missing-${i}`, 'missing'))}
          {fromCamera.map((sg, i) => piece(sg, `restored-${i}`, 'restored'))}
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
              bgcolor: (th) => alpha(th.palette.success.main, 0.75) }} />
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
            {t('gaps.covered', { pct: restoredPct })} · {formatDuration(span)}
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
                    {autoJob ? (
                      <Button size="small" color="warning" onClick={() => void cancelAutoJob()}>
                        {t('gaps.cancelRefill')}
                      </Button>
                    ) : selected.state !== 'open' && selected.state !== 'filling' ? (
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
                      <>
                        {/* A judgement about value, not about the archive: the footage is not wanted,
                            so the gap stops being work. Reversible through Resume above — a misclick
                            must not cost a recovery that was still possible. */}
                        <Button
                          size="small"
                          onClick={() => void (async () => {
                            await fetch(`/api/gaps/${encodeURIComponent(selected.outageUid)}/ignore`,
                              { method: 'POST' }).catch(() => undefined)
                            void load()
                          })()}
                        >
                          {t('gaps.ignore')}
                        </Button>
                        <Button
                          size="small"
                          variant="contained"
                          // Never disabled. With nothing missing it only re-reads the state, which is
                          // how a gap filled earlier stops claiming it is not; and "nothing left on
                          // the camera" is a verdict from one search, which a camera that was merely
                          // unreachable can produce — asking again costs a session and nothing else.
                          onClick={() => void runRefill()}
                        >
                          {missing.length === 0 ? t('gaps.recheck') : t('gaps.refill')}
                        </Button>
                      </>
                    )}
                    <EventOpenInPlayback episode={asEpisode} />
                  </Stack>

                  {autoJob && (() => {
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

                    {recovered.length > 0 && (
                      <>
                        <Typography variant="body2" sx={{ mt: 1 }}>
                          {t('gaps.recoveredClips', { count: recovered.length })}
                        </Typography>
                        {recovered.map((clip) => (
                          <Typography
                            key={clip.clipUid || clip.filePath}
                            variant="caption"
                            color="text.secondary"
                            component="div"
                            sx={{ wordBreak: 'break-all', cursor: 'pointer' }}
                            onClick={() => setPreview({
                              kind: 'archive',
                              cameraId: selected.cameraId,
                              from: clip.startTime,
                              to: clip.endTime ?? new Date(gapWindow(selected, nowMs).endMs).toISOString(),
                              label: clip.filePath,
                            })}
                          >
                            {new Date(clip.startTime).toLocaleTimeString()} — {clip.filePath}
                          </Typography>
                        ))}
                      </>
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
