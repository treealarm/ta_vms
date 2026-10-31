import { useEffect, useState } from 'react'
import {
  Alert, Box, Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle, IconButton,
  Stack, Tab, Table, TableBody, TableCell, TableHead, TableRow, Tabs, TextField, Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import { useTranslation } from 'react-i18next'
import { mapApi } from '../../api/mapApi'
import { errorText } from '../../api/requestJson'
import type { MapLevel, MapStateDescription } from '../../dto/mapDtos'

type TabName = 'states' | 'levels'

/**
 * The map's two dictionaries, edited from the page footer: what a state is called and looks like,
 * and the scale layers. Both act on the whole deployment rather than on one node, which is why
 * they live here and not beside a selection.
 */
export default function MapDictionariesDialog({ open, onClose }: { open: boolean; onClose: () => void }) {
  const { t } = useTranslation()
  const [tab, setTab] = useState<TabName>('states')
  const [descriptions, setDescriptions] = useState<MapStateDescription[]>([])
  const [levels, setLevels] = useState<MapLevel[]>([])
  const [removedLevels, setRemovedLevels] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return

    setError(null)
    setRemovedLevels([])
    void mapApi.getStateDescriptions([])
      .then((response) => setDescriptions(response.descriptions ?? []))
      .catch((e) => setError(errorText(e)))
    void mapApi.getLevels()
      .then((response) => setLevels(response.levels ?? []))
      .catch((e) => setError(errorText(e)))
  }, [open])

  const changeState = (index: number, patch: Partial<MapStateDescription>) =>
    setDescriptions((rows) => rows.map((row, i) => (i === index ? { ...row, ...patch } : row)))

  const changeLevel = (index: number, patch: Partial<MapLevel>) =>
    setLevels((rows) => rows.map((row, i) => (i === index ? { ...row, ...patch } : row)))

  const save = async () => {
    setBusy(true)
    setError(null)
    try {
      if (tab === 'states') {
        await mapApi.updateStateDescriptions(descriptions)
      } else {
        const saved = await mapApi.updateLevels(levels)
        if (removedLevels.length > 0) await mapApi.deleteLevels(removedLevels)
        // The rows that had no id come back with the ones the map assigned.
        setLevels(saved.levels ?? levels)
        setRemovedLevels([])
      }
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog open={open} onClose={onClose} maxWidth="md" fullWidth>
      <DialogTitle>{t('maps.dictionaries.title')}</DialogTitle>
      <DialogContent sx={{ display: 'flex', flexDirection: 'column', minHeight: 440 }}>
        <Tabs value={tab} onChange={(_, value: TabName) => setTab(value)} sx={{ mb: 1 }}>
          <Tab value="states" label={t('maps.dictionaries.states')} />
          <Tab value="levels" label={t('maps.dictionaries.levels')} />
        </Tabs>

        {error ? <Alert severity="error" sx={{ mb: 1 }}>{error}</Alert> : null}

        <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
          {tab === 'states' ? (
            <Table size="small">
              <TableHead>
                <TableRow>
                  <TableCell>{t('maps.dictionaries.state')}</TableCell>
                  <TableCell>{t('maps.dictionaries.description')}</TableCell>
                  <TableCell>{t('maps.dictionaries.colour')}</TableCell>
                  <TableCell align="center">{t('maps.dictionaries.alarm')}</TableCell>
                </TableRow>
              </TableHead>
              <TableBody>
                {descriptions.map((row, index) => (
                  <TableRow key={row.id ?? row.state}>
                    <TableCell sx={{ whiteSpace: 'nowrap' }}>{row.state}</TableCell>
                    <TableCell>
                      <TextField
                        size="small"
                        fullWidth
                        value={row.stateDescr ?? ''}
                        onChange={(event) => changeState(index, { stateDescr: event.target.value })}
                      />
                    </TableCell>
                    <TableCell>
                      <Stack direction="row" spacing={0.5} alignItems="center">
                        <Box
                          component="input"
                          type="color"
                          value={/^#[0-9a-f]{6}$/i.test(row.stateColor ?? '') ? row.stateColor! : '#90a4ae'}
                          onChange={(event: React.ChangeEvent<HTMLInputElement>) =>
                            changeState(index, { stateColor: event.target.value })}
                          sx={{ width: 32, height: 28, p: 0, border: 0, bgcolor: 'transparent' }}
                        />
                        <TextField
                          size="small"
                          value={row.stateColor ?? ''}
                          onChange={(event) => changeState(index, { stateColor: event.target.value })}
                          sx={{ width: 100 }}
                        />
                      </Stack>
                    </TableCell>
                    <TableCell align="center" padding="none">
                      <Checkbox
                        size="small"
                        checked={row.alarm ?? false}
                        onChange={(event) => changeState(index, { alarm: event.target.checked })}
                      />
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          ) : (
            <>
              <Table size="small">
                <TableHead>
                  <TableRow>
                    <TableCell>{t('maps.dictionaries.zoomLevel')}</TableCell>
                    <TableCell>{t('maps.dictionaries.zoomMin')}</TableCell>
                    <TableCell>{t('maps.dictionaries.zoomMax')}</TableCell>
                    <TableCell />
                  </TableRow>
                </TableHead>
                <TableBody>
                  {levels.map((row, index) => (
                    <TableRow key={row.id ?? `new:${index}`}>
                      <TableCell>
                        <TextField
                          size="small"
                          fullWidth
                          value={row.zoomLevel ?? ''}
                          onChange={(event) => changeLevel(index, { zoomLevel: event.target.value })}
                        />
                      </TableCell>
                      <TableCell>
                        <TextField
                          size="small"
                          type="number"
                          value={row.zoomMin ?? 0}
                          onChange={(event) => changeLevel(index, { zoomMin: Number(event.target.value) })}
                          sx={{ width: 90 }}
                        />
                      </TableCell>
                      <TableCell>
                        <TextField
                          size="small"
                          type="number"
                          value={row.zoomMax ?? 0}
                          onChange={(event) => changeLevel(index, { zoomMax: Number(event.target.value) })}
                          sx={{ width: 90 }}
                        />
                      </TableCell>
                      <TableCell padding="none">
                        <IconButton
                          size="small"
                          onClick={() => {
                            if (row.id) setRemovedLevels((ids) => [...ids, row.id!])
                            setLevels((rows) => rows.filter((_, i) => i !== index))
                          }}
                        >
                          <DeleteOutlineIcon fontSize="small" />
                        </IconButton>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
              <Button
                size="small"
                startIcon={<AddIcon />}
                sx={{ mt: 1 }}
                onClick={() => setLevels((rows) => [...rows, { zoomLevel: '', zoomMin: 0, zoomMax: 0 }])}
              >
                {t('maps.dictionaries.addLevel')}
              </Button>
            </>
          )}
        </Box>

        <Typography variant="caption" color="text.secondary" sx={{ pt: 1 }}>
          {tab === 'states' ? t('maps.dictionaries.statesHint') : t('maps.dictionaries.levelsHint')}
        </Typography>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>{t('common.close')}</Button>
        <Button variant="contained" disabled={busy} onClick={() => void save()}>{t('common.save')}</Button>
      </DialogActions>
    </Dialog>
  )
}
