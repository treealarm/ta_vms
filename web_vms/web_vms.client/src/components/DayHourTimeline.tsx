import { useEffect, useMemo } from 'react'
import { Box, Stack, Tooltip, Typography, alpha, useTheme } from '@mui/material'
import dayjs, { Dayjs } from 'dayjs'
import { useAppDispatch, useAppSelector } from '../store'
import { fetchRecordingRanges, recordingRangesKey } from '../store/fsSlice'

type Props = {
  cameraId: string
  day: Dayjs
  selectedTime?: Dayjs
  onPick: (time: Dayjs) => void
  dark?: boolean
}

const HOURS = 24

export default function DayHourTimeline({ cameraId, day, selectedTime, onPick, dark = false }: Props) {
  const dispatch = useAppDispatch()
  const theme = useTheme()
  const W = theme.palette.common.white

  const dayStart = useMemo(() => day.startOf('day'), [day])
  const fromUtc = useMemo(() => dayStart.toISOString(), [dayStart])
  const toUtc = useMemo(() => day.endOf('day').toISOString(), [day])
  const key = recordingRangesKey({ cameraId, fromUtc, toUtc })

  const ranges = useAppSelector((s) => s.fs.recordingRangesByKey[key]?.ranges)

  // Always refetch on mount/day change rather than trusting a cached entry: new
  // clips keep arriving, so a stale cache would keep showing an hour as empty.
  useEffect(() => {
    void dispatch(fetchRecordingRanges({ cameraId, fromUtc, toUtc, limit: 500 }))
  }, [cameraId, fromUtc, toUtc, dispatch])

  const hourHasData = useMemo(() => {
    const flags = new Array(HOURS).fill(false)
    if (!ranges) return flags
    for (const range of ranges) {
      for (const rec of range.recordings) {
        const startMin = dayjs(rec.startTime).diff(dayStart, 'minute')
        const endMin = dayjs(rec.endTime ?? rec.startTime).diff(dayStart, 'minute')
        const firstHour = Math.max(0, Math.floor(Math.min(startMin, endMin) / 60))
        const lastHour = Math.min(HOURS - 1, Math.ceil(Math.max(startMin, endMin) / 60))
        for (let h = firstHour; h <= lastHour; h++) flags[h] = true
      }
    }
    return flags
  }, [ranges, dayStart])

  const selectedHour = selectedTime?.isSame(day, 'day') ? selectedTime.hour() : -1

  return (
    <Box sx={{ px: 0.25 }}>
      <Stack direction="row" sx={{ height: 22, gap: '1px' }}>
        {hourHasData.map((has, h) => (
          <Tooltip key={h} title={`${String(h).padStart(2, '0')}:00`} disableInteractive>
            <Box
              onClick={() => has && onPick(day.hour(h).minute(0).second(0))}
              sx={{
                flex: 1,
                borderRadius: '2px',
                cursor: has ? 'pointer' : 'default',
                bgcolor: has
                  ? (h === selectedHour ? 'primary.main' : alpha(theme.palette.primary.main, dark ? 0.55 : 0.45))
                  : (dark ? alpha(W, 0.06) : alpha(theme.palette.common.black, 0.06)),
                outline: h === selectedHour ? `1px solid ${theme.palette.primary.light}` : 'none',
                '&:hover': has ? { bgcolor: 'primary.main' } : undefined,
              }}
            />
          </Tooltip>
        ))}
      </Stack>
      <Stack direction="row" justifyContent="space-between" sx={{ px: 0.1, mt: 0.25 }}>
        {[0, 6, 12, 18, 23].map((h) => (
          <Typography key={h} sx={{ fontSize: '0.55rem', color: dark ? alpha(W, 0.4) : 'text.secondary' }}>
            {String(h).padStart(2, '0')}
          </Typography>
        ))}
      </Stack>
    </Box>
  )
}
