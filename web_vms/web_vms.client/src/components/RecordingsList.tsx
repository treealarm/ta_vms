import { useEffect, useState } from 'react'
import {
  List, ListItemButton, ListItemText,
  Tooltip, CircularProgress, Box, Stack,
  IconButton, TextField, Popover, Typography,
  ListSubheader
} from '@mui/material'
import { toLocalDatetimeInputValue, type RecordingDto } from '../dto/dtos'
import { fetchRecordings } from '../store/fsSlice'
import { useAppDispatch, useAppSelector } from '../store'

type Props = {
  open: boolean
  cameraId: string
  timestamp?: string
  filePath?: string
  onSelect: (rec: RecordingDto) => void
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

export default function RecordingsList({ open, cameraId, timestamp, filePath, onSelect }: Props) {
  const dispatch = useAppDispatch()
  const [selected, setSelected] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [pickerTime, setPickerTime] = useState(toLocalDatetimeInputValue(new Date()))
  const [displayTime, setDisplayTime] = useState(normalizeTimestamp(timestamp))
  const [queryTime, setQueryTime] = useState<string | undefined>()
  const [loaded, setLoaded] = useState(false)
  const [pickerAnchor, setPickerAnchor] = useState<HTMLElement | null>(null)

  useEffect(() => {
    setDisplayTime(normalizeTimestamp(timestamp))
  }, [timestamp])

  useEffect(() => {
    if (open) {
      const initialTime = normalizeTimestamp(timestamp)
      setPickerTime(toLocalDatetimeInputValue(new Date(initialTime)))
      setQueryTime(initialTime)
      setLoaded(false)
    }
  }, [open])

  useEffect(() => {
    if (!open || !queryTime || loaded) {
      return
    }

    let cancelled = false

    const load = async () => {
      setLoading(true)
      await dispatch(fetchRecordings({ cameraId, timestamp: queryTime }))

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

  const recordingsKey = queryTime ? `${cameraId}:${queryTime}` : ''
  const data = useAppSelector((state) => state.fs.recordingsByKey[recordingsKey]?.recordings ?? [])

  const requestAt = (time: string) => {
    setQueryTime(time)
    setPickerTime(toLocalDatetimeInputValue(new Date(time)))
    setLoaded(false)
  }

  const handlePickerChange = (value: string) => {
    setPickerTime(value)
    if (!value) {
      return
    }

    const nextTime = new Date(value)
    if (isNaN(nextTime.getTime())) {
      return
    }

    requestAt(nextTime.toISOString())
    setPickerAnchor(null)
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
    setPickerTime(toLocalDatetimeInputValue(new Date(displayTime)))
    setPickerAnchor(target)
  }

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      <Stack
        direction="column"
        alignItems="center"
        spacing={0.5}
        sx={{ px: 1, py: 1, borderBottom: '1px solid rgba(0,0,0,0.1)', flexShrink: 0 }}
      >
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
          <TextField
            autoFocus
            type="datetime-local"
            value={pickerTime}
            onChange={(e) => handlePickerChange(e.target.value)}
            inputProps={{ step: 1 }}
            size="small"
          />
        </Box>
      </Popover>

      <Box sx={{ flex: 1, overflowY: 'auto', minHeight: 0 }}>
        {loading ? (
          <Box sx={{ display: 'flex', justifyContent: 'center', mt: 1 }}>
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
                      <ListSubheader
                        disableSticky
                        sx={{
                          lineHeight: '24px',
                          bgcolor: 'background.paper',
                          fontSize: 12,
                          opacity: 0.7
                        }}
                      >
                        {new Date(rec.startTime).toLocaleDateString()}
                      </ListSubheader>
                    )}

                    <Tooltip
                      title={
                        <>
                          <div>{rec.filePath}</div>
                          <div>{new Date(rec.startTime).toLocaleString()}</div>
                        </>
                      }
                      placement="left"
                    >
                      <ListItemButton
                        selected={isSelected}
                        onClick={() => handleClick(rec)}
                        sx={{
                          py: 0.5,
                          borderLeft: isNearest ? '3px solid orange' : undefined,
                          bgcolor: isSelected ? 'rgba(25,118,210,0.15)' : undefined
                        }}
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
