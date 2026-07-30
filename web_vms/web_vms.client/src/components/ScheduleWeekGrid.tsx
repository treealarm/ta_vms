import { useMemo, useRef, useState } from 'react'
import { Box, Button, IconButton, Stack, TextField, Tooltip, Typography } from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteIcon from '@mui/icons-material/Delete'
import { useTranslation } from 'react-i18next'
import type { ScheduleWindowDto } from '../dto/dtos'
import {
  DAY,
  PAINT_STEP,
  WEEK_DAYS,
  addRange,
  applySpan,
  covers,
  dateKey,
  formatTime,
  isDateRow,
  maskTimeInput,
  normalize,
  parseMaskedTime,
  rowValue,
  rowsToWindows,
  sameRanges,
  snapCell,
  spanBetween,
  subtractRange,
  weeklyKey,
  windowsToRows,
  type Range,
  type RowKey,
} from '../utils/scheduleWindows'

// --- pieces -----------------------------------------------------------------------------------

type TimeFieldProps = {
  label: string
  seconds: number
  /** Returns false to reject the value (e.g. start past end), which marks the field invalid. */
  onCommit: (seconds: number) => boolean
}

/**
 * Exact time entry. Digits alone are enough — the colons are inserted as you type ("0930" shows as
 * "09:30") and missing trailing parts are zero-filled on commit.
 *
 * MUI X TimeField was the obvious alternative, but it cannot express the exclusive end-of-day
 * 24:00:00 that a full-day window needs, so this stays a masked TextField.
 *
 * Remounted by the parent via key when the committed value changes, so it needs no sync effect.
 */
function TimeField({ label, seconds, onCommit }: TimeFieldProps) {
  const [text, setText] = useState(() => formatTime(seconds))
  const [bad, setBad] = useState(false)

  const commit = () => {
    const parsed = parseMaskedTime(text)
    if (parsed === null || !onCommit(parsed)) { setBad(true); return }
    setBad(false)
    // Show the committed value in full, so a padded entry like "09" reads back as 09:00.
    setText(formatTime(parsed))
  }

  return (
    <TextField
      label={label}
      value={text}
      onChange={(e) => { setText(maskTimeInput(e.target.value)); setBad(false) }}
      onBlur={commit}
      onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); commit() } }}
      error={bad}
      size="small"
      slotProps={{ htmlInput: { inputMode: 'numeric', placeholder: 'HH:MM:SS' } }}
      sx={{ width: 108 }}
    />
  )
}

function HourRuler({ labelWidth }: { labelWidth: number }) {
  return (
    <Stack direction="row" spacing={1} alignItems="center">
      <Box sx={{ width: labelWidth, flexShrink: 0 }} />
      <Box sx={{ display: 'flex', flex: 1, minWidth: 0 }}>
        {Array.from({ length: 12 }, (_, i) => (
          <Typography key={i} variant="caption" color="text.secondary" sx={{ flex: 1, minWidth: 0 }}>
            {String(i * 2).padStart(2, '0')}
          </Typography>
        ))}
      </Box>
      <Box sx={{ width: 40, flexShrink: 0 }} />
    </Stack>
  )
}

type StripProps = {
  label: string
  ranges: Range[]
  labelWidth: number
  selected: boolean
  disabled?: boolean
  onSelect: () => void
  onDragStart: (second: number) => void
  onDragTo: (second: number) => void
  onDragEnd: () => void
  action?: React.ReactNode
}

/** One day as a continuous 24 h track. Ranges are drawn as bars, so a value entered with second
 *  precision shows up exactly instead of being rounded to a cell. */
