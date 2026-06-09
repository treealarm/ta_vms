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
import { useAppDispatch, useAppSelector } from '../store'
import { LocalizationProvider } from '@mui/x-date-pickers/LocalizationProvider'
import { DateTimePicker } from '@mui/x-date-pickers/DateTimePicker'
import { AdapterDayjs } from '@mui/x-date-pickers/AdapterDayjs'
import dayjs, { Dayjs } from 'dayjs'

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
  const acceptRef = useRef<number | null>(null)

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

  useEffect(() => () => { if (acceptRef.current) clearTimeout(acceptRef.current) }, [])

  useEffect(() => {
    if (!open || !queryTime || loaded) return
    let cancelled = false
    const load = async () => {
      setLoading(true)
      await dispatch(fetchRecordingRanges({ cameraId, timestamp: queryTime, limit: 50 }))
      if (!cancelled) { setLoading(false); setLoaded(true) }
    }
    void load()
    return () => { cancelled = true }
  }, [cameraId, dispatch, loaded, open, queryTime])

  const recordingsKey = queryTime ? `${cameraId}:${queryTime}` : ''
  const EMPTY: RecordingRangeDto[] = []
  const data = useAppSelector((s) => s.fs.recordingRangesByKey[recordingsKey]?.ranges ?? EMPTY)

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

  const requestAt = (time: string) => {
    const d = dayjs(time)
    if (!d.isValid()) return
    const iso = d.toISOString()
    setQueryTime(iso); setPickerTime(d); setDisplayTime(iso); setLoaded(false)
    onTimeChange?.(iso)
  }

  const handleAccept = (value: Dayjs | null) => {
    if (!value) return
    const iso = value.toISOString()
    if (acceptRef.current) clearTimeout(acceptRef.current)
    acceptRef.current = window.setTimeout(() => { requestAt(iso); onTimeChange?.(iso) }, 1500)
  }

  const handlePrev = () => {
    const first = data[0]
    if (first?.recordings[0]) requestAt(new Date(first.recordings[0].startTime).toISOString())
  }
  const handleNext = () => {
    const last = data[data.length - 1]
    if (last?.recordings[0]) requestAt(new Date(last.recordings[0].startTime).toISOString())
  }
  const openCalendar = (anchor: HTMLElement) => {
    setPickerTime(dayjs(displayTime))
    setPickerAnchor(anchor)
  }

  useImperativeHandle(ref, () => ({ prev: handlePrev, next: handleNext, openCalendar }))

  if (!open) return null

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
      <Box sx={{ flex: 1, overflowY: 'auto', minHeight: 0 }}>
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
            {filteredData.map((rec, index) => {
              const first = rec.recordings[0]
              if (!first) return null
              const last = rec.recordings[rec.recordings.length - 1]
              const prev = filteredData[index - 1]
              const showDate = index === 0 || new Date(first.startTime).toDateString() !== new Date(prev?.recordings[0]?.startTime ?? '').toDateString()
              const isNearest = rec.recordings.some((r) => r.filePath === filePath)
              const isSelected = selected === first.startTime

              return (
                <div key={`${first.startTime}:${last?.endTime ?? ''}`}>
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
                        primary={fmtTime(first.startTime)}
                        slotProps={{
                          primary: {
                            variant: 'body2',
                            noWrap: true,
                            sx: {
                              fontVariantNumeric: 'tabular-nums',
                              fontSize: '0.76rem', lineHeight: 1.25,
                              color: dark ? (isSelected ? W : alpha(W, 0.8)) : undefined,
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
        <Box sx={{ px: 1.5, pt: 1, pb: 0.5, bgcolor: 'primary.main' }}>
          <Typography sx={{ fontSize: '0.7rem', color: 'primary.contrastText', fontWeight: 600, letterSpacing: '0.05em', textTransform: 'uppercase' }}>
            {t('recordings.jumpToDateTime')}
          </Typography>
        </Box>
        <Box sx={{ p: 1 }}>
          <LocalizationProvider dateAdapter={AdapterDayjs}>
            <DateTimePicker
              value={pickerTime}
              onChange={(v) => setPickerTime(v)}
              onAccept={(v) => { handleAccept(v); setPickerAnchor(null) }}
              timeSteps={{ minutes: 1, seconds: 1 }}
              views={['day', 'hours', 'minutes', 'seconds']}
              format="YYYY-MM-DD HH:mm:ss"
              reduceAnimations
              slotProps={{
                textField: {
                  size: 'small',
                  autoFocus: true,
                  sx: { '& input': { fontSize: '0.8rem', py: 0.6 } },
                },
                actionBar: { actions: ['accept', 'cancel'] },
              }}
            />
          </LocalizationProvider>
        </Box>
      </Popover>
    </Box>
  )
})

export default RecordingsList
