import { useEffect, useMemo, useRef, useState } from 'react'
import {
  List, ListItemButton, ListItemText,
  Tooltip, CircularProgress, Box, Stack,
  IconButton, Popover, Typography,
  ListSubheader, Select, MenuItem
} from '@mui/material'
import { type RecordingDto, type RecordingRangeDto } from '../dto/dtos'
import { fetchRecordingRanges } from '../store/fsSlice'
import { useAppDispatch, useAppSelector } from '../store'

import { LocalizationProvider } from '@mui/x-date-pickers/LocalizationProvider'
import { DateTimePicker } from '@mui/x-date-pickers/DateTimePicker'
import { AdapterDayjs } from '@mui/x-date-pickers/AdapterDayjs'
import dayjs, { Dayjs } from 'dayjs'

function formatClipStart24(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) {
    return iso
  }
  return d.toLocaleTimeString(undefined, {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  })
}

function formatTooltipDateTime(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) {
    return iso
  }
  return d.toLocaleString(undefined, {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  })
}

function formatListDayLabel(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) {
    return ''
  }
  return d.toLocaleDateString('sv-SE')
}

type Props = {
  open: boolean
  cameraId: string
  timestamp?: string
  filePath?: string
  onSelect: (rec: RecordingDto) => void
  onTimeChange?: (iso: string) => void
  /** Fired when the range list for the current query changes (e.g. after fetch). */
  onRangesChange?: (ranges: RecordingRangeDto[]) => void
  /** Optional: sync list selection with current playback (e.g. auto-advance). Range `startTime` (ISO). */
  playbackStartTime?: string
}

const normalizeTimestamp = (value?: string) => {
  if (!value) {
    return new Date().toISOString()
  }

  const parsed = new Date(value)
  if (isNaN(parsed.getTime())) {
    return new Date().toISOString()
  }

  // Guard against "epoch-like" placeholders when server position is not ready yet.
  if (parsed.getUTCFullYear() <= 1970) {
    return new Date().toISOString()
  }

  return parsed.toISOString()
}

