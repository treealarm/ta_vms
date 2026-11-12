import { useCallback, useEffect, useMemo, useState } from 'react'
import {
  Alert, Box, Button, CircularProgress, MenuItem, Stack, Switch, Table, TableBody, TableCell,
  TableHead, TableRow, TextField, Tooltip, Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'

import { setDigitalInputArmMode } from '../../api/deviceIoApi'
import { listGuardSensors } from '../../api/guardingApi'
import { errorText } from '../../api/requestJson'
import type { GuardSensorCursorDto, GuardSensorDto } from '../../dto/dtos'
import { useAppDispatch, useAppSelector } from '../../store'
import { loadSpaces } from '../../store/spatialSlice'
import { treeOf } from '../maps/spatialUtils'

const STORAGE_KEY = 'guarding.space'
const PAGE_SIZE = 50

// The operator's guarding panel: the sensors of one space and everything under it, with the switch
// that arms them. The list is the server's — the browser never enumerates cameras — so a deployment
// of a hundred thousand cameras costs one page per view, and the page is already filtered by what
// the caller may see.
export default function GuardingPanel() {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const spaces = useAppSelector((s) => s.spatial.spaces)
  const spatialLoaded = useAppSelector((s) => s.spatial.loaded)

  const tree = useMemo(() => treeOf(spaces), [spaces])
  const [spaceId, setSpaceId] = useState<string>(
    () => localStorage.getItem(STORAGE_KEY) ?? '',
  )

  // The remembered space may be gone (an administrator removed it); the first root stands in.
  const selected = spaces.find((s) => s.id === spaceId) ?? tree[0]?.space ?? null
  const effectiveSpaceId = selected?.id ?? ''

  const [rows, setRows] = useState<GuardSensorDto[]>([])
  // The cursors the loaded pages were asked with, so a reload rebuilds the same view instead of
  // throwing the operator back to the first page.
  const [cursors, setCursors] = useState<(GuardSensorCursorDto | null)[]>([null])
  const [next, setNext] = useState<GuardSensorCursorDto | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!spatialLoaded) void dispatch(loadSpaces())
  }, [dispatch, spatialLoaded])

  const fetchPage = useCallback(
    (cursor: GuardSensorCursorDto | null) =>
      listGuardSensors({ spaceId: effectiveSpaceId || undefined, pageSize: PAGE_SIZE, after: cursor }),
    [effectiveSpaceId],
  )

  const loadFirst = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const page = await fetchPage(null)
      setRows(page.sensors)
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

  const reload = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const collected: GuardSensorDto[] = []
      let lastNext: GuardSensorCursorDto | null = null
      for (const cursor of cursors) {
        const page = await fetchPage(cursor)
        collected.push(...page.sensors)
        lastNext = page.hasMore ? page.next ?? null : null
        if (!page.hasMore) break
      }
      setRows(collected)
      setNext(lastNext)
      setCursors(lastNext ? cursors : [null])
    } catch (e) {
      setError(errorText(e))
    } finally {
      setLoading(false)
    }
  }, [cursors, fetchPage])

  const loadMore = useCallback(async () => {
    if (!next || loading) return
    setLoading(true)
    setError(null)
    try {
      const page = await fetchPage(next)
      setRows((current) => [...current, ...page.sensors])
      setNext(page.next ?? null)
      setCursors((current) => [...current, next])
    } catch (e) {
      setError(errorText(e))
    } finally {
      setLoading(false)
    }
  }, [fetchPage, loading, next])

  return (
    <Stack spacing={1.5} sx={{ height: '100%', minHeight: 0 }}>
      <Stack direction="row" spacing={1.5} alignItems="center" flexWrap="wrap">
        <TextField
          select
          size="small"
          label={t('guarding.space')}
          value={effectiveSpaceId}
          onChange={(e) => {
            setSpaceId(e.target.value)
            localStorage.setItem(STORAGE_KEY, e.target.value)
          }}
          sx={{ minWidth: 260 }}
        >
          {tree.map(({ space, depth }) => (
            <MenuItem key={space.id ?? space.name} value={space.id ?? ''}>
              {`${'  '.repeat(depth)}${space.name}`}
            </MenuItem>
          ))}
        </TextField>
        <Typography variant="caption" color="text.secondary">
          {t('guarding.spaceHint')}
        </Typography>
      </Stack>

      {error && <Alert severity="error" sx={{ fontSize: '0.8rem' }}>{error}</Alert>}

      <Box sx={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
        <Table size="small" stickyHeader>
          <TableHead>
            <TableRow>
              <TableCell>{t('guarding.sensor')}</TableCell>
              <TableCell>{t('guarding.camera')}</TableCell>
              <TableCell>{t('guarding.spaceColumn')}</TableCell>
              <TableCell>{t('guarding.guard')}</TableCell>
            </TableRow>
          </TableHead>
          <TableBody>
            {rows.map((row) => (
              <GuardRow key={row.objectId} row={row} onChanged={reload} />
            ))}
          </TableBody>
        </Table>

        {!loading && rows.length === 0 && (
          <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
            {t('guarding.empty')}
          </Typography>
        )}
        {loading && rows.length === 0 && (
          <Stack alignItems="center" py={3}><CircularProgress size={22} /></Stack>
        )}
      </Box>

      <Stack direction="row" alignItems="center" spacing={1}>
        {next && (
          <Button size="small" onClick={() => void loadMore()} disabled={loading}>
            {t('guarding.more')}
          </Button>
        )}
        {loading && rows.length > 0 && <CircularProgress size={16} />}
        <Box sx={{ flex: 1 }} />
        <Typography variant="caption" color="text.secondary">
          {t('guarding.loaded', { count: rows.length })}
        </Typography>
      </Stack>
    </Stack>
  )
}

