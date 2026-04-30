import { useEffect, useRef, useState } from 'react'
import {
  List, ListItemButton, ListItemText,
  Tooltip, CircularProgress, Box, Stack,
  IconButton, Popover, Typography,
  ListSubheader
} from '@mui/material'
import { type RecordingDto } from '../dto/dtos'
import { fetchRecordings } from '../store/fsSlice'
import { useAppDispatch, useAppSelector } from '../store'

import { LocalizationProvider } from '@mui/x-date-pickers/LocalizationProvider'
import { DateTimePicker } from '@mui/x-date-pickers/DateTimePicker'
import { AdapterDayjs } from '@mui/x-date-pickers/AdapterDayjs'
import dayjs, { Dayjs } from 'dayjs'

type Props = {
  open: boolean
  cameraId: string
  timestamp?: string
  filePath?: string
  onSelect: (rec: RecordingDto) => void
  onTimeChange?: (iso: string) => void
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

export default function RecordingsList({ open, cameraId, timestamp, filePath, onSelect, onTimeChange }: Props) {
  const dispatch = useAppDispatch()
  const [selected, setSelected] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [pickerTime, setPickerTime] = useState<Dayjs | null>(dayjs())
  const [displayTime, setDisplayTime] = useState(normalizeTimestamp(timestamp))
  const [queryTime, setQueryTime] = useState<string | undefined>()
  const [loaded, setLoaded] = useState(false)
  const [pickerAnchor, setPickerAnchor] = useState<HTMLElement | null>(null)

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
      await dispatch(fetchRecordings({ cameraId, timestamp: queryTime, limit:50 }))

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
  const EMPTY: RecordingDto[] = []
  const data = useAppSelector(
    (state) => state.fs.recordingsByKey[recordingsKey]?.recordings ?? EMPTY
  )

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
    if (!last) {
      return
    }

    requestAt(new Date(last.startTime).toISOString())
  }

  const handlePrev = () => {
    const first = data[0]
    if (!first) {
      return
    }

    requestAt(new Date(first.startTime).toISOString())
  }

  const handleClick = (rec: RecordingDto) => {
    setSelected(rec.filePath)
    onSelect(rec)
  }

  if (!open) {
    return null
  }

  const shownTime = new Date(displayTime)
  const shownDate = shownTime.toLocaleDateString()
  const shownClock = shownTime.toLocaleTimeString()

  const openPicker = (target: HTMLElement) => {
    setPickerTime(dayjs(displayTime))
    setPickerAnchor(target)
  }

  return (
    <Box className="vms-recordings-root">
      <Stack direction="column" alignItems="center" spacing={0.5} className="vms-recordings-toolbar">
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
        <Box className="vms-picker-popover">
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

      <Box className="vms-recordings-scroll">
        {loading ? (
          <Box className="vms-recordings-loader">
            <CircularProgress size={20} />
          </Box>
        ) : (
            <List dense disablePadding>
              {data.map((rec, index) => {
                const prev = data[index - 1]

                const currentDate = new Date(rec.startTime).toDateString()
                const prevDate = prev ? new Date(prev.startTime).toDateString() : null

                const showDateHeader = index === 0 || currentDate !== prevDate

                const isNearest = filePath === rec.filePath
                const isSelected = selected === rec.filePath
                const time = new Date(rec.startTime).toLocaleTimeString()

                return (
                  <div key={rec.filePath}>
                    {showDateHeader && (
                      <ListSubheader disableSticky className="vms-recordings-subheader">
                        {new Date(rec.startTime).toLocaleDateString()}
                      </ListSubheader>
                    )}

                    <Tooltip
                      title={
                        <Box component="span" className="vms-tooltip-block">
                          <div className="vms-tooltip-path">{rec.filePath}</div>
                          <div>{new Date(rec.startTime).toLocaleString()}</div>
                        </Box>
                      }
                      placement="left"
                    >
                      <ListItemButton
                        selected={isSelected}
                        onClick={() => handleClick(rec)}
                        data-vms-playing={isNearest ? true : undefined}
                      >
                        <ListItemText primary={isNearest ? `➤ ${time}` : time} />
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