export default function RecordingsList({ open, cameraId, timestamp, filePath, onSelect, onTimeChange, onRangesChange, playbackStartTime }: Props) {
  const dispatch = useAppDispatch()
  const [selected, setSelected] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [pickerTime, setPickerTime] = useState<Dayjs | null>(dayjs())
  const [displayTime, setDisplayTime] = useState(normalizeTimestamp(timestamp))
  const [queryTime, setQueryTime] = useState<string | undefined>()
  const [loaded, setLoaded] = useState(false)
  const [pickerAnchor, setPickerAnchor] = useState<HTMLElement | null>(null)
  const [streamFilter, setStreamFilter] = useState<string>('')

  const acceptTimeoutRef = useRef<number | null>(null)

  useEffect(() => {
    setDisplayTime(normalizeTimestamp(timestamp))
  }, [timestamp])

  useEffect(() => {
    if (open) {
      const initialTime = normalizeTimestamp(timestamp)
      setPickerTime(dayjs(initialTime))
      setQueryTime(initialTime)
      setLoaded(false)
    }
  }, [open])

  useEffect(() => {
    return () => {
      if (acceptTimeoutRef.current) {
        clearTimeout(acceptTimeoutRef.current)
      }
    }
  }, [])

  useEffect(() => {
    if (!open || !queryTime || loaded) {
      return
    }

    let cancelled = false

    const load = async () => {
      setLoading(true)
      await dispatch(fetchRecordingRanges({ cameraId, timestamp: queryTime, limit: 50 }))

      if (!cancelled) {
        setLoading(false)
        setLoaded(true)
      }
    }

    void load()

    return () => {
      cancelled = true
    }
  }, [cameraId, dispatch, loaded, open, queryTime])

  const handleAccept = (value: Dayjs | null) => {
    if (!value) return

    const iso = value.toISOString()

    if (acceptTimeoutRef.current) {
      clearTimeout(acceptTimeoutRef.current)
    }

    acceptTimeoutRef.current = window.setTimeout(() => {
      requestAt(iso)
      onTimeChange?.(iso)
    }, 2000)
  }



  const recordingsKey = queryTime ? `${cameraId}:${queryTime}` : ''
  const EMPTY: RecordingRangeDto[] = []
  const data = useAppSelector(
    (state) => state.fs.recordingRangesByKey[recordingsKey]?.ranges ?? EMPTY
  )

  useEffect(() => {
    onRangesChange?.(data)
  }, [data, onRangesChange])

  const streamNames = useMemo(() => {
    const names = new Set<string>()
    data.forEach((range) => range.recordings.forEach((r) => { if (r.streamName) names.add(r.streamName) }))
    return [...names].sort()
  }, [data])

  // Reset filter if selected stream disappears from new data
  useEffect(() => {
    if (streamFilter && !streamNames.includes(streamFilter)) setStreamFilter('')
  }, [streamFilter, streamNames])

  const filteredData = useMemo(() => {
    if (!streamFilter) return data
    return data.filter((range) => range.recordings.some((r) => r.streamName === streamFilter))
  }, [data, streamFilter])

  useEffect(() => {
    if (playbackStartTime != null && playbackStartTime !== '') {
      setSelected(playbackStartTime)
    }
  }, [playbackStartTime])

  const requestAt = (time: string) => {
    const d = dayjs(time)
    if (!d.isValid()) return
    const iso = d.toISOString()

    setQueryTime(iso)
    setPickerTime(d)
    setDisplayTime(iso)
    setLoaded(false)
    onTimeChange?.(iso)
  }


  const handleNext = () => {
    const last = data[data.length - 1]
    if (!last?.recordings[0]) return
    requestAt(new Date(last.recordings[0].startTime).toISOString())
  }

  const handlePrev = () => {
    const first = data[0]
    if (!first?.recordings[0]) return
    requestAt(new Date(first.recordings[0].startTime).toISOString())
  }

  const handleClick = (range: RecordingRangeDto) => {
    const first = range.recordings[0]
    if (!first) return
    setSelected(first.startTime)
    onSelect(first)
  }

  if (!open) {
    return null
  }

  const shownTime = new Date(displayTime)
  const shownDate = shownTime.toLocaleDateString('sv-SE')
  const shownClock = shownTime.toLocaleTimeString(undefined, {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  })

  const openPicker = (target: HTMLElement) => {
    setPickerTime(dayjs(displayTime))
    setPickerAnchor(target)
  }

  return (
    <Box
      sx={{
        display: 'flex',
        flexDirection: 'column',
        height: '100%',
        minHeight: 0,
        width: 118,
        flexShrink: 0,
        alignSelf: 'stretch',
      }}
    >
      <Stack
        direction="column"
        alignItems="center"
        spacing={0.5}
        sx={{ px: 1, py: 1, borderBottom: '1px solid rgba(0,0,0,0.1)', flexShrink: 0 }}
      >
        {streamNames.length > 1 && (
          <Select
            size="small"
            value={streamFilter}
            onChange={(e) => setStreamFilter(e.target.value)}
            displayEmpty
            sx={{ width: '100%', fontSize: '0.75rem', '.MuiSelect-select': { py: 0.4, px: 1 } }}
          >
            <MenuItem value="" sx={{ fontSize: '0.75rem' }}>All streams</MenuItem>
            {streamNames.map((s) => (
              <MenuItem key={s} value={s} sx={{ fontSize: '0.75rem' }}>{s}</MenuItem>
            ))}
          </Select>
        )}
        <Typography variant="caption" sx={{ lineHeight: 1.15 }}>
          {shownDate}
        </Typography>
        <Typography variant="caption" sx={{ lineHeight: 1.15 }}>
          {shownClock}
        </Typography>

        <Stack direction="row" spacing={0.5}>
          <IconButton size="small" onClick={handlePrev} title="Previous">
            ◀
          </IconButton>
          <IconButton size="small" onClick={(e) => openPicker(e.currentTarget)} title="Pick date and time">
            📅
          </IconButton>
          <IconButton size="small" onClick={handleNext} title="Next">
            ▶
          </IconButton>
        </Stack>
      </Stack>

      <Popover
        open={Boolean(pickerAnchor)}
        anchorEl={pickerAnchor}
        onClose={() => setPickerAnchor(null)}
        anchorOrigin={{ vertical: 'bottom', horizontal: 'left' }}
      >
        <Box sx={{ p: 1 }}>
          <LocalizationProvider dateAdapter={AdapterDayjs}>
            <DateTimePicker
              value={pickerTime}
              onChange={(newValue) => setPickerTime(newValue)}
              onAccept={handleAccept}
              timeSteps={{ minutes:1, seconds: 1 }}
              views={['year', 'month', 'day', 'hours', 'minutes', 'seconds']}
              format="YYYY-MM-DD HH:mm:ss"
              slotProps={{
                textField: {
                  size: 'small',
                  autoFocus: true
                }
              }}
            />
          </LocalizationProvider>
        </Box>
      </Popover>

      <Box sx={{ flex: 1, overflowY: 'auto', minHeight: 0 }}>
        {loading ? (
          <Box sx={{ display: 'flex', justifyContent: 'center', mt: 1 }}>
            <CircularProgress size={20} />
          </Box>
        ) : (
            <List dense disablePadding>
              {filteredData.map((rec, index) => {
                const first = rec.recordings[0]
                if (!first) return null
                const last = rec.recordings[rec.recordings.length - 1]
                const prev = filteredData[index - 1]

                const currentDate = new Date(first.startTime).toDateString()
                const prevDate = prev?.recordings[0] ? new Date(prev.recordings[0].startTime).toDateString() : null

                const showDateHeader = index === 0 || currentDate !== prevDate

                const isNearest = rec.recordings.some(r => r.filePath === filePath)
                const isSelected = selected === first.startTime

                return (
                  <div key={`${first.startTime}:${last?.endTime ?? ''}`}>
                    {showDateHeader && (
                      <ListSubheader
                        disableSticky
                        sx={{
                          lineHeight: '24px',
                          bgcolor: 'background.paper',
                          fontSize: 12,
                          opacity: 0.7
                        }}
                      >
                        {formatListDayLabel(first.startTime)}
                      </ListSubheader>
                    )}

                    <Tooltip
                      title={
                        <Box component="span" sx={{ display: 'block', maxWidth: 380 }}>
                          <div style={{ wordBreak: 'break-all' }}>{first.filePath}</div>
                          <div>Start: {formatTooltipDateTime(first.startTime)}</div>
                          {last?.endTime && <div>End: {formatTooltipDateTime(last.endTime)}</div>}
                          <div>Clips: {rec.recordings.length}</div>
                        </Box>
                      }
                      placement="left"
                    >
                      <ListItemButton
                        selected={isSelected}
                        onClick={() => handleClick(rec)}
                        sx={{
                          py: 0.35,
                          px: 0.75,
                          borderLeft: isNearest ? '3px solid orange' : undefined,
                          bgcolor: isSelected ? 'rgba(25,118,210,0.15)' : undefined,
                        }}
                      >
                        <ListItemText
                          primary={isNearest ? `➤ ${formatClipStart24(first.startTime)}` : formatClipStart24(first.startTime)}
                          slotProps={{
                            primary: {
                              variant: 'body2',
                              noWrap: true,
                              sx: { fontVariantNumeric: 'tabular-nums', fontSize: '0.8rem', lineHeight: 1.2 },
                            },
                          }}
                        />
                      </ListItemButton>
                    </Tooltip>
                  </div>
                )
              })}
            </List>
        )}
      </Box>
    </Box>
  )
}
