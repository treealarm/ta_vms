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
  filePath?: string
  onSelect: (rec: RecordingDto) => void
}

export default function RecordingsList({
  open,
  cameraId,
  timestamp,
  filePath,
  onSelect
}: Props) {
  const [data, setData] = useState<RecordingDto[]>([])

  const [selected, setSelected] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  const [queryTime, setQueryTime] = useState<string | undefined>()
  const [loaded, setLoaded] = useState(false)

  // фиксируем timestamp при открытии
  useEffect(() => {
    if (open) {
      setQueryTime(timestamp)
      setLoaded(false)
    }
  }, [open])


  // один запрос
  useEffect(() => {
    if (!open || !queryTime || loaded) return

    let cancelled = false

    const fetchData = async () => {
      setLoading(true)

      try {
        const res = await fetch('/api/fs/recordings', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            cameraId,
            timestamp: queryTime
          })
        })

        const json = await res.json()

        if (cancelled) return

        setData(json.recordings || [])

      } finally {
        if (!cancelled) {
          setLoading(false)
          setLoaded(true)
        }
      }
    }

    fetchData()

    return () => {
      cancelled = true
    }
  }, [cameraId, queryTime, open, loaded])

  const handleClick = (rec: RecordingDto) => {
    setSelected(rec.filePath)
    console.log('rec.filePath', rec.filePath)
    onSelect(rec)
  }

  console.log('filePath', filePath);
  if (!open) return null

  return (
    <Box
      sx={{
        display: 'flex',
        flexDirection: 'column',
        height: '100%',
        minHeight: 0 // 🔑 критично для flex
      }}
    >
      {/* HEADER */}
      <Stack
        direction="row"
        alignItems="center"
        justifyContent="space-between"
        sx={{
          px: 1,
          py: 1,
          borderBottom: '1px solid rgba(0,0,0,0.1)',
          flexShrink: 0
        }}
      >
      </Stack>

      {/* CONTENT */}
      <Box
        sx={{
          flex: 1,
          overflowY: 'auto',
          minHeight: 0 // 🔑 вот это фикс скролла
        }}
      >
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
        )}
      </Box>
    </Box>
  )
}