function DayStrip({
  label, ranges, labelWidth, selected, disabled, onSelect, onDragStart, onDragTo, onDragEnd, action,
}: StripProps) {
  const secondAt = (e: React.PointerEvent<HTMLElement>) => {
    const rect = e.currentTarget.getBoundingClientRect()
    if (rect.width <= 0) return 0
    const frac = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width))
    return frac * DAY
  }

  return (
    <Stack direction="row" spacing={1} alignItems="center">
      <Typography
        variant="caption"
        onClick={onSelect}
        sx={{
          width: labelWidth,
          flexShrink: 0,
          cursor: 'pointer',
          fontWeight: selected ? 600 : 400,
          color: selected ? 'text.primary' : 'text.secondary',
        }}
      >
        {label}
      </Typography>

      <Box
        onPointerDown={(e) => {
          if (disabled || e.button !== 0) return
          e.preventDefault()
          onSelect()
          // Capturing the pointer keeps every move and the release on this track even when the
          // cursor wanders off it, which is what used to break a drag mid-way.
          e.currentTarget.setPointerCapture(e.pointerId)
          onDragStart(secondAt(e))
        }}
        onPointerMove={(e) => {
          if (disabled || !e.currentTarget.hasPointerCapture(e.pointerId)) return
          onDragTo(secondAt(e))
        }}
        onPointerUp={(e) => {
          if (e.currentTarget.hasPointerCapture(e.pointerId))
            e.currentTarget.releasePointerCapture(e.pointerId)
          onDragEnd()
        }}
        onPointerCancel={onDragEnd}
        onLostPointerCapture={onDragEnd}
        sx={{
          position: 'relative',
          flex: 1,
          minWidth: 0,
          height: 22,
          bgcolor: 'action.hover',
          opacity: disabled ? 0.5 : 1,
          cursor: disabled ? 'default' : 'pointer',
          outline: selected ? 1 : 0,
          outlineColor: 'primary.light',
          outlineStyle: 'solid',
        }}
      >
        {/* Two-hour gridlines */}
        {Array.from({ length: 11 }, (_, i) => (
          <Box
            key={i}
            sx={{
              position: 'absolute', top: 0, bottom: 0, width: '1px',
              left: `${((i + 1) * 2) / 24 * 100}%`, bgcolor: 'divider',
            }}
          />
        ))}
        {ranges.map((r, i) => (
          <Box
            key={i}
            sx={{
              position: 'absolute', top: 0, bottom: 0,
              left: `${(r.start / DAY) * 100}%`,
              width: `${((r.end - r.start) / DAY) * 100}%`,
              bgcolor: 'primary.main',
            }}
          />
        ))}
      </Box>

      <Box sx={{ width: 40, flexShrink: 0 }}>{action}</Box>
    </Stack>
  )
}

type Props = {
  windows: ScheduleWindowDto[]
  onChange: (windows: ScheduleWindowDto[]) => void
  disabled?: boolean
}

/**
 * Editor for a schedule's windows: seven weekday tracks plus any number of single-date tracks.
 * The mouse paints in 15-minute steps for a quick rough selection; the list on the right edits the
 * selected day's ranges down to the second, and painting elsewhere never rounds those exact values.
 */
