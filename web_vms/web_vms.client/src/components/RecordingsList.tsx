import { useEffect, useState } from 'react'
import { List, ListItemButton, ListItemText, Tooltip, CircularProgress, Box, Stack, IconButton, TextField } from '@mui/material'
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

export default function RecordingsList({ open, cameraId, timestamp, filePath, onSelect }: Props) {
  const dispatch = useAppDispatch()
  const [selected, setSelected] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)
  const [inputTime, setInputTime] = useState(toLocalDatetimeInputValue(new Date()))
  const [queryTime, setQueryTime] = useState<string | undefined>()
  const [loaded, setLoaded] = useState(false)

  useEffect(() => {
    if (open) {
      const initialTime = timestamp ?? new Date().toISOString()
      setInputTime(toLocalDatetimeInputValue(new Date(initialTime)))
      setQueryTime(initialTime)
      setLoaded(false)
    }
  }, [open, timestamp])

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
    setInputTime(toLocalDatetimeInputValue(new Date(time)))
    setLoaded(false)
  }

  const handleApplyTime = () => {
    if (!inputTime) {
      return
    }

    const nextTime = new Date(inputTime)
    if (isNaN(nextTime.getTime())) {
      return
    }

    requestAt(nextTime.toISOString())
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

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
      <Stack
        direction="row"
        alignItems="center"
        spacing={0.5}
        sx={{ px: 1, py: 1, borderBottom: '1px solid rgba(0,0,0,0.1)', flexShrink: 0 }}
      >
        <TextField
          type="datetime-local"
          value={inputTime}
          onChange={(e) => setInputTime(e.target.value)}
          inputProps={{ step: 1 }}
          size="small"
        />
        <IconButton size="small" onClick={handleApplyTime} title="Обновить список по времени">
          🔎
        </IconButton>
        <IconButton size="small" onClick={handlePrev} title="Назад">
          ◀
        </IconButton>
        <IconButton size="small" onClick={handleNext} title="Вперёд">
          ▶
        </IconButton>
      </Stack>

      <Box sx={{ flex: 1, overflowY: 'auto', minHeight: 0 }}>
        {loading ? (
          <Box sx={{ display: 'flex', justifyContent: 'center', mt: 1 }}>
            <CircularProgress size={20} />
          </Box>
        ) : (
          <List dense disablePadding>
            {data.map((rec) => {
              const isNearest = filePath === rec.filePath
              const isSelected = selected === rec.filePath
              const time = new Date(rec.startTime).toLocaleTimeString()

              return (
                <Tooltip
                  key={rec.filePath}
                  title={
                    <>
                      <div>{rec.filePath}</div>
                      <div>
                        {new Date(rec.startTime).toLocaleString()} —{' '}
                        {rec.endTime ? new Date(rec.endTime).toLocaleString() : '...'}
                      </div>
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
              )
            })}
          </List>
        )}
      </Box>
    </Box>
  )
}
