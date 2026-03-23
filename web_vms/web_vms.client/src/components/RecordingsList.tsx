import { useEffect, useState } from 'react'
import {
  List,
  ListItemButton,
  ListItemText,
  Tooltip,
  CircularProgress,
  Box,
  IconButton,
  Stack,
  Typography
} from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import type { RecordingDto } from '../dto/dtos'

type Props = {
  open: boolean
  onClose: () => void
  cameraId: string
  timestamp?: string
  onSelect: (rec: RecordingDto) => void
}

export default function RecordingsList({
  open,
  onClose,
  cameraId,
  timestamp,
  onSelect
}: Props) {
  const [data, setData] = useState<RecordingDto[]>([])
  const [nearest, setNearest] = useState<RecordingDto | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  const [queryTime, setQueryTime] = useState<string | undefined>()
  const [loaded, setLoaded] = useState(false)

  // фиксируем timestamp только при открытии
  useEffect(() => {
    if (open) {
      setQueryTime(timestamp)
      setLoaded(false) // разрешаем новый запрос
    }
  }, [open])

  // делаем ОДИН запрос
  useEffect(() => {
    if (!open || !queryTime || loaded) return

    const fetchData = async () => {
      setLoading(true)

      const res = await fetch('/api/fs/recordings', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          cameraId,
          timestamp: queryTime
        })
      })

      const json = await res.json()

      setData(json.recordings || [])
      setNearest(json.nearest || null)

      setLoading(false)
      setLoaded(true)
    }

    fetchData()
  }, [cameraId, queryTime, open, loaded])

  const handleClick = (rec: RecordingDto) => {
    setSelected(rec.filePath)
    onSelect(rec)
  }

  if (!open) return null

  return (
    <Box
      sx={{
        position: 'absolute',
        top: 0,
        right: 0,
        width: 260,
        height: '100%',
        bgcolor: 'background.paper',
        borderLeft: '1px solid rgba(0,0,0,0.1)',
        boxShadow: 3,
        display: 'flex',
        flexDirection: 'column',
        zIndex: 10
      }}
    >
      {/* HEADER */}
      <Stack
        direction="row"
        alignItems="center"
        justifyContent="space-between"
        sx={{ px: 1, py: 1, borderBottom: '1px solid rgba(0,0,0,0.1)' }}
      >
        <Typography variant="body2">Recordings</Typography>
        <IconButton size="small" onClick={onClose}>
          <CloseIcon fontSize="small" />
        </IconButton>
      </Stack>

      {/* CONTENT */}
      <Box sx={{ flex: 1, overflowY: 'auto', p: 1 }}>
        {loading && <CircularProgress size={20} />}

        <List dense>
          {data.map((rec) => {
            const isNearest = nearest?.filePath === rec.filePath
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
                      {rec.endTime
                        ? new Date(rec.endTime).toLocaleString()
                        : '...'}
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
                    bgcolor: isSelected
                      ? 'rgba(25,118,210,0.15)'
                      : undefined
                  }}
                >
                  <ListItemText
                    primary={isNearest ? `➤ ${time}` : time}
                    primaryTypographyProps={{ fontSize: 13 }}
                  />
                </ListItemButton>
              </Tooltip>
            )
          })}
        </List>
      </Box>
    </Box>
  )
}