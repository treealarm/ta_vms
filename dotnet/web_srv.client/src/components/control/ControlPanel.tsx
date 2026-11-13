import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  Alert, Box, Button, Chip, CircularProgress, List, ListItemButton, ListItemIcon, ListItemText,
  ListSubheader, MenuItem, Stack, TextField, ToggleButton, ToggleButtonGroup, Typography,
} from '@mui/material'
import PowerOutlinedIcon from '@mui/icons-material/PowerOutlined'
import SensorsOutlinedIcon from '@mui/icons-material/SensorsOutlined'
import VideocamOutlinedIcon from '@mui/icons-material/VideocamOutlined'
import ShieldIcon from '@mui/icons-material/Shield'
import ShieldOutlinedIcon from '@mui/icons-material/ShieldOutlined'
import { useTranslation } from 'react-i18next'

import { listControlObjects } from '../../api/controlApi'
import { errorText } from '../../api/requestJson'
import type { ControlObjectCursorDto, ControlObjectDto, ControlObjectKind } from '../../dto/dtos'
import { useAppDispatch, useAppSelector } from '../../store'
import { loadSpaces } from '../../store/spatialSlice'
import { treeOf } from '../maps/spatialUtils'
import ObjectControl from './ObjectControl'
import NodeGuard from './NodeGuard'

const STORAGE_KEY = 'control.space'
const PAGE_SIZE = 50
// The select needs a value that is not the empty string: MUI renders '' as nothing at all.
const ALL_SPACES = 'all'

