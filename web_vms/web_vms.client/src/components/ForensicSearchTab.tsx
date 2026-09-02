import { useEffect, useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  CircularProgress,
  Collapse,
  Divider,
  IconButton,
  List,
  ListItemButton,
  MenuItem,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import ExpandLessIcon from '@mui/icons-material/ExpandLess'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import { DateTimePicker } from '@mui/x-date-pickers/DateTimePicker'
import { LocalizationProvider } from '@mui/x-date-pickers/LocalizationProvider'
import { AdapterDayjs } from '@mui/x-date-pickers/AdapterDayjs'
import ChevronLeftIcon from '@mui/icons-material/ChevronLeft'
import ChevronRightIcon from '@mui/icons-material/ChevronRight'
import FirstPageIcon from '@mui/icons-material/FirstPage'
import dayjs, { type Dayjs } from 'dayjs'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import type { AnalyticsDetectionKind, SimilarDetectionHitDto } from '../dto/dtos'
import { ANALYTICS_DETECTION_CLASSES, recordableStreams } from '../dto/dtos'
import { useAppSelector } from '../store'
import { getErrorMessage } from '../store/api'
import DetectionArchivePreview from './DetectionArchivePreview'
import OpenCameraInPlayback from './OpenCameraInPlayback'

type ProbeMode = 'detection' | 'archive' | 'image'

type SearchPayload =
  | { detection: { detectionId: number } }
  | { archive: { cameraId: string; trackId: number; ts: string; kind: AnalyticsDetectionKind } }
  | { image: { jpegBase64: string; kind: AnalyticsDetectionKind; detectFirst: boolean } }

const PAGE_SIZE = 20

/** One track appearance on one camera — many raw detection hits collapse into this row. */
type AppearanceGroup = {
  key: string
  cameraId: string
  trackId: number
  kind: AnalyticsDetectionKind
  bestScore: number
  firstTs: string
  lastTs: string
  hits: SimilarDetectionHitDto[]
  /** Highest-scoring hit (prefer one with a crop) — drives preview + crop. */
  bestHit: SimilarDetectionHitDto
}

function defaultMinScore(kind: AnalyticsDetectionKind): number {
  // Unaligned ArcFace (no 5-point warp) puts the same face on a second camera around 0.33–0.39;
  // 0.45 was a seenaptec default for *aligned* crops and silently drops the true match. Body ReID
  // is much tighter (~0.7 on the same pair), so person/vehicle stay at 0.4.
  return kind === 'face' ? 0.32 : 0.4
}

function groupMatches(matches: SimilarDetectionHitDto[]): AppearanceGroup[] {
  const map = new Map<string, AppearanceGroup>()
  for (const m of matches) {
    const d = m.detection
    const key = `${d.cameraId}|${d.kind}|${d.trackId || d.id}`
    const existing = map.get(key)
    if (!existing) {
      map.set(key, {
        key,
        cameraId: d.cameraId,
        trackId: d.trackId,
        kind: d.kind,
        bestScore: m.score,
        firstTs: d.detectedAt,
        lastTs: d.detectedAt,
        hits: [m],
        bestHit: m,
      })
      continue
    }
    existing.hits.push(m)
    if (m.score > existing.bestScore
      || (m.score === existing.bestScore && m.detection.hasCrop && !existing.bestHit.detection.hasCrop)) {
      existing.bestScore = m.score
      existing.bestHit = m
    }
    if (d.detectedAt < existing.firstTs) existing.firstTs = d.detectedAt
    if (d.detectedAt > existing.lastTs) existing.lastTs = d.detectedAt
  }
  // Earliest appearance first — a timeline of where the object showed up.
  return [...map.values()].sort(
    (a, b) => new Date(a.firstTs).getTime() - new Date(b.firstTs).getTime()
  )
}

function initialMode(params: URLSearchParams): ProbeMode {
  if (params.has('detectionId')) return 'detection'
  if (params.has('cameraId') && params.has('trackId')) return 'archive'
  return 'image'
}

export default function ForensicSearchTab() {
  const { t } = useTranslation()
  const [searchParams] = useSearchParams()
  const cameras = useAppSelector((s) => Object.values(s.live.cameraStatesById))
  const cameraIds = useMemo(() => cameras.map((c) => c.cameraId).sort(), [cameras])

  const arrivedFromVideo = searchParams.get('from') === 'monitors'
    || searchParams.get('from') === 'investigations'
    || searchParams.get('from') === 'events'
  const [searchOpen, setSearchOpen] = useState(!arrivedFromVideo)

  const [probeMode, setProbeMode] = useState<ProbeMode>(() => initialMode(searchParams))
  const [detectionId, setDetectionId] = useState(searchParams.get('detectionId') ?? '')
  const [cameraId, setCameraId] = useState(searchParams.get('cameraId') ?? '')
  const [trackId, setTrackId] = useState(searchParams.get('trackId') ?? '')
  const [ts, setTs] = useState<Dayjs | null>(() => {
    const value = searchParams.get('ts') ?? searchParams.get('probeTs')
    return value ? dayjs(value) : dayjs()
  })
  const [kind, setKind] = useState<AnalyticsDetectionKind>(() => {
    const value = searchParams.get('kind') as AnalyticsDetectionKind | null
    return value && ANALYTICS_DETECTION_CLASSES.includes(value as typeof ANALYTICS_DETECTION_CLASSES[number])
      ? value
      : 'person'
  })
  const [imageBase64, setImageBase64] = useState('')
  const [detectFirst, setDetectFirst] = useState(true)

  const [fromTs, setFromTs] = useState<Dayjs | null>(() => {
    const center = searchParams.get('ts') ?? searchParams.get('probeTs')
    return center ? dayjs(center).subtract(24, 'hour') : dayjs().subtract(24, 'hour')
  })
  const [toTs, setToTs] = useState<Dayjs | null>(() => {
    const center = searchParams.get('ts') ?? searchParams.get('probeTs')
    return center ? dayjs(center).add(24, 'hour') : dayjs().add(24, 'hour')
  })
  const [minScore, setMinScore] = useState(() => {
    const value = searchParams.get('kind') as AnalyticsDetectionKind | null
    const initialKind = value && ANALYTICS_DETECTION_CLASSES.includes(value as typeof ANALYTICS_DETECTION_CLASSES[number])
      ? value
      : 'person'
    return defaultMinScore(initialKind)
  })
  const [limit, setLimit] = useState(200)
  const [cameraFilter, setCameraFilter] = useState<string[]>([])

  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [groups, setGroups] = useState<AppearanceGroup[]>([])
  const [hasSearched, setHasSearched] = useState(false)
  const [selected, setSelected] = useState<AppearanceGroup | null>(null)
  const [cropUrl, setCropUrl] = useState<string | null>(null)
  const [autoKey, setAutoKey] = useState('')
  const [resultCameraFilter, setResultCameraFilter] = useState('')
  const [resultPage, setResultPage] = useState(0)

  useEffect(() => {
    if (!cameraId && cameraIds.length > 0)
      setCameraId(cameraIds[0])
  }, [cameraId, cameraIds])

  useEffect(() => {
    const nextMode = initialMode(searchParams)
    setProbeMode(nextMode)
    if (searchParams.has('detectionId'))
      setDetectionId(searchParams.get('detectionId') ?? '')
    if (searchParams.has('cameraId'))
      setCameraId(searchParams.get('cameraId') ?? '')
    if (searchParams.has('trackId'))
      setTrackId(searchParams.get('trackId') ?? '')
    const nextTs = searchParams.get('ts') ?? searchParams.get('probeTs')
    if (nextTs && dayjs(nextTs).isValid()) {
      const center = dayjs(nextTs)
      setTs(center)
      setFromTs(center.subtract(24, 'hour'))
      setToTs(center.add(24, 'hour'))
    }
    const nextKind = searchParams.get('kind') as AnalyticsDetectionKind | null
    if (nextKind && ANALYTICS_DETECTION_CLASSES.includes(nextKind as typeof ANALYTICS_DETECTION_CLASSES[number])) {
      setKind(nextKind)
      setMinScore(defaultMinScore(nextKind))
    }
  }, [searchParams])

  const detectionIdValid = Number.isInteger(Number(detectionId)) && Number(detectionId) > 0
  const trackIdValid = Number.isInteger(Number(trackId)) && Number(trackId) > 0
  const datesValid = Boolean(fromTs?.isValid() && toTs?.isValid() && !fromTs.isAfter(toTs))

  const buildPayload = (): SearchPayload | null => {
    if (probeMode === 'detection') {
      if (!detectionIdValid) return null
      return { detection: { detectionId: Number(detectionId) } }
    }
    if (probeMode === 'archive') {
      if (!cameraId || !trackIdValid || !ts?.isValid()) return null
      return {
        archive: {
          cameraId,
          trackId: Number(trackId),
          ts: ts.toISOString(),
          kind,
        },
      }
    }
    if (imageBase64.length === 0) return null
    return {
      image: {
        jpegBase64: imageBase64,
        kind,
        detectFirst,
      },
    }
  }

  const canSearch = buildPayload() !== null && datesValid && minScore >= 0 && minScore <= 1 && limit > 0

  const runSearch = async (payloadOverride?: SearchPayload) => {
    const probePayload = payloadOverride ?? buildPayload()
    if (
      !probePayload
      || !fromTs?.isValid()
      || !toTs?.isValid()
      || fromTs.isAfter(toTs)
    )
      return

    setLoading(true)
    setError(null)
    setHasSearched(true)
    try {
      const payload = {
        ...probePayload,
        fromTs: fromTs.toISOString(),
        toTs: toTs.toISOString(),
        minScore,
        limit,
        cameraIds: cameraFilter,
      }

      const response = await fetch('/api/analytics/detections/search-similar', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload),
      })
      if (!response.ok)
        throw new Error(await getErrorMessage(response))
      const next = await response.json() as SimilarDetectionHitDto[]
      const nextGroups = groupMatches(next)
      setGroups(nextGroups)

      // Prefer the probe's own track near the click time so the player lands on footage the
      // operator was looking at.
      let initial: AppearanceGroup | null = nextGroups[0] ?? null
      if (probeMode === 'archive' && cameraId && trackIdValid && ts?.isValid()) {
        const probeMs = ts.valueOf()
        const trackGroups = nextGroups.filter(
          (g) => g.cameraId === cameraId
            && g.trackId === Number(trackId)
            && g.kind === kind
        )
        if (trackGroups.length > 0) {
          initial = [...trackGroups].sort(
            (a, b) => Math.abs(new Date(a.bestHit.detection.detectedAt).getTime() - probeMs)
              - Math.abs(new Date(b.bestHit.detection.detectedAt).getTime() - probeMs)
          )[0] ?? initial
        }
      } else if (probeMode === 'detection' && detectionIdValid) {
        initial = nextGroups.find((g) => g.hits.some((h) => h.detection.id === Number(detectionId)))
          ?? initial
      }
      setSelected(initial)
      setResultCameraFilter('')
      const initialIndex = initial
        ? nextGroups.findIndex((g) => g.key === initial.key)
        : 0
      setResultPage(initialIndex >= 0 ? Math.floor(initialIndex / PAGE_SIZE) : 0)
    } catch (e) {
      setGroups([])
      setSelected(null)
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    if (!arrivedFromVideo) return
    const payload = buildPayload()
    if (!payload) return
    const key = searchParams.toString()
    if (key === autoKey) return
    setAutoKey(key)
    void runSearch(payload)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [arrivedFromVideo, searchParams, detectionId, cameraId, trackId, ts, kind, probeMode])

  // Lazy crop for the selected appearance — prefer the best-scoring frame.
  useEffect(() => {
    setCropUrl((prev) => {
      if (prev) URL.revokeObjectURL(prev)
      return null
    })
    const id = selected?.bestHit.detection.id
    if (!id || !selected?.bestHit.detection.hasCrop) return
    let cancelled = false
    let objectUrl: string | null = null
    void fetch(`/api/analytics/detections/${id}/crop`)
      .then((r) => (r.ok ? r.blob() : Promise.reject()))
      .then((blob) => {
        if (cancelled) return
        objectUrl = URL.createObjectURL(blob)
        setCropUrl(objectUrl)
      })
      .catch(() => {})
    return () => {
      cancelled = true
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [selected?.bestHit.detection.id, selected?.bestHit.detection.hasCrop])

  const cameraLabel = (id: string) => cameras.find((c) => c.cameraId === id)?.name || id
  const resultCameraIds = useMemo(
    () => [...new Set(groups.map((g) => g.cameraId))].sort(),
    [groups]
  )
  const filteredGroups = useMemo(
    () => resultCameraFilter
      ? groups.filter((g) => g.cameraId === resultCameraFilter)
      : groups,
    [groups, resultCameraFilter]
  )
  const resultPageCount = Math.max(1, Math.ceil(filteredGroups.length / PAGE_SIZE))
  const pageGroups = filteredGroups.slice(resultPage * PAGE_SIZE, (resultPage + 1) * PAGE_SIZE)

  const seekingSummary = (() => {
    if (!arrivedFromVideo) return null
    if (probeMode === 'detection' && detectionIdValid) {
      return t('forensic.seekingDetection', { id: detectionId })
    }
    if (probeMode === 'archive' && cameraId && trackIdValid && ts?.isValid()) {
      return t('forensic.seekingArchive', {
        kind: t(`analytics.class.${kind}`, kind),
        trackId,
        camera: cameraLabel(cameraId),
        time: ts.toDate().toLocaleString(),
      })
    }
    return null
  })()

  const selectedHit = selected?.bestHit ?? null
  // Land the player on when this appearance started — the "First seen" the operator is looking at —
  // not the densest later crop, which can be tens of seconds later on a long track.
  const selectedSeekMs = selected
    ? new Date(selected.firstTs).getTime()
    : null

  const previewCameraId = selectedHit?.detection.cameraId ?? null
  const previewStreamName = useMemo(() => {
    if (!previewCameraId) return undefined
    const cam = cameras.find((c) => c.cameraId === previewCameraId)
    if (!cam) return undefined
    // Same rule as ArchiveWallPlayer: without a stream filter, preview.m3u8 can interleave
    // main/sub. Prefer a recordable stream — that is where the archive actually lives.
    return recordableStreams(cam)[0]?.streamName
      ?? (cam.streams ?? []).find((s) => !s.disabled)?.streamName
  }, [cameras, previewCameraId])

  return (
    <LocalizationProvider dateAdapter={AdapterDayjs}>
      <Card
        variant="outlined"
        sx={{ height: '100%', minHeight: 0, display: 'flex', flexDirection: 'column', overflow: 'hidden' }}
      >
        <CardContent sx={{ p: 2, flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', '&:last-child': { pb: 2 } }}>
          <Stack spacing={1.5} sx={{ flex: 1, minHeight: 0 }}>
            <Stack direction="row" alignItems="center" spacing={1} sx={{ flexShrink: 0 }}>
              <Box sx={{ flex: 1, minWidth: 0 }}>
                <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap" useFlexGap>
                  <Typography variant="h6">{t('forensic.findingsTitle')}</Typography>
                  {seekingSummary && (
                    <Chip
                      size="small"
                      color="primary"
                      label={`${t('forensic.seekingLabel')}: ${seekingSummary}`}
                    />
                  )}
                </Stack>
              </Box>
              <Button
                size="small"
                variant="outlined"
                onClick={() => setSearchOpen((open) => !open)}
                endIcon={searchOpen ? <ExpandLessIcon /> : <ExpandMoreIcon />}
              >
                {searchOpen ? t('forensic.hideSearch') : t('forensic.showSearch')}
              </Button>
            </Stack>

            <Collapse
              in={searchOpen}
              sx={{
                flexShrink: 0,
                border: 1,
                borderColor: 'divider',
                borderRadius: 1,
                bgcolor: 'action.hover',
                px: 1,
                pb: 1,
                '& .MuiInputBase-root': { minHeight: 32 },
                '& .MuiInputBase-input': { py: 0.65 },
                '& .MuiFormHelperText-root': { display: 'none' },
              }}
            >
              <Stack spacing={0.75} sx={{ pt: 1 }}>
                <Typography variant="caption" color="text.secondary">{t('forensic.findingsHint')}</Typography>

                <Box>
              <Typography variant="caption" fontWeight={600}>{t('forensic.probeSection')}</Typography>
              <Stack direction={{ xs: 'column', md: 'row' }} spacing={0.75} flexWrap="wrap" useFlexGap sx={{ mt: 0.5 }}>
                <TextField
                  select
                  size="small"
                  label={t('forensic.probeMode')}
                  value={probeMode}
                  onChange={(e) => setProbeMode(e.target.value as ProbeMode)}
                  sx={{ minWidth: 220 }}
                >
                  <MenuItem value="detection">{t('forensic.probeDetection')}</MenuItem>
                  <MenuItem value="archive">{t('forensic.probeArchive')}</MenuItem>
                  <MenuItem value="image">{t('forensic.probeImage')}</MenuItem>
                </TextField>

                {probeMode === 'detection' ? (
                  <TextField
                    size="small"
                    label={t('forensic.detectionId')}
                    value={detectionId}
                    onChange={(e) => setDetectionId(e.target.value)}
                    error={detectionId.length > 0 && !detectionIdValid}
                    helperText={t('forensic.detectionIdHint')}
                    inputProps={{ inputMode: 'numeric' }}
                  />
                ) : probeMode === 'archive' ? (
                  <>
                    <TextField
                      select
                      size="small"
                      label={t('forensic.cameraId')}
                      value={cameraId}
                      onChange={(e) => setCameraId(e.target.value)}
                      sx={{ minWidth: 220 }}
                    >
                      {cameraIds.map((id) => (
                        <MenuItem key={id} value={id}>{cameraLabel(id)}</MenuItem>
                      ))}
                    </TextField>
                    <TextField
                      size="small"
                      label={t('forensic.trackId')}
                      value={trackId}
                      onChange={(e) => setTrackId(e.target.value)}
                      error={trackId.length > 0 && !trackIdValid}
                      inputProps={{ inputMode: 'numeric' }}
                    />
                    <DateTimePicker
                      label={t('forensic.probeTs')}
                      value={ts}
                      onChange={setTs}
                      slotProps={{ textField: { size: 'small' } }}
                    />
                    <TextField
                      select
                      size="small"
                      label={t('forensic.kind')}
                      value={kind}
                      onChange={(e) => {
                        const next = e.target.value as AnalyticsDetectionKind
                        setKind(next)
                        setMinScore(defaultMinScore(next))
                      }}
                      sx={{ minWidth: 160 }}
                    >
                      {ANALYTICS_DETECTION_CLASSES.map((k) => (
                        <MenuItem key={k} value={k}>{t(`analytics.class.${k}`)}</MenuItem>
                      ))}
                    </TextField>
                  </>
                ) : (
                  <>
                    <Button component="label" variant="outlined" size="small">
                      {t('forensic.pickImage')}
                      <input
                        type="file"
                        hidden
                        accept="image/jpeg,image/jpg"
                        onChange={(e) => {
                          const file = e.target.files?.[0]
                          if (!file) return
                          const reader = new FileReader()
                          reader.onload = () => {
                            const value = String(reader.result ?? '')
                            const base64 = value.includes(',') ? value.split(',')[1] ?? '' : ''
                            setImageBase64(base64)
                          }
                          reader.readAsDataURL(file)
                        }}
                      />
                    </Button>
                    <TextField
                      select
                      size="small"
                      label={t('forensic.kind')}
                      value={kind}
                      onChange={(e) => {
                        const next = e.target.value as AnalyticsDetectionKind
                        setKind(next)
                        setMinScore(defaultMinScore(next))
                      }}
                      sx={{ minWidth: 160 }}
                    >
                      {ANALYTICS_DETECTION_CLASSES.map((k) => (
                        <MenuItem key={k} value={k}>{t(`analytics.class.${k}`)}</MenuItem>
                      ))}
                    </TextField>
                    <TextField
                      select
                      size="small"
                      label={t('forensic.detectFirst')}
                      value={detectFirst ? 'yes' : 'no'}
                      onChange={(e) => setDetectFirst(e.target.value === 'yes')}
                      sx={{ minWidth: 160 }}
                    >
                      <MenuItem value="yes">{t('forensic.detectFirstYes')}</MenuItem>
                      <MenuItem value="no">{t('forensic.detectFirstNo')}</MenuItem>
                    </TextField>
                    {imageBase64.length > 0 && (
                      <Chip size="small" color="success" label={t('forensic.imageLoaded')} />
                    )}
                  </>
                )}
              </Stack>
                </Box>

                <Box>
              <Typography variant="caption" fontWeight={600}>{t('forensic.searchSection')}</Typography>
              <Stack direction={{ xs: 'column', md: 'row' }} spacing={0.75} alignItems={{ md: 'flex-start' }} flexWrap="wrap" useFlexGap sx={{ mt: 0.5 }}>
                <DateTimePicker
                  label={t('forensic.fromTs')}
                  value={fromTs}
                  onChange={setFromTs}
                  slotProps={{ textField: { size: 'small', error: !datesValid } }}
                />
                <DateTimePicker
                  label={t('forensic.toTs')}
                  value={toTs}
                  onChange={setToTs}
                  slotProps={{ textField: { size: 'small', error: !datesValid } }}
                />
                <TextField
                  size="small"
                  label={t('forensic.minScore')}
                  type="number"
                  value={minScore}
                  onChange={(e) => setMinScore(Number(e.target.value))}
                  inputProps={{ min: 0, max: 1, step: 0.05 }}
                  helperText={t('forensic.minScoreHint')}
                  sx={{ width: 150 }}
                />
                <TextField
                  size="small"
                  label={t('forensic.limit')}
                  type="number"
                  value={limit}
                  onChange={(e) => setLimit(Math.max(1, Number(e.target.value)))}
                  inputProps={{ min: 1, max: 500 }}
                  sx={{ width: 110 }}
                />
                <TextField
                  select
                  size="small"
                  label={t('forensic.cameraFilter')}
                  value={cameraFilter}
                  onChange={(e) => setCameraFilter(
                    typeof e.target.value === 'string' ? e.target.value.split(',') : e.target.value,
                  )}
                  SelectProps={{
                    multiple: true,
                    renderValue: (selectedIds) => {
                      const ids = selectedIds as string[]
                      return ids.length === 0
                        ? t('forensic.allCameras')
                        : t('forensic.selectedCameras', { count: ids.length })
                    },
                    displayEmpty: true,
                  }}
                  helperText={t('forensic.cameraFilterHint')}
                  sx={{ minWidth: 220 }}
                >
                  {cameraIds.map((id) => (
                    <MenuItem key={id} value={id}>{cameraLabel(id)}</MenuItem>
                  ))}
                </TextField>
                <Button
                  variant="contained"
                  onClick={() => void runSearch()}
                  disabled={loading || !canSearch}
                  sx={{ minWidth: 120, height: 32 }}
                >
                  {loading ? <CircularProgress size={22} color="inherit" /> : t('forensic.search')}
                </Button>
              </Stack>
              {!datesValid && (
                <Typography variant="caption" color="error">{t('forensic.invalidDateRange')}</Typography>
              )}
                </Box>
              </Stack>
            </Collapse>

            {loading && (
              <Alert severity="info" icon={<CircularProgress size={20} />}>{t('forensic.loading')}</Alert>
            )}
            {error && (
              <Alert
                severity="error"
                action={<Button color="inherit" size="small" onClick={() => void runSearch()}>{t('forensic.retry')}</Button>}
              >
                {t('forensic.searchError', { error })}
              </Alert>
            )}
            {!loading && !error && !hasSearched && (
              <Alert severity="info">{t('forensic.ready')}</Alert>
            )}

            {hasSearched && !error && (
              <Stack
                direction={{ xs: 'column', md: 'row' }}
                spacing={2}
                alignItems="stretch"
                sx={{ flex: 1, minHeight: 0, overflow: { xs: 'auto', md: 'hidden' } }}
              >
                {/* Left: matches */}
                <Box sx={{
                  width: { xs: '100%', md: 280 }, flexShrink: 0,
                  display: 'flex', flexDirection: 'column',
                  minHeight: 0,
                }}>
                  <Stack direction="row" spacing={1} alignItems="center" sx={{ mb: 1, flexShrink: 0 }}>
                    <Typography variant="subtitle2" sx={{ flex: 1, minWidth: 0 }}>
                      {filteredGroups.length > 0
                        ? t('forensic.appearanceCount', { count: filteredGroups.length })
                        : t('forensic.noResults')}
                    </Typography>
                    <TextField
                      select
                      size="small"
                      value={resultCameraFilter}
                      onChange={(e) => {
                        const next = e.target.value
                        setResultCameraFilter(next)
                        setResultPage(0)
                        setSelected((next ? groups.filter((g) => g.cameraId === next) : groups)[0] ?? null)
                      }}
                      SelectProps={{ displayEmpty: true }}
                      sx={{ width: 125, '& .MuiInputBase-root': { height: 30 }, '& .MuiSelect-select': { py: 0.5 } }}
                    >
                      <MenuItem value="">{t('forensic.allCameras')}</MenuItem>
                      {resultCameraIds.map((id) => (
                        <MenuItem key={id} value={id}>{cameraLabel(id)}</MenuItem>
                      ))}
                    </TextField>
                  </Stack>
                  <Box sx={{
                    border: 1, borderColor: 'divider', borderRadius: 1,
                    flex: 1, minHeight: 0, height: { xs: 280, md: 'auto' }, overflow: 'auto',
                  }}>
                    <List dense disablePadding>
                      {pageGroups.map((g) => {
                        const low = g.bestScore < minScore + 0.05
                        const span = g.firstTs !== g.lastTs
                        return (
                          <ListItemButton
                            key={g.key}
                            selected={selected?.key === g.key}
                            onClick={() => setSelected(g)}
                            sx={{ display: 'block', borderBottom: 1, borderColor: 'divider', py: 1 }}
                          >
                            <Stack direction="row" spacing={0.5} alignItems="center" flexWrap="wrap" useFlexGap>
                              <Typography variant="body2" fontWeight="medium" noWrap sx={{ flex: 1, minWidth: 0 }}>
                                {cameraLabel(g.cameraId)}
                              </Typography>
                              <Chip
                                label={`${(g.bestScore * 100).toFixed(0)}%`}
                                size="small"
                                color={g.bestScore < 0.7 ? 'default' : 'primary'}
                                sx={{ height: 18, '& .MuiChip-label': { px: 0.75, fontSize: '0.65rem' } }}
                              />
                              {low && (
                                <Chip
                                  size="small"
                                  variant="outlined"
                                  label={t('forensic.lowSimilarity')}
                                  sx={{ height: 18, '& .MuiChip-label': { px: 0.5, fontSize: '0.65rem' } }}
                                />
                              )}
                            </Stack>
                            <Typography variant="caption" color="text.secondary" component="div">
                              {t(`analytics.class.${g.kind}`, g.kind)}
                              {' · #'}
                              {g.trackId}
                              {g.hits.length > 1 ? ` · ${t('forensic.sightings', { count: g.hits.length })}` : ''}
                            </Typography>
                            <Typography variant="caption" color="text.secondary" component="div">
                              {span
                                ? `${new Date(g.firstTs).toLocaleString()} → ${new Date(g.lastTs).toLocaleTimeString()}`
                                : new Date(g.firstTs).toLocaleString()}
                            </Typography>
                          </ListItemButton>
                        )
                      })}
                    </List>
                  </Box>
                  <Stack direction="row" spacing={0.25} alignItems="center" justifyContent="flex-end" sx={{ mt: 0.5, flexShrink: 0 }}>
                    <IconButton
                      size="small"
                      disabled={resultPage === 0}
                      onClick={() => setResultPage(0)}
                    >
                      <FirstPageIcon fontSize="small" />
                    </IconButton>
                    <IconButton
                      size="small"
                      disabled={resultPage === 0}
                      onClick={() => setResultPage((page) => Math.max(0, page - 1))}
                    >
                      <ChevronLeftIcon fontSize="small" />
                    </IconButton>
                    <Typography variant="caption" color="text.secondary" sx={{ minWidth: 34, textAlign: 'center' }}>
                      {resultPage + 1}/{resultPageCount}
                    </Typography>
                    <IconButton
                      size="small"
                      disabled={resultPage + 1 >= resultPageCount}
                      onClick={() => setResultPage((page) => Math.min(resultPageCount - 1, page + 1))}
                    >
                      <ChevronRightIcon fontSize="small" />
                    </IconButton>
                  </Stack>
                </Box>

                {/* Middle: player */}
                <Box sx={{ flex: 2, minWidth: 0, minHeight: 0, width: '100%', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
                  <Stack direction="row" justifyContent="flex-end" sx={{ mb: 1, flexShrink: 0 }}>
                    <OpenCameraInPlayback
                      cameraId={selected?.cameraId ?? null}
                      seekMs={selectedSeekMs}
                    />
                  </Stack>
                  <Box sx={{ flex: 1, minHeight: 0 }}>
                    <DetectionArchivePreview
                      cameraId={previewCameraId}
                      seekMs={selectedSeekMs}
                      streamName={previewStreamName}
                      highlightTrackId={selected?.trackId ?? null}
                      highlightKind={selected?.kind ?? null}
                    />
                  </Box>
                </Box>

                {/* Right: details */}
                <Box sx={{
                  width: { xs: '100%', md: 300 }, flexShrink: 0,
                  minHeight: 0,
                  overflow: 'auto',
                }}>
                  {!selected || !selectedHit ? (
                    <Typography variant="body2" color="text.secondary">{t('archive.selectHint')}</Typography>
                  ) : (
                    <Stack spacing={1.5}>
                      <Typography variant="subtitle2">{t('forensic.hitDetails')}</Typography>
                      {cropUrl && (
                        <Box
                          component="img"
                          src={cropUrl}
                          alt=""
                          sx={{
                            width: '100%',
                            maxHeight: 180,
                            objectFit: 'contain',
                            borderRadius: 1,
                            bgcolor: 'action.hover',
                            outline: '3px solid #FFD54F',
                            outlineOffset: '-1px',
                          }}
                        />
                      )}
                      <DetailRow label={t('forensic.cameraId')} value={cameraLabel(selected.cameraId)} />
                      <DetailRow
                        label={t('forensic.kind')}
                        value={t(`analytics.class.${selected.kind}`, selected.kind)}
                      />
                      <DetailRow label={t('forensic.score')} value={`${(selected.bestScore * 100).toFixed(1)}%`} />
                      <DetailRow
                        label={t('forensic.firstSeen')}
                        value={new Date(selected.firstTs).toLocaleString()}
                      />
                      {selected.firstTs !== selected.lastTs && (
                        <DetailRow
                          label={t('forensic.lastSeen')}
                          value={new Date(selected.lastTs).toLocaleString()}
                        />
                      )}
                      <DetailRow
                        label={t('forensic.sightingsLabel')}
                        value={String(selected.hits.length)}
                      />
                      <DetailRow label={t('forensic.trackId')} value={String(selected.trackId)} />
                      <DetailRow label={t('forensic.detectionId')} value={String(selectedHit.detection.id)} />
                      {selectedHit.detection.objectId && (
                        <DetailRow label={t('events.objectId', 'Object ID')} value={selectedHit.detection.objectId} />
                      )}
                      {selectedHit.detection.plateText && (
                        <DetailRow label={t('analytics.class.license_plate')} value={selectedHit.detection.plateText} />
                      )}
                      <Divider />
                      <OpenCameraInPlayback
                        cameraId={selected.cameraId}
                        seekMs={selectedSeekMs}
                      />
                    </Stack>
                  )}
                </Box>
              </Stack>
            )}
          </Stack>
        </CardContent>
      </Card>
    </LocalizationProvider>
  )
}

function DetailRow({ label, value }: { label: string; value: string }) {
  return (
    <Stack direction="row" spacing={1} justifyContent="space-between">
      <Typography variant="body2" color="text.secondary">{label}</Typography>
      <Typography variant="body2" sx={{ wordBreak: 'break-all', textAlign: 'right' }}>{value}</Typography>
    </Stack>
  )
}
