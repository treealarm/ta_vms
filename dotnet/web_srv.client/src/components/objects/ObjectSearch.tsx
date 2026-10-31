import { useState } from 'react'
import {
  Box, Button, Divider, IconButton, LinearProgress, List, ListItemButton, Stack, TextField,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import SearchIcon from '@mui/icons-material/Search'
import { useTranslation } from 'react-i18next'
import { toIsoUtc, toLocalDatetimeInputValue } from '../../dto/dtos'
import type { SpaceDto } from '../../dto/dtos'
import type { MapMarker, MapPropertyFilter } from '../../dto/mapDtos'
import { useAppDispatch, useAppSelector } from '../../store'
import { clearSearch, searchByName, searchByProps } from '../../store/searchSlice'
import { clearTracks, loadTracks } from '../../store/tracksSlice'
import { spaceBox } from '../../map/geo'
import { sortMarkers } from '../../map/nodeKind'
import KindIcon from '../../map/KindIcon'

type Props = {
  /** The space the tracks are asked about — the one open on the canvas. */
  space: SpaceDto
  /** A result was picked: unfold the tree down to it, open its space and select it. */
  onSelect: (marker: MapMarker) => void
}

type PropRow = { propName: string; strVal: string }

/** The tracks open on the hour that just passed; the operator widens the period from there. */
const DEFAULT_PERIOD_MS = 60 * 60_000

/**
 * Operator → Search: nodes by name or by "property = value", and the tracks of the open space
 * over a period. A result is a way into the tree — the page unfolds the path and selects the node
 * — and the tracks are drawn on the canvas by `TracksLayer` until they are cleared.
 */
export default function ObjectSearch({ space, onSelect }: Props) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()

  const mode = useAppSelector((s) => s.search.mode)
  const rows = useAppSelector((s) => s.search.rows)
  const loading = useAppSelector((s) => s.search.loading)
  const error = useAppSelector((s) => s.search.error)
  const tracks = useAppSelector((s) => s.tracks.tracks)
  const tracksSpaceId = useAppSelector((s) => s.tracks.spaceId)
  const tracksLoading = useAppSelector((s) => s.tracks.loading)
  const tracksError = useAppSelector((s) => s.tracks.error)

  const [name, setName] = useState('')
  const [props, setProps] = useState<PropRow[]>([{ propName: '', strVal: '' }])
  const [periodFrom, setPeriodFrom] = useState(
    () => toLocalDatetimeInputValue(new Date(Date.now() - DEFAULT_PERIOD_MS)),
  )
  const [periodTo, setPeriodTo] = useState(() => toLocalDatetimeInputValue(new Date()))

  // Only rows with both halves asked for count: an empty value would match every property name.
  const usableProps: MapPropertyFilter[] = props
    .map((row) => ({ propName: row.propName.trim(), strVal: row.strVal.trim() }))
    .filter((row) => row.propName !== '' && row.strVal !== '')

  const findByProps = () => {
    if (usableProps.length > 0) void dispatch(searchByProps(usableProps))
  }

  const showTracks = () => {
    const timeStart = toIsoUtc(periodFrom)
    const timeEnd = toIsoUtc(periodTo)
    if (!space.id || !timeStart || !timeEnd) return

    void dispatch(loadTracks({ spaceId: space.id, box: spaceBox(space), timeStart, timeEnd }))
  }

  const here = tracksSpaceId === space.id ? tracks : []

  return (
    <Box sx={{ p: 1, display: 'flex', flexDirection: 'column', gap: 1 }}>
      <Stack direction="row" spacing={0.5} alignItems="center">
        <TextField
          size="small"
          fullWidth
          label={t('maps.search.name')}
          value={name}
          onChange={(event) => setName(event.target.value)}
          onKeyDown={(event) => { if (event.key === 'Enter' && name.trim()) void dispatch(searchByName(name.trim())) }}
        />
        <IconButton
          size="small"
          title={t('maps.search.findByName')}
          disabled={!name.trim() || loading}
          onClick={() => void dispatch(searchByName(name.trim()))}
        >
          <SearchIcon fontSize="small" />
        </IconButton>
      </Stack>

      <Typography variant="overline" color="text.secondary">{t('maps.search.byProps')}</Typography>
      {props.map((row, index) => (
        <Stack key={index} direction="row" spacing={0.5} alignItems="center">
          <TextField
            size="small"
            label={t('maps.search.propName')}
            value={row.propName}
            onChange={(event) => setProps(updateRow(props, index, { propName: event.target.value }))}
          />
          <TextField
            size="small"
            label={t('maps.search.propValue')}
            value={row.strVal}
            onChange={(event) => setProps(updateRow(props, index, { strVal: event.target.value }))}
            onKeyDown={(event) => { if (event.key === 'Enter') findByProps() }}
          />
          <IconButton
            size="small"
            disabled={props.length === 1}
            onClick={() => setProps(props.filter((_, i) => i !== index))}
          >
            <DeleteOutlineIcon fontSize="small" />
          </IconButton>
        </Stack>
      ))}
      <Stack direction="row" spacing={0.5} alignItems="center">
        <Button
          size="small"
          startIcon={<AddIcon />}
          sx={{ flex: 1 }}
          onClick={() => setProps([...props, { propName: '', strVal: '' }])}
        >
          {t('maps.search.addProp')}
        </Button>
        <Button size="small" variant="contained" disabled={usableProps.length === 0 || loading} onClick={findByProps}>
          {t('maps.search.find')}
        </Button>
      </Stack>

      <Divider />

      {loading ? <LinearProgress /> : null}
      {error ? <Typography variant="caption" color="error">{error}</Typography> : null}
      {mode ? (
        rows.length > 0 ? (
          <>
            <Stack direction="row" alignItems="center">
              <Typography variant="caption" color="text.secondary" sx={{ flex: 1 }}>
                {t('maps.search.results', { count: rows.length })}
              </Typography>
              <Button size="small" onClick={() => void dispatch(clearSearch())}>{t('maps.search.clear')}</Button>
            </Stack>
            <List dense disablePadding>
              {sortMarkers(rows).map((marker) => (
                <ListItemButton key={marker.id} onClick={() => onSelect(marker)} sx={{ py: 0.25 }}>
                  <Box sx={{ width: 24, flexShrink: 0, display: 'flex', justifyContent: 'center' }}>
                    <KindIcon kind={marker.nodeKind} />
                  </Box>
                  <Box sx={{ minWidth: 0, ml: 0.5 }}>
                    <Typography variant="body2" noWrap>{marker.name || marker.id}</Typography>
                    <Typography variant="caption" color="text.secondary" noWrap sx={{ display: 'block' }}>
                      {t(`maps.node.kind.${marker.nodeKind ?? 'object'}`)}
                    </Typography>
                  </Box>
                </ListItemButton>
              ))}
            </List>
          </>
        ) : (
          <Typography variant="body2" color="text.disabled">{t('maps.search.noResults')}</Typography>
        )
      ) : null}

      <Divider />

      <Typography variant="overline" color="text.secondary">{t('maps.search.tracks')}</Typography>
      <TextField
        size="small"
        type="datetime-local"
        label={t('maps.search.periodFrom')}
        value={periodFrom}
        onChange={(event) => setPeriodFrom(event.target.value)}
        InputLabelProps={{ shrink: true }}
      />
      <TextField
        size="small"
        type="datetime-local"
        label={t('maps.search.periodTo')}
        value={periodTo}
        onChange={(event) => setPeriodTo(event.target.value)}
        InputLabelProps={{ shrink: true }}
      />
      <Stack direction="row" spacing={1}>
        <Button size="small" variant="outlined" disabled={tracksLoading || !space.id} onClick={showTracks}>
          {t('maps.search.showTracks')}
        </Button>
        <Button size="small" disabled={here.length === 0} onClick={() => void dispatch(clearTracks())}>
          {t('maps.search.hideTracks')}
        </Button>
      </Stack>
      {tracksError ? <Typography variant="caption" color="error">{tracksError}</Typography> : null}
      {tracksSpaceId === space.id && !tracksLoading ? (
        <Typography variant="caption" color="text.secondary">
          {here.length > 0
            ? t('maps.search.trackCount', { count: here.length })
            : t('maps.search.noTracks')}
        </Typography>
      ) : null}
    </Box>
  )
}

function updateRow(rows: PropRow[], index: number, patch: Partial<PropRow>): PropRow[] {
  return rows.map((row, i) => (i === index ? { ...row, ...patch } : row))
}