// The operator's Control panel: the objects of one space — cameras, their sensors and their relays —
// with what each one currently says and what can be done with it. The list is the server's, so a
// deployment of a hundred thousand cameras costs one page per view, and the page is already
// filtered by what the caller may see. A row is an object; its actions live in the pane beside it.
export default function ControlPanel() {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const spaces = useAppSelector((s) => s.spatial.spaces)
  const spatialLoaded = useAppSelector((s) => s.spatial.loaded)

  const tree = useMemo(() => treeOf(spaces), [spaces])
  // ALL_SPACES asks for every space the caller may see; a stored id that no longer exists falls
  // back to it.
  const [spaceId, setSpaceId] = useState<string>(
    () => localStorage.getItem(STORAGE_KEY) || ALL_SPACES,
  )
  const effectiveSpaceId = spaces.some((s) => s.id === spaceId) ? spaceId : ''

  const [kind, setKind] = useState<string>('')
  const [rows, setRows] = useState<ControlObjectDto[]>([])
  // The cursors the loaded pages were asked with, so a reload rebuilds the same view instead of
  // throwing the operator back to the first page.
  const [cursors, setCursors] = useState<(ControlObjectCursorDto | null)[]>([null])
  const [next, setNext] = useState<ControlObjectCursorDto | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)

  useEffect(() => {
    if (!spatialLoaded) void dispatch(loadSpaces())
  }, [dispatch, spatialLoaded])

  const fetchPage = useCallback(
    (cursor: ControlObjectCursorDto | null) =>
      listControlObjects({
        spaceId: effectiveSpaceId || undefined,
        kind: kind || undefined,
        pageSize: PAGE_SIZE,
        after: cursor,
      }),
    [effectiveSpaceId, kind],
  )

  const loadFirst = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const page = await fetchPage(null)
      setRows(page.objects)
      setCursors([null])
      setNext(page.next ?? null)
    } catch (e) {
      setRows([])
      setNext(null)
      setError(errorText(e))
    } finally {
      setLoading(false)
    }
  }, [fetchPage])

  useEffect(() => {
    void loadFirst()
  }, [loadFirst])

  // A silent reload is the poll's: it must not flash the spinner or a stale error on every tick.
  const reload = useCallback(async (silent = false) => {
    if (!silent) {
      setLoading(true)
      setError(null)
    }
    try {
      const collected: ControlObjectDto[] = []
      let lastNext: ControlObjectCursorDto | null = null
      for (const cursor of cursors) {
        const page = await fetchPage(cursor)
        collected.push(...page.objects)
        lastNext = page.hasMore ? page.next ?? null : null
        if (!page.hasMore) break
      }
      setRows(collected)
      setNext(lastNext)
      setCursors(lastNext ? cursors : [null])
    } catch (e) {
      if (!silent) setError(errorText(e))
    } finally {
      if (!silent) setLoading(false)
    }
  }, [cursors, fetchPage])

  // A relay does not report its new state in the same breath as the command, and the list has no
  // stream of its own: without a tick the row would keep the snapshot it was read with until the
  // next action, and an operator who leaves a relay and comes back would find it unchanged.
  const reloadRef = useRef(reload)
  useEffect(() => {
    reloadRef.current = reload
  }, [reload])
  useEffect(() => {
    const timer = window.setInterval(() => void reloadRef.current(true), 5000)
    return () => window.clearInterval(timer)
  }, [])

  const loadMore = useCallback(async () => {
    if (!next || loading) return
    setLoading(true)
    setError(null)
    try {
      const page = await fetchPage(next)
      setRows((current) => [...current, ...page.objects])
      setNext(page.next ?? null)
      setCursors((current) => [...current, next])
    } catch (e) {
      setError(errorText(e))
    } finally {
      setLoading(false)
    }
  }, [fetchPage, loading, next])

  // The selected row survives a reload (the object id is stable); a filtered-out one falls back to
  // the first row, so the pane is never empty while the list is not.
  const selected = rows.find((r) => r.objectId === selectedId) ?? rows[0] ?? null

  // "All" is not one heap: cameras, sensors and relays are one control surface, but a reader
  // scans one kind at a time, and a page of a hundred mixed rows would not say where cameras end.
  const groups = useMemo(() => {
    const order: ControlObjectKind[] = ['camera', 'digital_input', 'relay']
    return order
      .map((kind) => ({ kind, rows: rows.filter((row) => row.kind === kind) }))
      .filter((group) => group.rows.length > 0)
  }, [rows])

  return (
    <Stack spacing={1.5} sx={{ height: '100%', minHeight: 0 }}>
      <Stack direction="row" spacing={1.5} alignItems="center" flexWrap="wrap">
        <TextField
          select
          size="small"
          label={t('control.space')}
          value={spaceId === ALL_SPACES || effectiveSpaceId ? spaceId : ALL_SPACES}
          onChange={(e) => {
            setSpaceId(e.target.value)
            localStorage.setItem(STORAGE_KEY, e.target.value)
            setSelectedId(null)
          }}
          sx={{ minWidth: 260 }}
        >
          <MenuItem value={ALL_SPACES}>{t('control.allSpaces')}</MenuItem>
          {tree.map(({ space, depth }) => (
            <MenuItem key={space.id ?? space.name} value={space.id ?? ''}>
              {`${'  '.repeat(depth)}${space.name}`}
            </MenuItem>
          ))}
        </TextField>
        <ToggleButtonGroup
          size="small"
          exclusive
          value={kind}
          onChange={(_, value: string | null) => {
            if (value === null) return
            setKind(value)
            setSelectedId(null)
          }}
        >
          <ToggleButton value="">{t('control.kindAll')}</ToggleButton>
          <ToggleButton value="camera">{t('control.kindCamera')}</ToggleButton>
          <ToggleButton value="digital_input">{t('control.kindSensor')}</ToggleButton>
          <ToggleButton value="relay">{t('control.kindRelay')}</ToggleButton>
        </ToggleButtonGroup>
        <Typography variant="caption" color="text.secondary">
          {t('control.spaceHint')}
        </Typography>
      </Stack>

      {error && <Alert severity="error" sx={{ fontSize: '0.8rem' }}>{error}</Alert>}

      <Box sx={{ flex: 1, minHeight: 0, display: 'flex' }}>
        <Box
          sx={{
            width: 380, flexShrink: 0, minHeight: 0, display: 'flex', flexDirection: 'column',
            borderRight: 1, borderColor: 'divider',
          }}
        >
          <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
            {groups.map((group) => (
              <List
                key={group.kind}
                dense
                disablePadding
                subheader={(
                  <ListSubheader disableSticky sx={{ lineHeight: 2, bgcolor: 'transparent' }}>
                    {`${t(groupTitle(group.kind))} (${group.rows.length})`}
                  </ListSubheader>
                )}
              >
                {group.rows.map((row) => (
                  <ListItemButton
                    key={row.objectId}
                    selected={row.objectId === selected?.objectId}
                    onClick={() => setSelectedId(row.objectId)}
                  >
                    <ListItemIcon sx={{ minWidth: 32 }}>{kindIcon(row)}</ListItemIcon>
                    <ListItemText
                      primary={row.name}
                      secondary={row.kind === 'camera' ? row.spaceName : row.cameraName}
                      primaryTypographyProps={{ noWrap: true }}
                      secondaryTypographyProps={{ noWrap: true }}
                    />
                    {row.kind === 'relay' ? (
                      // The list states what a relay says; the switch is the pane's, so a row
                      // cannot be flipped while the operator is only scanning the space.
                      <Chip
                        size="small"
                        variant="outlined"
                        color={row.state === 'active' ? 'warning' : 'default'}
                        label={row.state === 'active' ? t('control.stateActive') : t('control.stateInactive')}
                      />
                    ) : row.hasArming ? (
                      row.armed
                        ? <ShieldIcon fontSize="small" color="success" titleAccess={t('control.guard')} />
                        : <ShieldOutlinedIcon fontSize="small" color="disabled" titleAccess={t('control.guard')} />
                    ) : null}
                  </ListItemButton>
                ))}
              </List>
            ))}

            {!loading && rows.length === 0 && (
              <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
                {t('control.empty')}
              </Typography>
            )}
            {loading && rows.length === 0 && (
              <Stack alignItems="center" py={3}><CircularProgress size={22} /></Stack>
            )}
          </Box>

          <Stack direction="row" alignItems="center" spacing={1} sx={{ px: 1, py: 0.75, borderTop: 1, borderColor: 'divider' }}>
            {next && (
              <Button size="small" onClick={() => void loadMore()} disabled={loading}>
                {t('control.more')}
              </Button>
            )}
            {loading && rows.length > 0 && <CircularProgress size={16} />}
            <Box sx={{ flex: 1 }} />
            <Typography variant="caption" color="text.secondary">
              {t('control.loaded', { count: rows.length })}
            </Typography>
          </Stack>
        </Box>

        <Box sx={{ flex: 1, minWidth: 0, overflowY: 'auto', p: 2 }}>
          {/* The space's own guard heads the pane: it reaches every row below it, and nothing in
              the list stands in for it. */}
          {effectiveSpaceId && (
            <Stack spacing={1} sx={{ pb: 2, mb: 2, borderBottom: 1, borderColor: 'divider' }}>
              <Box>
                <Typography variant="subtitle1" fontWeight={600}>
                  {spaces.find((s) => s.id === effectiveSpaceId)?.name ?? t('control.space')}
                </Typography>
                <Typography variant="caption" color="text.secondary">
                  {t('control.spaceGuardHint')}
                </Typography>
              </Box>
              <NodeGuard objectId={effectiveSpaceId} onChanged={() => void reload()} />
            </Stack>
          )}
          {selected ? (
            <ObjectControl key={selected.objectId} row={selected} onChanged={() => void reload()} heading />
          ) : (
            <Typography variant="body2" color="text.secondary">
              {t('control.nothingSelected')}
            </Typography>
          )}
        </Box>
      </Box>
    </Stack>
  )
}

// A section heading names its kind in the plural; the pane's heading names one object in the
// singular, and the two must not read as a mistake.
function groupTitle(kind: ControlObjectKind) {
  if (kind === 'camera') return 'control.kindCamera'
  if (kind === 'relay') return 'control.kindRelay'
  return 'control.kindSensor'
}

function kindIcon(row: ControlObjectDto) {
  if (row.kind === 'camera') return <VideocamOutlinedIcon fontSize="small" />
  if (row.kind === 'relay') return <PowerOutlinedIcon fontSize="small" />
  return <SensorsOutlinedIcon fontSize="small" />
}
