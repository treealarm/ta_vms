import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Checkbox,
  Chip,
  CircularProgress,
  Divider,
  LinearProgress,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Typography,
} from '@mui/material'
import { DateTimePicker } from '@mui/x-date-pickers/DateTimePicker'
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

  // Search window. The camera's own span is the default rather than seeded state, so it stays
  // correct when the summary arrives after mount; an explicit pick overrides it. The panel is
  // mounted with key={cameraId}, so switching cameras resets everything here for free.
  const [searchFromPick, setSearchFromPick] = useState<Dayjs | null>(null)
  const [searchToPick, setSearchToPick] = useState<Dayjs | null>(null)
  const searchFrom = searchFromPick ?? (dataFrom ? dayjs(dataFrom) : null)
  const searchTo = searchToPick ?? (dataUntil ? dayjs(dataUntil) : null)

  // Import slice — a sub-range of a selected span, so a partial import is possible.
  const [importFrom, setImportFrom] = useState<Dayjs | null>(null)
  const [importTo, setImportTo] = useState<Dayjs | null>(null)
  const [selectedKey, setSelectedKey] = useState<string | null>(null)
  const [importError, setImportError] = useState<string | null>(null)

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

  const selected = collapsed.find((iv) => keyOf(iv) === selectedKey) ?? null

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

  const selectSpan = (iv: CollapsedEdgeInterval) => {
    setSelectedKey(keyOf(iv))
    setImportFrom(iv.startTime ? dayjs(iv.startTime) : null)
    setImportTo(iv.endTime ? dayjs(iv.endTime) : null)
    setImportError(null)
  }

  // The camera seeks by offset from the recording's own start, so that origin has to be exact.
  // Substituting the span start would silently shift the seek and stamp the footage with someone
  // else's wall clock, so an unknown origin blocks the import instead.
  const recordingStart = selected
    ? recordings?.recordings.find((r) => r.recordingToken === selected.recordingToken)?.earliestRecording ?? ''
    : ''

  const runImport = async (exportToFile: boolean) => {
    if (!selected || !importFrom || !importTo || !recordingStart) return
    setImportError(null)

    const action = await dispatch(
      startEdgeImport({
        cameraId,
        request: {
          recordingToken: selected.recordingToken,
          startTime: importFrom.toISOString(),
          endTime: importTo.toISOString(),
          recordingStartTime: recordingStart,
          exportToFile,
        },
      })
    )
    if (startEdgeImport.rejected.match(action)) {
      setImportError(action.error?.message ?? t('edge.importFailed'))
      return
    }
    void dispatch(fetchEdgeImports(cameraId))
  }

  const importInvalid =
    !selected || !importFrom || !importTo || !importTo.isAfter(importFrom) || !recordingStart

  return (
    <Stack spacing={2} sx={{ p: 2, overflowY: 'auto' }}>
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
          <Typography variant="subtitle2" gutterBottom>{t('edge.foundSpans')}</Typography>
          {collapsed.length === 0 ? (
            <Typography variant="body2" color="text.secondary">
              {intervals?.searched ? t('edge.noSpans') : t('edge.notSearchedYet')}
            </Typography>
          ) : (
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell padding="checkbox" />
                  <TableCell>{t('edge.recording')}</TableCell>
                  <TableCell>{t('edge.from')}</TableCell>
                  <TableCell>{t('edge.to')}</TableCell>
                  <TableCell>{t('edge.tracks')}</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {collapsed.map((iv) => (
                  <TableRow
                    key={keyOf(iv)}
                    hover
                    selected={keyOf(iv) === selectedKey}
                    onClick={() => selectSpan(iv)}
                    sx={{ cursor: 'pointer' }}
                  >
                    <TableCell padding="checkbox">
                      <Checkbox size="small" checked={keyOf(iv) === selectedKey} />
                    </TableCell>
                    <TableCell>{iv.recordingToken}</TableCell>
                    <TableCell>{fmt(iv.startTime)}</TableCell>
                    <TableCell>{fmt(iv.endTime)}</TableCell>
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
          )}
        </Box>

        <Divider />

        {/* ── Import slice ── */}
        <Box>
          <Typography variant="subtitle2" gutterBottom>{t('edge.importSlice')}</Typography>
          <Typography variant="caption" color="text.secondary" display="block" gutterBottom>
            {t('edge.importSliceHint')}
          </Typography>
          <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap" useFlexGap>
            <DateTimePicker
              label={t('edge.from')}
              value={importFrom}
              onChange={setImportFrom}
              disabled={!selected}
              slotProps={{ textField: { size: 'small' } }}
            />
            <DateTimePicker
              label={t('edge.to')}
              value={importTo}
              onChange={setImportTo}
              disabled={!selected}
              slotProps={{ textField: { size: 'small' } }}
            />
            <Button
              variant="contained"
              size="small"
              disabled={importInvalid || hasActiveJob}
              onClick={() => void runImport(false)}
            >
              {t('edge.importToArchive')}
            </Button>
            <Button
              variant="outlined"
              size="small"
              disabled={importInvalid || hasActiveJob}
              onClick={() => void runImport(true)}
            >
              {t('edge.downloadMp4')}
            </Button>
          </Stack>
          {hasActiveJob && (
            <Typography variant="caption" color="text.secondary">
              {t('edge.oneImportPerCamera')}
            </Typography>
          )}
          {selected && !recordingStart && (
            <Alert severity="warning" sx={{ mt: 1 }}>{t('edge.unknownRecordingStart')}</Alert>
          )}
          {importError && <Alert severity="error" sx={{ mt: 1 }}>{importError}</Alert>}
        </Box>

        <Divider />

        {/* ── Jobs ── */}
        <Box>
          <Typography variant="subtitle2" gutterBottom>{t('edge.jobs')}</Typography>
          {jobs.length === 0 ? (
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
                      {isImportActive(job) && (
                        <Button
                          size="small"
                          color="warning"
                          onClick={() => void dispatch(cancelEdgeImport({ cameraId, jobId: job.jobId }))}
                        >
                          {t('common.cancel')}
                        </Button>
                      )}
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
                {exports.map((file) => (
                  <TableRow key={file.relPath}>
                    <TableCell>{file.relPath.split('/').pop()}</TableCell>
                    <TableCell>{fmt(file.createdAt)}</TableCell>
                    <TableCell>{formatBytes(file.sizeBytes)}</TableCell>
                    <TableCell>
                      <Button size="small" onClick={() => void downloadExport(file.relPath)}>
                        {t('edge.download')}
                      </Button>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </Box>
        )}
      </LocalizationProvider>
    </Stack>
  )
}

/**
 * Plain navigation would bypass the auth fetch wrapper (no Authorization header), so the file is
 * fetched and handed to a temporary object URL instead.
 */
async function downloadExport(relPath: string): Promise<void> {
  const response = await fetch(`/api/edge-archive/exports/${relPath}`)
  if (!response.ok) return
  const blob = await response.blob()
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = relPath.split('/').pop() ?? 'export.mp4'
  document.body.appendChild(link)
  link.click()
  link.remove()
  URL.revokeObjectURL(url)
}
