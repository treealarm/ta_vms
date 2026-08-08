import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Checkbox,
  Chip,
  CircularProgress,
  Divider,
  LinearProgress,
  Slider,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Typography,
} from '@mui/material'
import { DateTimePicker } from '@mui/x-date-pickers/DateTimePicker'
import { DateTimeField } from '@mui/x-date-pickers/DateTimeField'
import { LocalizationProvider } from '@mui/x-date-pickers/LocalizationProvider'
import { AdapterDayjs } from '@mui/x-date-pickers/AdapterDayjs'
import dayjs, { type Dayjs } from 'dayjs'
import { useTranslation } from 'react-i18next'

import { useAppDispatch, useAppSelector } from '../store'
import {
  cancelEdgeImport,
  fetchEdgeExports,
  fetchEdgeImports,
  fetchEdgeIntervals,
  fetchEdgeRecordings,
  startEdgeImport,
} from '../store/edgeArchiveSlice'
import EdgeMediaPreview, { type EdgePreviewTarget } from './EdgeMediaPreview'
import type { EdgeExportLinkDto } from '../dto/dtos'
import {
  collapseIntervalsByRecordingSpan,
  formatBytes,
  importProgressFraction,
  isImportActive,
  type CollapsedEdgeInterval,
} from './edgeArchiveModel'
const IMPORT_POLL_MS = 2000

// Same shape as RecordingsList uses, so device timestamps read like archive ones.
function fmt(iso: string | null | undefined): string {
  if (!iso) return '—'
  const d = dayjs(iso)
  if (!d.isValid()) return iso
  return d.toDate().toLocaleString(undefined, {
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false,
  })
}

/**
 * Picks a sub-range of one span: a two-thumb slider over the span's own length, with time fields for
 * exact correction. Everything is an offset inside the span, so a value outside what the camera
 * reported cannot be expressed — the earlier calendar let any date at all be chosen.
 */
function SpanRangeEditor({
  span, durationSec, offsets, range, onChange,
}: {
  span: CollapsedEdgeInterval
  durationSec: number
  offsets: { fromSec: number; toSec: number }
  range: { from: Dayjs | null; to: Dayjs | null }
  onChange: (fromSec: number, toSec: number) => void
}) {
  const { t } = useTranslation()
  const start = span.startTime ? dayjs(span.startTime) : null

  // The field carries a full timestamp, not just a time: a span can start yesterday and end today,
  // and then a time alone cannot say which day is meant. The value is read back as an offset from
  // the span's start and clamped by the caller, so a date outside the span still cannot be chosen.
  const offsetFromValue = (value: Dayjs | null, fallback: number) => {
    if (!value || !value.isValid() || !start) return fallback
    return value.diff(start, 'second')
  }

  return (
    <Stack spacing={0.5} sx={{ py: 0.5 }}>
      <Slider
        size="small"
        min={0}
        max={durationSec || 1}
        value={[offsets.fromSec, offsets.toSec]}
        onChange={(_, v) => Array.isArray(v) && onChange(v[0], v[1])}
        disableSwap
        valueLabelDisplay="auto"
        valueLabelFormat={(v) => {
          if (!start) return String(v)
          const at = start.add(v, 'second')
          // Show the date too once the span crosses midnight, or the label is ambiguous.
          return at.isSame(start, 'day') ? at.format('HH:mm:ss') : at.format('MM-DD HH:mm:ss')
        }}
        sx={{ mx: 1, width: 'calc(100% - 16px)' }}
      />
      <Stack direction="row" spacing={1} alignItems="center">
        <DateTimeField
          label={t('edge.from')}
          value={range.from}
          onChange={(v) => onChange(offsetFromValue(v, offsets.fromSec), offsets.toSec)}
          format="YYYY-MM-DD HH:mm:ss"
          ampm={false}
          size="small"
          sx={{ width: 190 }}
        />
        <DateTimeField
          label={t('edge.to')}
          value={range.to}
          onChange={(v) => onChange(offsets.fromSec, offsetFromValue(v, offsets.toSec))}
          format="YYYY-MM-DD HH:mm:ss"
          ampm={false}
          size="small"
          sx={{ width: 190 }}
        />
        <Typography variant="caption" color="text.secondary">
          {formatDuration(offsets.toSec - offsets.fromSec)}
        </Typography>
      </Stack>
    </Stack>
  )
}

