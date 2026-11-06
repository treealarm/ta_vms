import { useEffect, useState } from 'react'
import {
  Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, MenuItem, Stack,
  TextField, Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import { mapApi } from '../../api/mapApi'
import { errorText } from '../../api/requestJson'
import type { MapDiagramType, MapMarker } from '../../dto/mapDtos'

type Props = {
  /** The device node the diagram is drawn under; null closes the dialog. */
  device: MapMarker | null
  onClose: () => void
  /** The diagram was created: the tree under the device is out of date. */
  onAttached?: () => void
}

/**
 * Draws a device from a template by hand — the path for a model no template attaches itself to.
 * Only templates are offered: an ordinary mnemoscheme has no parts to stand for, and attaching it
 * to a device would promise a sync that would never happen.
 */
export default function AttachDeviceDiagramDialog({ device, onClose, onAttached }: Props) {
  const { t } = useTranslation()
  const [templates, setTemplates] = useState<MapDiagramType[]>([])
  const [typeId, setTypeId] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!device) return

    setError(null)
    setTypeId('')
    void mapApi.getDiagramTypes({})
      .then((response) => {
        const rows = (response.diagramTypes ?? []).filter((type) => type.sourceKind)
        setTemplates(rows)
        setTypeId(rows[0]?.id ?? '')
      })
      .catch((e) => setError(errorText(e)))
  }, [device])

  const attach = async () => {
    if (!device?.cameraId || !typeId) return

    setBusy(true)
    setError(null)
    try {
      await mapApi.attachDeviceDiagram({
        sourceKind: 'camera',
        sourceId: device.cameraId,
        diagramTypeId: typeId,
      })
      onAttached?.()
      onClose()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog open={Boolean(device)} onClose={onClose} maxWidth="xs" fullWidth>
      <DialogTitle>{t('diagramEditor.attachDeviceDiagram')}</DialogTitle>
      <DialogContent>
        <Stack spacing={1.5} sx={{ mt: 0.5 }}>
          {error ? <Alert severity="error">{error}</Alert> : null}
          <Typography variant="caption" color="text.secondary">
            {t('diagramEditor.attachDeviceDiagramHint', { name: device?.name ?? '' })}
          </Typography>

          {templates.length === 0 && !error ? (
            <Typography variant="body2" color="text.secondary">
              {t('diagramEditor.noTemplates')}
            </Typography>
          ) : (
            <TextField
              select
              size="small"
              label={t('diagramEditor.type')}
              value={typeId}
              onChange={(event) => setTypeId(event.target.value)}
            >
              {templates.map((type) => (
                <MenuItem key={type.id} value={type.id}>
                  {type.name}
                </MenuItem>
              ))}
            </TextField>
          )}
        </Stack>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>{t('common.cancel')}</Button>
        <Button variant="contained" disabled={busy || !typeId} onClick={() => void attach()}>
          {t('diagramEditor.attach')}
        </Button>
      </DialogActions>
    </Dialog>
  )
}
