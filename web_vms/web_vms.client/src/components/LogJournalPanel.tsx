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
import type { LogEntryDto } from '../dto/dtos'

const LEVEL_OPTIONS = ['', 'debug', 'info', 'warn', 'error']

function formatLogLine(ts: string, level: string, message: string, file: string, func: string): string {
  return `${ts} [${level}] ${message} (${file}::${func})`
}

function looksLikeXml(s: string): boolean {
  const t = s.trimStart()
  return t.startsWith('<') && t.includes('>')
}

function formatXml(xml: string): string {
  try {
    const withBreaks = xml.replace(/(>)(<)(\/*)/g, '$1\n$2$3')
    let pad = 0
    return withBreaks
      .split('\n')
      .map((node) => {
        let indent = 0
        if (/^<\/\w/.test(node)) {
          pad = Math.max(pad - 1, 0)
        } else if (/^<\w[^>]*[^/]>.*$/.test(node) && !/<\/\w/.test(node)) {
          indent = 1
        }
        const line = '  '.repeat(pad) + node
        pad += indent
        return line
      })
      .join('\n')
      .trim()
  } catch {
    return xml
  }
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
  const [selected, setSelected] = useState<LogEntryDto[]>([])

  const sameEntry = (a: LogEntryDto, b: LogEntryDto) =>
    a.timestamp === b.timestamp && a.message === b.message

  const isSelected = (e: LogEntryDto) => selected.some((s) => sameEntry(s, e))

  const toggleSelect = (e: LogEntryDto, additive: boolean) => {
    setSelected((prev) => {
      if (additive) {
        return prev.some((s) => sameEntry(s, e))
          ? prev.filter((s) => !sameEntry(s, e))
          : [...prev, e]
      }
      return [e]
    })
  }

  const detailText = (e: LogEntryDto) => {
    const meta = `${toLocalDatetimeInputValue(new Date(e.timestamp))} · ${e.level}`
      + (e.file ? ` · ${e.file}` : '')
      + (e.function ? `::${e.function}` : '')
    const body = looksLikeXml(e.message) ? formatXml(e.message) : e.message
    return `${meta}\n${body}`
  }

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
    const text = selected.map(detailText).join('\n\n')
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

          <Stack direction="row" spacing={1} alignItems="flex-start">
            <Box className="vms-log-journal-body" sx={{ flex: 1, minWidth: 0, maxHeight: 'none !important', overflow: 'hidden' }}>
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
                  onClick={(ev) => toggleSelect(e, ev.ctrlKey || ev.metaKey)}
                  sx={{
                    cursor: 'pointer',
                    backgroundColor: isSelected(e) ? 'action.selected' : undefined,
                    whiteSpace: 'nowrap',
                    overflow: 'hidden',
                    textOverflow: 'ellipsis',
                  }}
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

            <Box
              className="vms-log-journal-body"
              sx={{ flex: 1, minWidth: 0, maxHeight: 'none !important' }}
            >
              {selected.length === 0 ? (
                <Typography variant="caption" className="vms-log-journal-empty">
                  {t('logs.selectHint')}
                </Typography>
              ) : (
                selected.map((s, i) => (
                  <Box key={`${s.timestamp}-${i}`} sx={{ mb: 1.5 }}>
                    <Typography variant="caption" sx={{ display: 'block', color: 'text.secondary', mb: 0.5 }}>
                      {toLocalDatetimeInputValue(new Date(s.timestamp))}
                      {' · '}{s.level}
                      {s.file ? ` · ${s.file}` : ''}
                      {s.function ? `::${s.function}` : ''}
                    </Typography>
                    <Box
                      component="pre"
                      sx={{ m: 0, whiteSpace: 'pre-wrap', wordBreak: 'break-word', fontFamily: 'monospace', fontSize: 12 }}
                    >
                      {looksLikeXml(s.message) ? formatXml(s.message) : s.message}
                    </Box>
                  </Box>
                ))
              )}
            </Box>
          </Stack>
        </Stack>
      </CardContent>
    </Card>
  )
}