function formatDuration(seconds: number): string {
  const s = Math.max(0, Math.round(seconds))
  const hh = Math.floor(s / 3600)
  const mm = Math.floor((s % 3600) / 60)
  const ss = s % 60
  return hh > 0 ? `${hh}h ${mm}m` : mm > 0 ? `${mm}m ${ss}s` : `${ss}s`
}

type Props = {
  cameraId: string
  /** Camera's own archive span, used to seed the search window. */
  dataFrom: string
  dataUntil: string
}

export default function EdgeArchiveBrowsePanel({ cameraId, dataFrom, dataUntil }: Props) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const intervals = useAppSelector((s) => s.edgeArchive.intervals)
  const recordings = useAppSelector((s) => s.edgeArchive.recordings)
  const jobs = useAppSelector((s) => s.edgeArchive.jobs)
  const exports = useAppSelector((s) => s.edgeArchive.exports)
  const isBusy = useAppSelector((s) => s.edgeArchive.isBusy)
  const status = useAppSelector((s) => s.edgeArchive.status)
  const importsError = useAppSelector((s) => s.edgeArchive.importsError)

  // Search window. The camera's own span is the default rather than seeded state, so it stays
  // correct when the summary arrives after mount; an explicit pick overrides it. The panel is
  // mounted with key={cameraId}, so switching cameras resets everything here for free.
  const [searchFromPick, setSearchFromPick] = useState<Dayjs | null>(null)
  const [searchToPick, setSearchToPick] = useState<Dayjs | null>(null)
  const searchFrom = searchFromPick ?? (dataFrom ? dayjs(dataFrom) : null)
  const searchTo = searchToPick ?? (dataUntil ? dayjs(dataUntil) : null)

  const [selectedKeys, setSelectedKeys] = useState<string[]>([])
  // Narrowing is stored as second offsets from the span's start, not as absolute timestamps. The
  // choice is always inside one span, so a date cannot be meaningful here — and expressing it as an
  // offset makes "picked a day far outside the span" unrepresentable rather than merely validated.
  const [ranges, setRanges] = useState<Record<string, { fromSec: number; toSec: number }>>({})
  const [importError, setImportError] = useState<string | null>(null)
  // What the inline player is showing, if anything.
  const [preview, setPreview] = useState<EdgePreviewTarget | null>(null)
  // Index of the span currently being pulled, so a queue of them shows progress.
  const [queuePosition, setQueuePosition] = useState<{ index: number; total: number } | null>(null)
  // Set when the operator cancels a running job, to stop the queue instead of starting the next one.
  const abortQueue = useRef(false)

  useEffect(() => {
    void dispatch(fetchEdgeRecordings(cameraId))
    void dispatch(fetchEdgeImports(cameraId))
    void dispatch(fetchEdgeExports(cameraId))
  }, [dispatch, cameraId])

  const hasActiveJob = jobs.some(isImportActive)

  // Poll only while something is actually running; a finished list is static.
  useEffect(() => {
    if (!hasActiveJob) return
    const timer = window.setInterval(() => {
      void dispatch(fetchEdgeImports(cameraId))
      void dispatch(fetchEdgeExports(cameraId))
    }, IMPORT_POLL_MS)
    return () => window.clearInterval(timer)
  }, [dispatch, cameraId, hasActiveJob])

  const collapsed = useMemo(
    () => collapseIntervalsByRecordingSpan(intervals?.intervals ?? []),
    [intervals]
  )

  const keyOf = (iv: CollapsedEdgeInterval) =>
    `${iv.recordingToken}|${iv.startTime}|${iv.endTime}`

  // Table order, not click order: the queue below imports oldest-first, which is what an operator
  // patching a stretch of archive expects.
  const selectedSpans = collapsed.filter((iv) => selectedKeys.includes(keyOf(iv)))

  const search = useCallback(() => {
    void dispatch(
      fetchEdgeIntervals({
        cameraId,
        from: searchFrom?.toISOString(),
        to: searchTo?.toISOString(),
        maxResults: 200,
      })
    )
  }, [dispatch, cameraId, searchFrom, searchTo])

  // The camera's whole span is the window an operator would ask for first, so run that search as
  // soon as the summary reports it rather than making the first look a two-step ritual. Tracked in
  // a ref, not state, so it cannot loop: it fires once per (camera, span), and never once the
  // operator has picked a window of their own.
  const autoSearchedFor = useRef('')
  useEffect(() => {
    if (!dataFrom || !dataUntil) return
    if (searchFromPick || searchToPick) return
    const key = `${cameraId}|${dataFrom}|${dataUntil}`
    if (autoSearchedFor.current === key) return
    autoSearchedFor.current = key
    void dispatch(fetchEdgeIntervals({ cameraId, from: dataFrom, to: dataUntil, maxResults: 200 }))
  }, [dispatch, cameraId, dataFrom, dataUntil, searchFromPick, searchToPick])

  const toggleSpan = (iv: CollapsedEdgeInterval) => {
    const key = keyOf(iv)
    setSelectedKeys(
      selectedKeys.includes(key) ? selectedKeys.filter((k) => k !== key) : [...selectedKeys, key]
    )
    setImportError(null)
  }

  const toggleAll = () => {
    setSelectedKeys(selectedKeys.length === collapsed.length ? [] : collapsed.map(keyOf))
    setImportError(null)
  }

  /** Length of what ONVIF says the span holds, in seconds. */
  const spanSeconds = (iv: CollapsedEdgeInterval) =>
    iv.startTime && iv.endTime ? Math.max(0, dayjs(iv.endTime).diff(dayjs(iv.startTime), 'second')) : 0

  /** Selected offsets, defaulting to the whole span. */
  const offsetsOf = (iv: CollapsedEdgeInterval) =>
    ranges[keyOf(iv)] ?? { fromSec: 0, toSec: spanSeconds(iv) }

  const setOffsets = (iv: CollapsedEdgeInterval, fromSec: number, toSec: number) => {
    const max = spanSeconds(iv)
    const lo = Math.min(Math.max(0, Math.round(fromSec)), max)
    const hi = Math.min(Math.max(0, Math.round(toSec)), max)
    // Clamped rather than rejected: dragging or typing past an edge should stop at it, not blank the
    // field and block the import.
    setRanges((prev) => ({ ...prev, [keyOf(iv)]: { fromSec: Math.min(lo, hi), toSec: Math.max(lo, hi) } }))
    setImportError(null)
  }

  /** What actually gets imported for a span, as wall clock. */
  const effectiveRange = (iv: CollapsedEdgeInterval) => {
    if (!iv.startTime) return { from: null, to: null }
    const start = dayjs(iv.startTime)
    const { fromSec, toSec } = offsetsOf(iv)
    return { from: start.add(fromSec, 'second'), to: start.add(toSec, 'second') }
  }

  /** Origin the camera seeks from for a span's recording; empty when it never reported one. */
  const recordingStartOf = (iv: CollapsedEdgeInterval) =>
    recordings?.recordings.find((r) => r.recordingToken === iv.recordingToken)?.earliestRecording ?? ''

  // The camera seeks by offset from the recording's own start, so that origin has to be exact.
  // Substituting the span start would silently shift the seek and stamp the footage with someone
  // else's wall clock, so an unknown origin blocks the import instead.
  const missingRecordingStart = selectedSpans.some((iv) => !recordingStartOf(iv))
  // Only a genuinely reversed range blocks the import; a cleared field just means the whole span.
  const hasInvalidRange = selectedSpans.some((iv) => {
    const { from, to } = effectiveRange(iv)
    return !from || !to || !to.isAfter(from)
  })

  /** Waits for one job to leave `running`. Returns false if it failed or the queue was aborted. */
  const awaitJob = async (jobId: string): Promise<boolean> => {
    // The device only ever runs one import at a time, so the next span cannot be started until this
    // one is done — polling the list is how we learn that.
    for (;;) {
      if (abortQueue.current) return false
      await new Promise((resolve) => setTimeout(resolve, IMPORT_POLL_MS))
      const action = await dispatch(fetchEdgeImports(cameraId))
      if (!fetchEdgeImports.fulfilled.match(action)) return false
      const job = action.payload.find((j) => j.jobId === jobId)
      if (!job) return false
      if (job.state === 'running') continue
      if (job.state === 'completed') return true
      setImportError(job.message || t('edge.importFailed'))
      return false
    }
  }

  const runImport = async (exportToFile: boolean) => {
    if (selectedSpans.length === 0) return
    setImportError(null)
    abortQueue.current = false

    const queue = selectedSpans.flatMap((iv) => {
      const { from, to } = effectiveRange(iv)
      if (!from || !to) return []
      return [{
        recordingToken: iv.recordingToken,
        recordingStartTime: recordingStartOf(iv),
        startTime: from.toISOString(),
        endTime: to.toISOString(),
      }]
    })

    try {
      for (let i = 0; i < queue.length; i++) {
        if (abortQueue.current) break
        setQueuePosition({ index: i + 1, total: queue.length })

        const action = await dispatch(
          startEdgeImport({ cameraId, request: { ...queue[i], exportToFile } })
        )
        if (startEdgeImport.rejected.match(action)) {
          setImportError(action.error?.message ?? t('edge.importFailed'))
          break
        }
        // Only wait when more spans follow; the last one can finish in the background.
        if (i < queue.length - 1 && !(await awaitJob(action.payload.jobId))) break
      }
    } finally {
      setQueuePosition(null)
      void dispatch(fetchEdgeImports(cameraId))
      void dispatch(fetchEdgeExports(cameraId))
    }
  }

  // One place that decides whether an import can start, and says why not. Split across the button's
  // `disabled` and a few scattered hints, "the button is greyed out and I cannot tell why" was the
  // normal experience.
  const importBlockedReason =
    importsError ? t('edge.importsUnavailable', { error: importsError })
    : queuePosition ? t('edge.importingQueue', queuePosition)
    : hasActiveJob ? t('edge.oneImportPerCamera')
    : selectedSpans.length === 0 ? t('edge.selectSpanFirst')
    : missingRecordingStart ? t('edge.unknownRecordingStart')
    : hasInvalidRange ? t('edge.invalidRange')
    : ''

  const importInvalid = Boolean(importBlockedReason)

  return (
    // Two columns on purpose: the left one is long (search, spans, jobs, exports) and the player used
    // to sit under all of it, off screen. Beside the lists, whatever is picked plays where it can
    // actually be seen.
    <Box sx={{ display: 'flex', height: '100%', minHeight: 0 }}>
      <Stack spacing={2} sx={{ flex: 1, minWidth: 0, p: 2, overflowY: 'auto' }}>
        <LocalizationProvider dateAdapter={AdapterDayjs}>
          {/* ── Search window ── */}
          <Box>
            <Typography variant="subtitle2" gutterBottom>{t('edge.searchWindow')}</Typography>
            <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap" useFlexGap>
              <DateTimePicker
                label={t('edge.from')}
                value={searchFrom}
                onChange={setSearchFromPick}
                slotProps={{ textField: { size: 'small' } }}
              />
              <DateTimePicker
                label={t('edge.to')}
                value={searchTo}
                onChange={setSearchToPick}
                slotProps={{ textField: { size: 'small' } }}
              />
              <Button variant="contained" size="small" onClick={search} disabled={isBusy}>
                {t('edge.search')}
              </Button>
              {isBusy && <CircularProgress size={18} />}
            </Stack>
            <Typography variant="caption" color="text.secondary">
              {t('edge.onBoard', { from: fmt(dataFrom), to: fmt(dataUntil) })}
            </Typography>
          </Box>

          {status && <Alert severity="error">{status}</Alert>}
          {intervals?.message && <Alert severity="warning">{intervals.message}</Alert>}

          {/* ── Found spans ── */}
          <Box>
            <Typography variant="subtitle2">{t('edge.foundSpans')}</Typography>
            <Typography variant="caption" color="text.secondary" display="block" gutterBottom>
              {t('edge.tableRangeHint')}
            </Typography>
            {collapsed.length === 0 ? (
              <Typography variant="body2" color="text.secondary">
                {intervals?.searched ? t('edge.noSpans') : t('edge.notSearchedYet')}
              </Typography>
            ) : (
              // The range editor column is wide and the player takes a fixed share of the width, so
              // the table scrolls on its own rather than pushing the page sideways.
              <Box sx={{ overflowX: 'auto' }}>
              <Table size="small" sx={{ minWidth: 720 }}>
                <TableHead>
                  <TableRow>
                    <TableCell padding="checkbox">
                      <Checkbox
                        size="small"
                        checked={collapsed.length > 0 && selectedKeys.length === collapsed.length}
                        indeterminate={selectedKeys.length > 0 && selectedKeys.length < collapsed.length}
                        onChange={toggleAll}
                      />
                    </TableCell>
                    <TableCell>{t('edge.recording')}</TableCell>
                    <TableCell>{t('edge.availableFrom')}</TableCell>
                    <TableCell>{t('edge.availableTo')}</TableCell>
                    <TableCell sx={{ minWidth: 320 }}>{t('edge.importRange')}</TableCell>
                    <TableCell>{t('edge.tracks')}</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {collapsed.map((iv) => (
                    <TableRow key={keyOf(iv)} hover selected={selectedKeys.includes(keyOf(iv))}>
                      <TableCell padding="checkbox">
                        <Checkbox
                          size="small"
                          checked={selectedKeys.includes(keyOf(iv))}
                          onChange={() => toggleSpan(iv)}
                        />
                      </TableCell>
                      <TableCell>{iv.recordingToken}</TableCell>
                      {/* What ONVIF reports the span holds — read-only, the reference for the choice. */}
                      <TableCell>{fmt(iv.startTime)}</TableCell>
                      <TableCell>{fmt(iv.endTime)}</TableCell>
                      <TableCell>
                        {selectedKeys.includes(keyOf(iv)) ? (
                          <SpanRangeEditor
                            span={iv}
                            durationSec={spanSeconds(iv)}
                            offsets={offsetsOf(iv)}
                            range={effectiveRange(iv)}
                            onChange={(from, to) => setOffsets(iv, from, to)}
                          />
                        ) : (
                          <Typography variant="caption" color="text.secondary">
                            {t('edge.wholeSpan')}
                          </Typography>
                        )}
                      </TableCell>
                      <TableCell>
                        <Stack direction="row" spacing={0.5} flexWrap="wrap" useFlexGap>
                          {iv.trackTokens.map((tk) => (
                            <Chip key={tk} label={tk} size="small" variant="outlined" />
                          ))}
                        </Stack>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              </Box>
            )}
          </Box>

          <Divider />

          {/* ── Import ── */}
          <Box>
            <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap" useFlexGap>
              <Button
                variant="contained"
                size="small"
                disabled={importInvalid}
                onClick={() => void runImport(false)}
              >
                {t('edge.importToArchive')}
              </Button>
              <Button
                variant="outlined"
                size="small"
                disabled={importInvalid}
                onClick={() => void runImport(true)}
              >
                {t('edge.downloadMp4')}
              </Button>
              {queuePosition && <CircularProgress size={18} />}
            </Stack>
            {importBlockedReason && (
              <Alert severity="info" sx={{ mt: 1 }}>{importBlockedReason}</Alert>
            )}
            {importError && <Alert severity="error" sx={{ mt: 1 }}>{importError}</Alert>}
          </Box>

          <Divider />

          {/* ── Jobs ── */}
          <Box>
            <Typography variant="subtitle2" gutterBottom>{t('edge.jobs')}</Typography>
            {importsError ? (
              <Alert severity="warning">{t('edge.importsUnavailable', { error: importsError })}</Alert>
            ) : jobs.length === 0 ? (
              <Typography variant="body2" color="text.secondary">{t('edge.noJobs')}</Typography>
            ) : (
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell>{t('edge.jobState')}</TableCell>
                    <TableCell>{t('edge.jobWindow')}</TableCell>
                    <TableCell>{t('edge.jobProgress')}</TableCell>
                    <TableCell>{t('edge.jobResult')}</TableCell>
                    <TableCell />
                  </TableRow>
                </TableHead>
                <TableBody>
                  {jobs.map((job) => (
                    <TableRow key={job.jobId}>
                      <TableCell>
                        <Chip
                          size="small"
                          label={t(`edge.state.${job.state}`)}
                          color={
                            job.state === 'completed' ? 'success'
                              : job.state === 'failed' ? 'error'
                              : job.state === 'running' ? 'info' : 'default'
                          }
                        />
                      </TableCell>
                      <TableCell>{fmt(job.startTime)} — {fmt(job.endTime)}</TableCell>
                      <TableCell sx={{ minWidth: 140 }}>
                        <LinearProgress
                          variant="determinate"
                          value={importProgressFraction(job) * 100}
                        />
                      </TableCell>
                      <TableCell>
                        {job.exportPath
                          ? formatBytes(job.bytesWritten)
                          : t('edge.clipsWritten', { count: job.clipsWritten })}
                        {job.message && (
                          <Typography variant="caption" display="block" color="text.secondary">
                            {job.message}
                          </Typography>
                        )}
                      </TableCell>
                      <TableCell>
                        {isImportActive(job) ? (
                          <Button
                            size="small"
                            color="warning"
                            onClick={() => {
                              abortQueue.current = true
                              void dispatch(cancelEdgeImport({ cameraId, jobId: job.jobId }))
                            }}
                          >
                            {t('common.cancel')}
                          </Button>
                        ) : job.state === 'completed' && job.startTime && job.endTime ? (
                          <Button
                            size="small"
                            onClick={() => setPreview(
                              job.exportPath
                                ? { kind: 'file', relPath: job.exportPath, label: job.exportPath }
                                : {
                                    kind: 'archive',
                                    cameraId,
                                    from: job.startTime!,
                                    to: job.endTime!,
                                    label: `${fmt(job.startTime)} — ${fmt(job.endTime)}`,
                                  }
                            )}
                          >
                            {t('edge.play')}
                          </Button>
                        ) : null}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            )}
          </Box>

          {/* ── Exports ── */}
          {exports.length > 0 && (
            <Box>
              <Typography variant="subtitle2" gutterBottom>{t('edge.exports')}</Typography>
              <Table size="small">
                <TableBody>
                  {exports.map((file) => {
                    const active = preview?.kind === 'file' && preview.relPath === file.relPath
                    return (
                      <TableRow
                        key={file.relPath}
                        hover
                        selected={active}
                        sx={{ cursor: 'pointer' }}
                        onClick={() => setPreview({
                          kind: 'file', relPath: file.relPath, label: file.relPath,
                        })}
                      >
                        <TableCell>{file.relPath.split('/').pop()}</TableCell>
                        <TableCell>{fmt(file.createdAt)}</TableCell>
                        <TableCell>{formatBytes(file.sizeBytes)}</TableCell>
                        <TableCell>
                          <Button
                            size="small"
                            onClick={(e) => { e.stopPropagation(); void downloadExport(file.relPath) }}
                          >
                            {t('edge.download')}
                          </Button>
                        </TableCell>
                      </TableRow>
                    )
                  })}
                </TableBody>
              </Table>
            </Box>
          )}
        </LocalizationProvider>
      </Stack>

      <Box sx={{ width: 440, flexShrink: 0, borderLeft: 1, borderColor: 'divider', p: 2, overflowY: 'auto' }}>
        {preview ? (
          <>
            <Stack direction="row" spacing={1} alignItems="center" sx={{ mb: 1 }}>
              <Typography variant="subtitle2" sx={{ flex: 1 }}>{t('edge.preview')}</Typography>
              <Button size="small" onClick={() => setPreview(null)}>{t('common.close')}</Button>
            </Stack>
            <EdgeMediaPreview target={preview} />
          </>
        ) : (
          <Typography variant="body2" color="text.secondary">
            {t('edge.previewPlaceholder')}
          </Typography>
        )}
      </Box>
    </Box>
  )
}

/**
 * Plain navigation bypasses the auth fetch wrapper, so a short-lived signed URL is minted first and
 * the browser downloads straight from it — no buffering of a multi-hundred-megabyte file in memory.
 */
async function downloadExport(relPath: string): Promise<void> {
  const response = await fetch(
    `/api/edge-archive/export-link?relPath=${encodeURIComponent(relPath)}`,
    { method: 'POST' }
  )
  if (!response.ok) return
  const { url } = (await response.json()) as EdgeExportLinkDto
  const link = document.createElement('a')
  link.href = `${url}&download=true`
  link.download = relPath.split('/').pop() ?? 'export.mp4'
  document.body.appendChild(link)
  link.click()
  link.remove()
}
