import {
  Box, Chip, Stack, Table, TableBody, TableCell, TableHead, TableRow, Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import type { MapMarker } from '../../dto/mapDtos'
import { useAppSelector } from '../../store'
import { ALARM_COLOR } from '../../map/nodeStyle'

type Props = {
  /** The nodes checked in the tree, in the order they were checked. */
  ids: Set<string>
  selectedId: string | null
  onSelect: (marker: MapMarker) => void
}

/**
 * The states of the nodes ticked in the tree, live: the tree's checkboxes pick the rows, the same
 * state and alarm store the canvas paints from fills them. A separate view rather than another
 * route, so the checkboxes stay where the operator left them.
 */
export default function StatesTable({ ids, selectedId, onSelect }: Props) {
  const { t } = useTranslation()
  const markers = useAppSelector((s) => s.objects.markers)
  const states = useAppSelector((s) => s.states.states)
  const alarms = useAppSelector((s) => s.states.alarms)
  const descriptions = useAppSelector((s) => s.states.descriptions)

  const rows = [...ids].map((id) => markers[id]).filter((marker): marker is MapMarker => !!marker)

  if (rows.length === 0) {
    return <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>{t('maps.states.none')}</Typography>
  }

  return (
    <Table size="small" stickyHeader>
      <TableHead>
        <TableRow>
          <TableCell>{t('maps.states.name')}</TableCell>
          <TableCell>{t('maps.states.states')}</TableCell>
        </TableRow>
      </TableHead>
      <TableBody>
        {rows.map((marker) => {
          const alarm = alarms[marker.id]
          const alarming = !!(alarm?.alarm || (alarm?.childrenAlarms ?? 0) > 0)
          const names = states[marker.id]?.states ?? []

          return (
            <TableRow
              key={marker.id}
              hover
              selected={marker.id === selectedId}
              onClick={() => onSelect(marker)}
              sx={{ cursor: 'pointer', bgcolor: alarming ? `${ALARM_COLOR}22` : undefined }}
            >
              <TableCell sx={{ py: 0.5 }}>
                <Typography variant="body2" noWrap>{marker.name || marker.id}</Typography>
                <Typography variant="caption" color="text.secondary">
                  {t(`maps.node.kind.${marker.nodeKind ?? 'object'}`)}
                </Typography>
              </TableCell>
              <TableCell sx={{ py: 0.5 }}>
                <Stack direction="row" spacing={0.5} flexWrap="wrap" useFlexGap>
                  {alarm?.alarm ? (
                    <Chip size="small" label={t('maps.node.alarm')} sx={{ bgcolor: ALARM_COLOR, color: '#fff' }} />
                  ) : null}
                  {(alarm?.childrenAlarms ?? 0) > 0 ? (
                    <Chip size="small" variant="outlined" color="error"
                      label={t('maps.node.childAlarms', { count: alarm?.childrenAlarms })} />
                  ) : null}
                  {names.map((name) => {
                    const description = descriptions[name]
                    return (
                      <Chip
                        key={name}
                        size="small"
                        variant="outlined"
                        label={description?.stateDescr || name}
                        sx={{ borderColor: description?.stateColor }}
                      />
                    )
                  })}
                  {!alarm?.alarm && !(alarm?.childrenAlarms ?? 0) && names.length === 0 ? (
                    <Box component="span"><Typography variant="caption" color="text.secondary">—</Typography></Box>
                  ) : null}
                </Stack>
              </TableCell>
            </TableRow>
          )
        })}
      </TableBody>
    </Table>
  )
}
