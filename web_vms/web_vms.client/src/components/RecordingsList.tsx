import { forwardRef, useEffect, useImperativeHandle, useMemo, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import {
  alpha, Box, CircularProgress, IconButton, List, ListItemButton, ListItemText,
  ListSubheader, MenuItem, Popover, Select, Stack, Tooltip, Typography, useTheme,
} from '@mui/material'
import NavigateBeforeIcon from '@mui/icons-material/NavigateBefore'
import NavigateNextIcon from '@mui/icons-material/NavigateNext'
import CalendarMonthIcon from '@mui/icons-material/CalendarMonth'
import LayersIcon from '@mui/icons-material/Layers'
import { type RecordingDto, type RecordingRangeDto } from '../dto/dtos'
import { fetchRecordingRanges } from '../store/fsSlice'

// How many ranges one page holds.
// Module scope, and emphatically not a fresh [] per render: this value is handed to onRangesChange
// as an identity, and a new array each render made the consumer treat "still loading" as "the
// ranges changed" on every paint — which in ArchiveHlsStreamCard resets the selection back to the
// oldest range on the page.
const EMPTY_RANGES: RecordingRangeDto[] = []

const PAGE_LIMIT = 50
import { useAppDispatch, useAppSelector } from '../store'
import dayjs, { Dayjs } from 'dayjs'
import ArchiveDateTimePicker from './ArchiveDateTimePicker'

function fmtTime(iso: string) {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false })
}
function fmtFull(iso: string) {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toLocaleString(undefined, { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false })
}
function fmtDay(iso: string) {
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? '' : d.toLocaleDateString('sv-SE')
}
function normalizeTs(value?: string) {
  if (!value) return new Date().toISOString()
  const p = new Date(value)
  if (isNaN(p.getTime()) || p.getUTCFullYear() <= 1970) return new Date().toISOString()
  return p.toISOString()
}

export type RecordingsListHandle = {
  prev: () => void
  next: () => void
  openCalendar: (anchor: HTMLElement) => void
}

type Props = {
  open: boolean
  cameraId: string
  timestamp?: string
  filePath?: string
  onSelect: (rec: RecordingDto) => void
  onTimeChange?: (iso: string) => void
  onRangesChange?: (ranges: RecordingRangeDto[]) => void
  playbackStartTime?: string
  dark?: boolean
}