export default function ScheduleWeekGrid({ windows, onChange, disabled }: Props) {
  const { t, i18n } = useTranslation()

  // Dates the user added that have nothing painted yet: they produce no windows, so they would
  // vanish on the next render without this.
  const [extraDates, setExtraDates] = useState<string[]>([])
  const [newDate, setNewDate] = useState('')
  const [selectedRow, setSelectedRow] = useState<RowKey>(weeklyKey(1))
  const drag = useRef<{ key: RowKey; anchor: Range; base: Range[]; paint: boolean } | null>(null)

  const rows = useMemo(() => windowsToRows(windows), [windows])

  const dateRowKeys = useMemo(() => {
    const keys = new Set<RowKey>([...rows.keys()].filter(isDateRow))
    for (const d of extraDates) keys.add(dateKey(d))
    return [...keys].sort((a, b) => a.localeCompare(b))
  }, [rows, extraDates])

  const dayName = (day: number) => {
    // 2024-01-07 is a Sunday, so adding the proto day index lands on the right weekday.
    const d = new Date(Date.UTC(2024, 0, 7 + day))
    return d.toLocaleDateString(i18n.language, { weekday: 'short', timeZone: 'UTC' })
  }

  const rowLabel = (key: RowKey) => isDateRow(key) ? rowValue(key) : dayName(Number(rowValue(key)))
  const rangesOf = (key: RowKey) => rows.get(key) ?? []

  const setRow = (key: RowKey, ranges: Range[]) => {
    const next = new Map(rows)
    // Keep empty rows out of the map: an empty row carries no windows anyway.
    if (ranges.length === 0) next.delete(key)
    else next.set(key, ranges)
    onChange(rowsToWindows(next))
  }

  /** Starts a drag on the cell under the cursor. Whether the whole drag paints or erases is decided
   *  here, from whether that first cell was already covered. */
  const dragStart = (key: RowKey, second: number) => {
    const anchor = snapCell(second)
    const base = rangesOf(key)
    const paint = !covers(base, anchor.start + PAINT_STEP / 2)
    drag.current = { key, anchor, base, paint }
    setRow(key, applySpan(base, anchor, paint))
  }

  /** Extends the drag to the cursor. The result is always recomputed from the ranges the row had at
   *  drag start, so a move that skips cells cannot leave a hole and dragging back shrinks again. */
  const dragTo = (second: number) => {
    const state = drag.current
    if (!state) return

    const span = spanBetween(state.anchor, snapCell(second))
    const next = applySpan(state.base, span, state.paint)
    if (sameRanges(next, rangesOf(state.key))) return
    setRow(state.key, next)
  }

  const dragEnd = () => { drag.current = null }

  const editRange = (key: RowKey, index: number, patch: Partial<Range>): boolean => {
    const ranges = rangesOf(key)
    const target = ranges[index]
    if (!target) return false
    const updated = { ...target, ...patch }
    if (updated.end <= updated.start) return false
    setRow(key, normalize([...ranges.slice(0, index), updated, ...ranges.slice(index + 1)]))
    return true
  }

  const addRow = (key: RowKey) => {
    const ranges = rangesOf(key)
    const last = ranges[ranges.length - 1]
    // Append after the existing ranges, or default to a working day.
    const start = last ? Math.min(DAY - 3600, last.end + 3600) : 9 * 3600
    setRow(key, addRange(ranges, { start, end: Math.min(DAY, start + 3600) }))
  }

  const addDate = () => {
    if (!newDate) return
    setExtraDates((prev) => prev.includes(newDate) ? prev : [...prev, newDate])
    setSelectedRow(dateKey(newDate))
    setNewDate('')
  }

  const removeDate = (key: RowKey) => {
    setExtraDates((prev) => prev.filter((d) => d !== rowValue(key)))
    const next = new Map(rows)
    next.delete(key)
    onChange(rowsToWindows(next))
  }

  const selectedRanges = rangesOf(selectedRow)
  const labelWidth = 88

  const strip = (key: RowKey, action?: React.ReactNode) => (
    <DayStrip
      key={key}
      label={rowLabel(key)}
      ranges={rangesOf(key)}
      labelWidth={labelWidth}
      selected={selectedRow === key}
      disabled={disabled}
      onSelect={() => setSelectedRow(key)}
      onDragStart={(second) => dragStart(key, second)}
      onDragTo={dragTo}
      onDragEnd={dragEnd}
      action={action}
    />
  )

  return (
    <Stack direction="row" spacing={2} alignItems="flex-start" flexWrap="wrap" useFlexGap sx={{ userSelect: 'none' }}>
      <Stack spacing={2} sx={{ flex: 1, minWidth: 420 }}>
        <Box>
          <Typography variant="subtitle2" gutterBottom>{t('schedule.weekly')}</Typography>
          <Stack spacing={0.5}>
            <HourRuler labelWidth={labelWidth} />
            {WEEK_DAYS.map((day) => strip(weeklyKey(day)))}
          </Stack>
        </Box>

        <Box>
          <Typography variant="subtitle2" gutterBottom>{t('schedule.dates')}</Typography>
          <Typography variant="caption" color="text.secondary" display="block" sx={{ mb: 1 }}>
            {t('schedule.datesHint')}
          </Typography>
          <Stack spacing={0.5}>
            {dateRowKeys.length > 0 && <HourRuler labelWidth={labelWidth} />}
            {dateRowKeys.map((key) => strip(key, (
              <IconButton size="small" disabled={disabled} onClick={() => removeDate(key)}>
                <DeleteIcon fontSize="small" />
              </IconButton>
            )))}
          </Stack>
          <Stack direction="row" spacing={1} alignItems="center" sx={{ mt: 1 }}>
            <TextField
              type="date"
              size="small"
              value={newDate}
              onChange={(e) => setNewDate(e.target.value)}
              disabled={disabled}
              slotProps={{ inputLabel: { shrink: true } }}
              label={t('schedule.addDate')}
              sx={{ width: 190 }}
            />
            <Button size="small" onClick={addDate} disabled={disabled || !newDate}>
              {t('schedule.add')}
            </Button>
          </Stack>
        </Box>
      </Stack>

      {/* Exact ranges of the selected track */}
      <Stack spacing={1} sx={{ width: 300, flexShrink: 0 }}>
        <Stack direction="row" spacing={1} alignItems="center">
          <Typography variant="subtitle2" sx={{ flex: 1 }}>
            {t('schedule.exactFor', { row: rowLabel(selectedRow) })}
          </Typography>
          <Tooltip title={t('schedule.addRange')}>
            <IconButton size="small" disabled={disabled} onClick={() => addRow(selectedRow)}>
              <AddIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        </Stack>

        <Typography variant="caption" color="text.secondary">
          {t('schedule.exactHint')}
        </Typography>

        {selectedRanges.length === 0 ? (
          <Typography variant="body2" color="text.secondary">{t('schedule.noRanges')}</Typography>
        ) : (
          selectedRanges.map((r, i) => (
            <Stack key={`${r.start}-${r.end}`} direction="row" spacing={1} alignItems="center">
              <TimeField
                key={`start-${r.start}-${r.end}`}
                label={t('schedule.from')}
                seconds={r.start}
                onCommit={(v) => editRange(selectedRow, i, { start: v })}
              />
              <TimeField
                key={`end-${r.start}-${r.end}`}
                label={t('schedule.to')}
                seconds={r.end}
                onCommit={(v) => editRange(selectedRow, i, { end: v })}
              />
              <IconButton
                size="small"
                disabled={disabled}
                onClick={() => setRow(selectedRow, subtractRange(selectedRanges, r))}
              >
                <DeleteIcon fontSize="small" />
              </IconButton>
            </Stack>
          ))
        )}
      </Stack>
    </Stack>
  )
}
