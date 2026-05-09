import { useEffect, useMemo, useState } from 'react'
import {
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  FormControlLabel,
  MenuItem,
  Stack,
  Switch,
  TextField,
  Typography
} from '@mui/material'
import ContentCopyIcon from '@mui/icons-material/ContentCopy'

import { useAppDispatch, useAppSelector } from '../store'
import { clearLogs, fetchLogs } from '../store/logSlice'
import { toLocalDatetimeInputValue } from '../dto/dtos'

const RANGE_OPTIONS = [
  { label: 'Last 60 sec', value: 60 },
  { label: 'Last 5 min', value: 300 },
  { label: 'Last 15 min', value: 900 }
]

const LEVEL_OPTIONS = ['', 'debug', 'info', 'warn', 'error']

function formatLogLine(ts: string, level: string, message: string, file: string, func: string): string {
  return `${ts} [${level}] ${message} (${file}::${func})`
}

function normalizeLogLevelKey(level: string): 'error' | 'warn' | 'info' | 'debug' | 'default' {
  const k = level.trim().toLowerCase()
  if (k === 'error') {
    return 'error'
  }
  if (k === 'warn' || k === 'warning') {
    return 'warn'
  }
  if (k === 'info') {
    return 'info'
  }
  if (k === 'debug') {
    return 'debug'
  }
  return 'default'
}

export default function LogJournalPanel() {
  const dispatch = useAppDispatch()
  const { entries, hasMore, isBusy, status } = useAppSelector((state) => state.logs)

  const [rangeSec, setRangeSec] = useState(60)
  const [limit, setLimit] = useState(200)
  const [level, setLevel] = useState('')
  const [source, setSource] = useState('')
  const [search, setSearch] = useState('')
  const [autoRefresh, setAutoRefresh] = useState(true)

  const runFetch = async () => {
    const to = new Date()
    const from = new Date(to.getTime() - rangeSec * 1000)

    await dispatch(
      fetchLogs({
        from: from.toISOString(),
        to: to.toISOString(),
        limit,
        level: level || undefined,
        source: source.trim() || undefined
      })
    )
  }

  useEffect(() => {
    void runFetch()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    if (!autoRefresh) {
      return
    }
    const timer = setInterval(() => {
      void runFetch()
    }, 3000)
    return () => clearInterval(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [autoRefresh, rangeSec, limit, level, source])

  const filteredEntries = useMemo(() => {
    const q = search.trim().toLowerCase()
    if (!q) {
      return entries
    }
    return entries.filter((e) => {
      const line = formatLogLine(e.timestamp, e.level, e.message, e.file, e.function).toLowerCase()
      return line.includes(q)
    })
  }, [entries, search])

  const onCopy = async () => {
    const text = filteredEntries
      .map((e) => formatLogLine(e.timestamp, e.level, e.message, e.file, e.function))
      .join('\n')
    if (!text) {
      return
    }
    await navigator.clipboard.writeText(text)
  }

  return (
    <Card elevation={4}>
      <CardContent sx={{ p: 1.5 }}>
        <Stack spacing={1}>
          <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap">
            <Typography variant="subtitle2">Log Journal</Typography>
            <Chip size="small" label={isBusy ? 'Loading...' : status} />
            {hasMore && <Chip size="small" color="warning" label="has more" />}
          </Stack>

          <Stack direction="row" spacing={1} flexWrap="wrap">
            <TextField
              select
              size="small"
              label="Range"
              value={rangeSec}
              onChange={(e) => setRangeSec(Number(e.target.value))}
              sx={{ minWidth: 140 }}
            >
              {RANGE_OPTIONS.map((r) => (
                <MenuItem key={r.value} value={r.value}>
                  {r.label}
                </MenuItem>
              ))}
            </TextField>

            <TextField
              size="small"
              type="number"
              label="Limit"
              value={limit}
              onChange={(e) => setLimit(Math.max(1, Number(e.target.value) || 1))}
              sx={{ width: 100 }}
            />

            <TextField
              select
              size="small"
              label="Level"
              value={level}
              onChange={(e) => setLevel(e.target.value)}
              sx={{ minWidth: 120 }}
            >
              <MenuItem value="">all</MenuItem>
              {LEVEL_OPTIONS.filter((v) => v).map((v) => (
                <MenuItem key={v} value={v}>
                  {v}
                </MenuItem>
              ))}
            </TextField>

            <TextField
              size="small"
              label="Source"
              value={source}
              onChange={(e) => setSource(e.target.value)}
              sx={{ minWidth: 160 }}
            />

            <TextField
              size="small"
              label="Search"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              sx={{ minWidth: 180, flexGrow: 1 }}
            />
            <FormControlLabel
              sx={{ ml: 0.5 }}
              control={
                <Switch size="small" checked={autoRefresh} onChange={(e) => setAutoRefresh(e.target.checked)} />
              }
              label="Auto"
            />

            <Button size="small" variant="outlined" onClick={() => void runFetch()}>
              Refresh
            </Button>
            <Button size="small" variant="outlined" onClick={() => dispatch(clearLogs())}>
              Clear
            </Button>
            <Button size="small" variant="outlined" startIcon={<ContentCopyIcon />} onClick={() => void onCopy()}>
              Copy
            </Button>
          </Stack>

          <Box className="vms-log-journal-body">
            {filteredEntries.length === 0 && (
              <Typography variant="caption" className="vms-log-journal-empty">
                No logs
              </Typography>
            )}
            {filteredEntries.map((e, idx) => (
              <Box
                key={`${e.timestamp}-${idx}`}
                className="vms-log-line"
                data-log-level={normalizeLogLevelKey(e.level)}
              >
                {formatLogLine(
                  toLocalDatetimeInputValue(new Date(e.timestamp)),
                  e.level,
                  e.message,
                  e.file,
                  e.function
                )}
              </Box>
            ))}
          </Box>
        </Stack>
      </CardContent>
    </Card>
  )
}