const RecordingsList = forwardRef<RecordingsListHandle, Props>(function RecordingsList(
  { open, cameraId, timestamp, filePath, onSelect, onTimeChange, onRangesChange, playbackStartTime, dark = false },
  ref
) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const [selected, setSelected] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [pickerTime, setPickerTime] = useState<Dayjs | null>(dayjs())
  const [displayTime, setDisplayTime] = useState(normalizeTs(timestamp))
  const [queryTime, setQueryTime] = useState<string | undefined>()
  const [loaded, setLoaded] = useState(false)
  const [pickerAnchor, setPickerAnchor] = useState<HTMLElement | null>(null)
  const [streamFilter, setStreamFilter] = useState('')

  const theme = useTheme()
  const W = theme.palette.common.white
  const txt = dark ? alpha(W, 0.85) : undefined
  const txtDim = dark ? alpha(W, 0.45) : 'text.secondary'
  const divider = dark ? alpha(W, 0.1) : alpha(theme.palette.common.black, 0.1)
  const btnSx = dark
    ? { p: '3px', color: alpha(W, 0.7), '&:hover': { color: W, bgcolor: alpha(W, 0.1) } }
    : { p: '4px' }

  useEffect(() => { setDisplayTime(normalizeTs(timestamp)) }, [timestamp])

  useEffect(() => {
    if (open) {
      const t = normalizeTs(timestamp)
      setPickerTime(dayjs(t))
      setQueryTime(t)
      setLoaded(false)
    }
  }, [open, timestamp])

  useEffect(() => {
    if (!open || !queryTime || loaded) return
    let cancelled = false
    const load = async () => {
      setLoading(true)
      // A page, not the whole archive: the server centres this many ranges on the anchor, and the
      // arrows above move the anchor to reach the rest. It used to ask for 200 because the server
      // truncated from the oldest end and hid everything recent — a day of short gaps and their
      // imports was enough to lose the last hour.
      await dispatch(fetchRecordingRanges({ cameraId, timestamp: queryTime, limit: PAGE_LIMIT }))
      if (!cancelled) { setLoading(false); setLoaded(true) }
    }
    void load()
    return () => { cancelled = true }
  }, [cameraId, dispatch, loaded, open, queryTime])

  const recordingsKey = queryTime ? `${cameraId}:${queryTime}` : ''
  const data = useAppSelector((s) => s.fs.recordingRangesByKey[recordingsKey]?.ranges ?? EMPTY_RANGES)

  useEffect(() => { onRangesChange?.(data) }, [data, onRangesChange])

  const streamNames = useMemo(() => {
    const names = new Set<string>()
    data.forEach((r) => r.recordings.forEach((rec) => { if (rec.streamName) names.add(rec.streamName) }))
    return [...names].sort()
  }, [data])

  useEffect(() => {
    if (streamFilter && !streamNames.includes(streamFilter)) setStreamFilter('')
  }, [streamFilter, streamNames])

  const filteredData = useMemo(
    () => streamFilter ? data.filter((r) => r.recordings.some((rec) => rec.streamName === streamFilter)) : data,
    [data, streamFilter]
  )

  useEffect(() => {
    if (playbackStartTime) setSelected(playbackStartTime)
  }, [playbackStartTime])

  // After a jump (calendar/timeline pick, prev/next, initial open) the freshly loaded
  // list is centered on displayTime, but renders with the scroll position at the top —
  // bring the clip nearest to displayTime into view instead of leaving it off-screen.
  const listRef = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!loaded || filteredData.length === 0) return
    const target = new Date(displayTime).getTime()
    let nearestStart: string | undefined
    let minDelta = Infinity
    for (const r of filteredData) {
      const start = r.recordings[0]?.startTime
      if (!start) continue
      const delta = Math.abs(new Date(start).getTime() - target)
      if (delta < minDelta) { minDelta = delta; nearestStart = start }
    }
    if (!nearestStart) return
    setSelected(nearestStart)
    const el = listRef.current?.querySelector<HTMLElement>(`[data-start="${CSS.escape(nearestStart)}"]`)
    el?.scrollIntoView({ block: 'center' })
  }, [loaded, filteredData, displayTime])

  // Which way we last paged and what the edge was when we did. The server centres its window on the
  // anchor and clamps it to the archive, so asking past the last range legitimately hands back the
  // same page — comparing the edge afterwards is the only way to tell "the archive ends here" from
  // "the button did nothing", and an operator must not have to guess which.
  const pagingRef = useRef<{ dir: 'earlier' | 'later'; edge: string } | null>(null)
  const [atEarliest, setAtEarliest] = useState(false)
  const [atLatest, setAtLatest] = useState(false)

  const requestAt = (time: string) => {
    const d = dayjs(time)
    if (!d.isValid()) return
    const iso = d.toISOString()
    // A new anchor makes both edges unknown again; the effect below re-establishes them.
    setAtEarliest(false); setAtLatest(false)
    setQueryTime(iso); setPickerTime(d); setDisplayTime(iso); setLoaded(false)
    onTimeChange?.(iso)
  }

  const edgesOf = (ranges: typeof data) => {
    const lastClips = ranges[ranges.length - 1]?.recordings
    const last = lastClips?.[lastClips.length - 1]
    return {
      first: ranges[0]?.recordings[0]?.startTime,
      // An unfinished clip has no end yet; its start is the latest instant the archive can vouch for.
      last: last?.endTime ?? last?.startTime,
    }
  }

  useEffect(() => {
    if (!loaded) return
    const pending = pagingRef.current
    if (!pending) return
    pagingRef.current = null

    const edges = edgesOf(data)
    const now = pending.dir === 'earlier' ? edges.first : edges.last
    // The page did not move, so there is nothing beyond it in that direction.
    if (now === pending.edge) {
      if (pending.dir === 'earlier') setAtEarliest(true)
      else setAtLatest(true)
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [loaded, data])

  const commitPickerTime = (value: Dayjs) => {
    requestAt(value.toISOString())
    setPickerAnchor(null)
  }

  // Paging moves the anchor past the edge of what is on screen, by a millisecond: the server returns
  // a window centred on it, so anchoring exactly on the edge would hand back the same page. The
  // boundary range stays visible at the far end, which is what makes a jump readable on a timeline.
  const handlePrev = () => {
    const edge = edgesOf(data).first
    if (!edge) return
    pagingRef.current = { dir: 'earlier', edge }
    requestAt(new Date(new Date(edge).getTime() - 1).toISOString())
  }
  const handleNext = () => {
    const edge = edgesOf(data).last
    if (!edge) return
    pagingRef.current = { dir: 'later', edge }
    requestAt(new Date(new Date(edge).getTime() + 1).toISOString())
  }
  const openCalendar = (anchor: HTMLElement) => {
    setPickerTime(dayjs(displayTime))
    setPickerAnchor(anchor)
  }

  useImperativeHandle(ref, () => ({ prev: handlePrev, next: handleNext, openCalendar }))

  if (!open) return null

  // The list runs oldest-first, so paging reads the way the list does: "Earlier" continues past the
  // top, "Later" past the bottom. The header keeps its arrows, but they sit next to the calendar
  // where they read as decoration — an operator scrolling to the end of a page has no reason to
  // look back up, and would sooner believe the archive ends there.
  //
  // A plain function, not a component: declared inside the body it would be a new component type on
  // every render, and React would tear down and rebuild the buttons each time — losing focus, and
  // losing a click that happened to straddle a render.
  const renderPageButton = (dir: 'earlier' | 'later') => {
    // Only "paging that way already came back unchanged" is sound. A short page proves nothing:
    // the server fetches limit*10 CLIPS and only then groups them into ranges, so a long continuous
    // recording legitimately yields a handful of ranges with plenty of archive on either side.
    const exhausted = dir === 'earlier' ? atEarliest : atLatest

    return (
      <ListItemButton
        onClick={dir === 'earlier' ? handlePrev : handleNext}
        disabled={exhausted}
        sx={{
          justifyContent: 'center', py: 0.4,
          ...(dark ? { '&:hover': { bgcolor: alpha(W, 0.08) } } : {}),
        }}
      >
        <Typography
          component="span"
          sx={{ fontSize: '0.68rem', color: txtDim, display: 'flex', alignItems: 'center', gap: 0.3 }}
        >
          {exhausted
            ? t('recordings.archiveEdge')
            : dir === 'earlier'
              ? <><NavigateBeforeIcon sx={{ fontSize: '0.9rem' }} />{t('recordings.earlier')}</>
              : <>{t('recordings.later')}<NavigateNextIcon sx={{ fontSize: '0.9rem' }} /></>}
        </Typography>
      </ListItemButton>
    )
  }

  const shownDate = new Date(displayTime).toLocaleDateString('sv-SE')
  const shownTime = new Date(displayTime).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false })

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0 }}>

      {/* Navigation row */}
      <Stack direction="row" alignItems="center" sx={{ px: 0.75, py: 0.5, borderBottom: `1px solid ${divider}`, flexShrink: 0, gap: 0.25 }}>
        <Tooltip title={t('recordings.earlier')}>
          <IconButton size="small" onClick={handlePrev} sx={btnSx}>
            <NavigateBeforeIcon sx={{ fontSize: '1rem' }} />
          </IconButton>
        </Tooltip>

        <Tooltip title={`${shownDate} ${shownTime}\n${t('recordings.pickDateTime')}`} placement="bottom">
          <IconButton size="small" onClick={(e) => openCalendar(e.currentTarget)} sx={{ ...btnSx, flex: 1, borderRadius: '4px', gap: 0.4 }}>
            <CalendarMonthIcon sx={{ fontSize: '0.85rem', flexShrink: 0 }} />
            <Typography component="span" sx={{ fontSize: '0.65rem', color: txt ?? 'text.primary', whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' }}>
              {shownDate}
            </Typography>
          </IconButton>
        </Tooltip>

        <Tooltip title={t('recordings.later')}>
          <IconButton size="small" onClick={handleNext} sx={btnSx}>
            <NavigateNextIcon sx={{ fontSize: '1rem' }} />
          </IconButton>
        </Tooltip>
      </Stack>

      {/* Stream filter */}
      {streamNames.length > 1 && (
        <Stack direction="row" alignItems="center" sx={{ px: 0.75, py: 0.4, borderBottom: `1px solid ${divider}`, flexShrink: 0, gap: 0.5 }}>
          <LayersIcon sx={{ fontSize: '0.85rem', color: txtDim, flexShrink: 0 }} />
          <Select
            size="small"
            value={streamFilter}
            onChange={(e) => setStreamFilter(e.target.value)}
            displayEmpty
            fullWidth
            sx={{
              fontSize: '0.7rem',
              '.MuiSelect-select': { py: 0.25, px: 0.75 },
              ...(dark ? {
                color: txt,
                '.MuiOutlinedInput-notchedOutline': { borderColor: alpha(W, 0.2) },
                '.MuiSvgIcon-root': { color: alpha(W, 0.5) },
              } : {}),
            }}
          >
            <MenuItem value="" sx={{ fontSize: '0.7rem' }}>{t('archive.allStreams')}</MenuItem>
            {streamNames.map((s) => (
              <MenuItem key={s} value={s} sx={{ fontSize: '0.7rem' }}>{s}</MenuItem>
            ))}
          </Select>
        </Stack>
      )}

      {/* Clip list */}
      <Box ref={listRef} sx={{ flex: 1, overflowY: 'auto', minHeight: 0 }}>
        {loading ? (
          <Box sx={{ display: 'flex', justifyContent: 'center', mt: 1.5 }}>
            <CircularProgress size={18} sx={dark ? { color: alpha(W, 0.5) } : {}} />
          </Box>
        ) : filteredData.length === 0 ? (
          <Typography sx={{ px: 1, pt: 1.5, fontSize: '0.7rem', color: txtDim, textAlign: 'center' }}>
            {t('recordings.noRecordings')}
          </Typography>
        ) : (
          <List dense disablePadding>
            {renderPageButton('earlier')}
            {filteredData.map((rec, index) => {
              const first = rec.recordings[0]
              if (!first) return null
              const last = rec.recordings[rec.recordings.length - 1]
              const prev = filteredData[index - 1]
              const showDate = index === 0 || new Date(first.startTime).toDateString() !== new Date(prev?.recordings[0]?.startTime ?? '').toDateString()
              const isNearest = rec.recordings.some((r) => r.filePath === filePath)
              const isSelected = selected === first.startTime
              // Counted rather than all-or-nothing: the ROI pass works clip by clip, so a range
              // spends a long time part way through, and that is exactly when watching it is useful.
              const roiDone = rec.recordings.filter((r) => r.roiTranscoded).length

              return (
                <div key={`${first.startTime}:${last?.endTime ?? ''}`} data-start={first.startTime}>
                  {showDate && (
                    <ListSubheader
                      disableSticky
                      sx={{
                        lineHeight: '22px', fontSize: '0.65rem', opacity: 0.6,
                        bgcolor: dark ? 'transparent' : 'background.paper',
                        color: dark ? alpha(W, 0.55) : undefined,
                      }}
                    >
                      {fmtDay(first.startTime)}
                    </ListSubheader>
                  )}

                  <Tooltip
                    title={
                      <Box component="span" sx={{ display: 'block' }}>
                        <div style={{ wordBreak: 'break-all', fontSize: '0.65rem' }}>{first.filePath}</div>
                        <div>{t('recordings.startTime')}: {fmtFull(first.startTime)}</div>
                        {last?.endTime && <div>{t('recordings.endTime')}: {fmtFull(last.endTime)}</div>}
                        <div>{t('recordings.clips')}: {rec.recordings.length}</div>
                        {first.origin === 'edge' && <div>{t('recordings.recovered')}</div>}
                        {roiDone > 0 && (
                          <div>
                            {t('recordings.roiTranscoded')}
                            {roiDone < rec.recordings.length && `: ${roiDone}/${rec.recordings.length}`}
                          </div>
                        )}
                      </Box>
                    }
                    placement="left"
                  >
                    <ListItemButton
                      selected={isSelected}
                      onClick={() => { setSelected(first.startTime); onSelect(first) }}
                      sx={{
                        py: 0.3, px: 0.75,
                        borderLeft: isNearest ? `2px solid ${theme.palette.primary.main}` : '2px solid transparent',
                        ...(dark && isSelected ? { bgcolor: alpha(W, 0.12) } : {}),
                        ...(dark ? { '&:hover': { bgcolor: alpha(W, 0.08) } } : {}),
                      }}
                    >
                      <ListItemText
                        primary={
                          <>
                            {fmtTime(first.startTime)}
                            {first.origin === 'edge' && ' ⤵'}
                            {roiDone > 0 && (
                              // Its own colour rather than the row's: the row's colour already says
                              // "recovered", and a range can be both. Dimmed while the pass is only
                              // part way through this range, so progress is visible instead of the
                              // mark appearing all at once at the end.
                              <Box
                                component="span"
                                sx={{
                                  ml: 0.5,
                                  color: 'success.main',
                                  opacity: roiDone === rec.recordings.length ? 1 : 0.5,
                                }}
                              >
                                ◱
                              </Box>
                            )}
                          </>
                        }
                        slotProps={{
                          primary: {
                            variant: 'body2',
                            noWrap: true,
                            sx: {
                              fontVariantNumeric: 'tabular-nums',
                              fontSize: '0.76rem', lineHeight: 1.25,
                              // An insert recovered from the camera is a different kind of footage
                              // and never continuous with what surrounds it, so the row says so
                              // rather than looking like one more stretch of live recording.
                              color: first.origin === 'edge'
                                ? theme.palette.warning.main
                                : (dark ? (isSelected ? W : alpha(W, 0.8)) : undefined),
                              fontStyle: first.origin === 'edge' ? 'italic' : undefined,
                              fontWeight: isNearest ? 600 : undefined,
                            },
                          },
                        }}
                      />
                    </ListItemButton>
                  </Tooltip>
                </div>
              )
            })}
            {renderPageButton('later')}
          </List>
        )}
      </Box>

      <Popover
        open={Boolean(pickerAnchor)}
        anchorEl={pickerAnchor}
        onClose={() => setPickerAnchor(null)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'center' }}
        transformOrigin={{ vertical: 'top', horizontal: 'center' }}
        slotProps={{ paper: { sx: { overflow: 'hidden', borderRadius: 2 } } }}
      >
        {pickerAnchor && (
          <ArchiveDateTimePicker
            cameraId={cameraId}
            value={pickerTime ?? dayjs()}
            onApply={commitPickerTime}
            onCancel={() => setPickerAnchor(null)}
            dark={dark}
          />
        )}
      </Popover>
    </Box>
  )
})

export default RecordingsList
