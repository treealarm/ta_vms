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
import { useTranslation } from 'react-i18next'

import { useAppDispatch, useAppSelector } from '../store'
import { clearLogs, fetchLogs } from '../store/logSlice'
import { toLocalDatetimeInputValue } from '../dto/dtos'

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
  const { t } = useTranslation()
  const { entries, hasMore, isBusy, status } = useAppSelector((state) => state.logs)

  const RANGE_OPTIONS = [
    { label: t('logs.last60s'), value: 60 },
    { label: t('logs.last5m'), value: 300 },
    { label: t('logs.last15m'), value: 900 },
  ]

  const [rangeSec, setRangeSec] = useState(60)
  const [limit, setLimit] = useState(200)
  const [level, setLevel] = useState('')
  const [appId, setAppId] = useState('')
  const [sources, setSources] = useState<string[]>([])
  const [search, setSearch] = useState('')
  const [autoRefresh, setAutoRefresh] = useState(true)

  const loadSources = () => {
    fetch('/api/logger/sources')
      .then((r) => r.ok ? r.json() as Promise<string[]> : [])
      .then(setSources)
      .catch(() => {})
  }

  const runFetch = async () => {
    const to = new Date()
    const from = new Date(to.getTime() - rangeSec * 1000)

    await dispatch(
      fetchLogs({
        from: from.toISOString(),
        to: to.toISOString(),
        limit,
        level: level || undefined,
        appId: appId || undefined,
      })
    )
  }

  useEffect(() => {
    loadSources()
    void runFetch()
    if (!autoRefresh) {
      return
    }
    const timer = setInterval(() => {
      loadSources()
      void runFetch()
    }, 3000)
    return () => clearInterval(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [autoRefresh, rangeSec, limit, level, appId])

  const filteredEntries = useMemo(() => {
    const q = search.trim().toLowerCase()
    const matched = q
      ? entries.filter((e) => {
          const line = formatLogLine(e.timestamp, e.level, e.message, e.file, e.function).toLowerCase()
          return line.includes(q)
        })
      : entries
    // Newest first
    return [...matched].reverse()
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
            <Typography variant="subtitle2">{t('logs.title')}</Typography>
            <Chip size="small" label={isBusy ? t('common.loading') : status} />
            {hasMore && <Chip size="small" color="warning" label={t('logs.hasMore')} />}
          </Stack>

          <Stack direction="row" spacing={1} flexWrap="wrap">
            <TextField
              select
              size="small"
              label={t('logs.range')}
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
              label={t('logs.limit')}
              value={limit}
              onChange={(e) => setLimit(Math.max(1, Number(e.target.value) || 1))}
              sx={{ width: 100 }}
            />

            <TextField
              select
              size="small"
              label={t('logs.level')}
              value={level}
              onChange={(e) => setLevel(e.target.value)}
              sx={{ minWidth: 120 }}
            >
              <MenuItem value="">{t('logs.all')}</MenuItem>
              {LEVEL_OPTIONS.filter((v) => v).map((v) => (
                <MenuItem key={v} value={v}>
                  {v}
                </MenuItem>
              ))}
            </TextField>

            <TextField
              select
              size="small"
              label={t('logs.service')}
              value={appId}
              onChange={(e) => setAppId(e.target.value)}
              sx={{ minWidth: 140 }}
            >
              <MenuItem value="">{t('logs.all')}</MenuItem>
              {sources.map((s) => (
                <MenuItem key={s} value={s}>{s}</MenuItem>
              ))}
            </TextField>

            <TextField
              size="small"
              label={t('logs.search')}
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              sx={{ minWidth: 180, flexGrow: 1 }}
            />
            <FormControlLabel
              sx={{ ml: 0.5 }}
              control={
                <Switch size="small" checked={autoRefresh} onChange={(e) => setAutoRefresh(e.target.checked)} />
              }
              label={t('logs.auto')}
            />

            <Button size="small" variant="outlined" onClick={() => void runFetch()}>
              {t('common.refresh')}
            </Button>
            <Button size="small" variant="outlined" onClick={() => dispatch(clearLogs())}>
              {t('logs.clear')}
            </Button>
            <Button size="small" variant="outlined" startIcon={<ContentCopyIcon />} onClick={() => void onCopy()}>
              {t('logs.copy')}
            </Button>
          </Stack>

          <Box className="vms-log-journal-body">
            {filteredEntries.length === 0 && (
              <Typography variant="caption" className="vms-log-journal-empty">
                {t('logs.noLogs')}
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