// One sensor: its name, the camera it belongs to, where it stands and what its arming resolves to.
// The switch is checked by the effective state, not by the configured mode — 'inherit' under an
// armed camera means on guard — and a click is rolled back if the server refuses it.
function GuardRow({ row, onChanged }: { row: GuardSensorDto; onChanged: () => void }) {
  const { t } = useTranslation()
  const [optimistic, setOptimistic] = useState<boolean | null>(null)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (optimistic !== null && row.armed === optimistic) setOptimistic(null)
  }, [row.armed, optimistic])

  const armed = optimistic ?? row.armed

  const toggle = async () => {
    const nextState = !armed
    setPending(true)
    setError(null)
    setOptimistic(nextState)
    try {
      await setDigitalInputArmMode(row.sourceId, row.partToken, nextState ? 'armed' : 'disarmed')
      onChanged()
    } catch (e) {
      setError(errorText(e))
      setOptimistic(null)
      onChanged()
    } finally {
      setPending(false)
    }
  }

  const guardLabel =
    row.armingSource === 'space'
      ? (armed ? t('onvifSettings.deviceIoGuardOnZone') : t('onvifSettings.deviceIoGuardOffZone'))
      : row.armingSource === 'camera'
        ? (armed ? t('onvifSettings.deviceIoGuardOnCamera') : t('onvifSettings.deviceIoGuardOffCamera'))
        : (armed ? t('onvifSettings.deviceIoGuardOn') : t('onvifSettings.deviceIoGuardOff'))

  // Nothing to write the mode to, or no right to write it: the switch is shown, not usable, with
  // the reason in the tooltip instead of a click that fails.
  const blockedReason = !row.configured
    ? t('guarding.notConfigured')
    : !row.canControl
      ? t('onvifSettings.deviceIoNoControl')
      : null

  const guardSwitch = (
    <Switch
      size="small"
      checked={armed}
      disabled={pending || blockedReason !== null}
      onChange={() => void toggle()}
    />
  )

  return (
    <TableRow>
      <TableCell>
        <Typography variant="body2">{row.name}</Typography>
        {error && <Typography variant="caption" color="error.main">{error}</Typography>}
      </TableCell>
      <TableCell>
        <Typography variant="caption" color="text.secondary">{row.cameraName || row.sourceId}</Typography>
      </TableCell>
      <TableCell>
        <Typography variant="caption" color="text.secondary">{row.spaceName}</Typography>
      </TableCell>
      <TableCell>
        <Stack direction="row" alignItems="center" spacing={1}>
          {blockedReason ? (
            <Tooltip title={blockedReason}><span>{guardSwitch}</span></Tooltip>
          ) : guardSwitch}
          <Typography
            variant="caption"
            color={armed ? 'success.main' : 'text.secondary'}
            title={t('onvifSettings.deviceIoGuardHint')}
          >
            {guardLabel}
          </Typography>
        </Stack>
      </TableCell>
    </TableRow>
  )
}
