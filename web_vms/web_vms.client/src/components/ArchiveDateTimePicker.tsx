import { useEffect, useMemo, useState } from 'react'
import { Box, Button, Stack, Typography } from '@mui/material'
import { LocalizationProvider } from '@mui/x-date-pickers/LocalizationProvider'
import { DateCalendar } from '@mui/x-date-pickers/DateCalendar'
import { AdapterDayjs } from '@mui/x-date-pickers/AdapterDayjs'
import dayjs, { type Dayjs } from 'dayjs'
import { useTranslation } from 'react-i18next'

import { useAppDispatch, useAppSelector } from '../store'
import { fetchRecordingCoverage, recordingCoverageKey } from '../store/fsSlice'
import type { RecordingCoverageGranularity } from '../dto/dtos'
import { useWallCoverage } from '../hooks/useWallCoverage'
import { firstCoveredInRange } from '../utils/archiveRanges'
import { HOUR_MS, localDayEndMs, localDayStartMs } from '../utils/wallTime'
import DayHourTimeline from './DayHourTimeline'

// Fine-grained enough that the first clip of an hour is located exactly, not merged away.
const DAY_PSEUDO_WIDTH = 2000

type Props = {
  cameraId: string
  /** Initially selected instant. */
  value: Dayjs
  onApply: (time: Dayjs) => void
  onCancel: () => void
  dark?: boolean
}

/**
 * Date/time picker that only offers instants the camera actually has archive for: years, months and
 * days with no recording are disabled, and the hour strip below shows where that day's material is.
 *
 * Shared by the per-cell recordings list and the wall timeline's "jump to" button — a plain
 * calendar would happily send the operator to an empty week.
 */
export default function ArchiveDateTimePicker({ cameraId, value, onApply, onCancel, dark = false }: Props) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const coverageByKey = useAppSelector((s) => s.fs.recordingCoverageByKey)

  const [selected, setSelected] = useState<Dayjs>(value)
  const [calendarYear, setCalendarYear] = useState<Dayjs>(value)
  const [calendarMonth, setCalendarMonth] = useState<Dayjs>(value)

  const loadCoverage = (granularity: RecordingCoverageGranularity, fromUtc?: string, toUtc?: string) => {
    void dispatch(fetchRecordingCoverage({ cameraId, granularity, fromUtc, toUtc }))
  }

  const yearBuckets = useMemo(
    () => new Set(coverageByKey[recordingCoverageKey({ cameraId, granularity: 'Year' })]?.buckets ?? []),
    [coverageByKey, cameraId]
  )
  const monthBuckets = useMemo(() => {
    const fromUtc = calendarYear.startOf('year').toISOString()
    return new Set(coverageByKey[recordingCoverageKey({ cameraId, granularity: 'Month', fromUtc })]?.buckets ?? [])
  }, [coverageByKey, cameraId, calendarYear])
  const dayBuckets = useMemo(() => {
    const fromUtc = calendarMonth.startOf('month').toISOString()
    return new Set(coverageByKey[recordingCoverageKey({ cameraId, granularity: 'Day', fromUtc })]?.buckets ?? [])
  }, [coverageByKey, cameraId, calendarMonth])

  // Coverage is re-fetched rather than trusted from cache: a camera recording right now may have
  // just gained its first clip of the day. The query is a cheap indexed DISTINCT.
  useEffect(() => {
    const t0 = value ?? dayjs()
    setCalendarYear(t0)
    setCalendarMonth(t0)
    loadCoverage('Year')
    loadCoverage('Month', t0.startOf('year').toISOString(), t0.startOf('year').add(1, 'year').toISOString())
    loadCoverage('Day', t0.startOf('month').toISOString(), t0.startOf('month').add(1, 'month').toISOString())
    // Runs once per mount — the picker is mounted inside a popover, so opening it is the trigger.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cameraId])

  const handleYearChange = (year: Dayjs) => {
    setCalendarYear(year)
    loadCoverage('Month', year.startOf('year').toISOString(), year.startOf('year').add(1, 'year').toISOString())
  }

  const handleMonthChange = (month: Dayjs) => {
    setCalendarMonth(month)
    loadCoverage('Day', month.startOf('month').toISOString(), month.startOf('month').add(1, 'month').toISOString())
  }

  // Exact coverage of the selected day, used to turn a picked hour into a real recording instant.
  const cameraIds = useMemo(() => [cameraId], [cameraId])
  const dayStartMs = localDayStartMs(selected.valueOf())
  const daySegments = useWallCoverage(
    cameraIds,
    dayStartMs,
    localDayEndMs(dayStartMs) - dayStartMs,
    DAY_PSEUDO_WIDTH
  ).byCamera[cameraId]

  /**
   * Picking an hour means "take me to the footage in that hour", not to its first minute — which is
   * usually empty. Falls back to the rest of the day, then to the raw pick.
   */
  const resolveToFootage = (picked: Dayjs): Dayjs => {
    const segs = daySegments ?? []
    if (segs.length === 0) return picked

    const ms = picked.valueOf()
    const inHour = firstCoveredInRange(segs, ms, ms + HOUR_MS)
    if (inHour !== null) return dayjs(inHour)

    const laterToday = firstCoveredInRange(segs, ms, localDayEndMs(ms))
    return laterToday !== null ? dayjs(laterToday) : picked
  }

  return (
    <Box>
      <Box sx={{ px: 1.5, pt: 1, pb: 0.5, bgcolor: 'primary.main' }}>
        <Typography
          sx={{
            fontSize: '0.7rem',
            color: 'primary.contrastText',
            fontWeight: 600,
            letterSpacing: '0.05em',
            textTransform: 'uppercase',
          }}
        >
          {t('recordings.jumpToDateTime')}
        </Typography>
      </Box>

      <Box sx={{ p: 1 }}>
        <LocalizationProvider dateAdapter={AdapterDayjs}>
          <DateCalendar
            value={selected}
            onChange={(v) => v && setSelected(v)}
            onYearChange={handleYearChange}
            onMonthChange={handleMonthChange}
            views={['year', 'month', 'day']}
            openTo="day"
            reduceAnimations
            shouldDisableYear={(y) => !yearBuckets.has(y.format('YYYY'))}
            shouldDisableMonth={(m) => !monthBuckets.has(m.format('YYYY-MM'))}
            shouldDisableDate={(d) => !dayBuckets.has(d.format('YYYY-MM-DD'))}
          />
        </LocalizationProvider>

        <Box sx={{ px: 0.5, pt: 0.25 }}>
          <DayHourTimeline
            cameraId={cameraId}
            day={selected}
            selectedTime={selected}
            onPick={(v) => onApply(resolveToFootage(v))}
            dark={dark}
          />
        </Box>

        <Stack direction="row" justifyContent="flex-end" spacing={0.5} sx={{ px: 0.5, pt: 0.75 }}>
          <Button size="small" onClick={onCancel}>
            {t('common.cancel')}
          </Button>
          <Button size="small" variant="contained" onClick={() => onApply(resolveToFootage(selected))}>
            {t('common.apply')}
          </Button>
        </Stack>
      </Box>
    </Box>
  )
}
