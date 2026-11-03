import { useEffect, useRef, useState } from 'react'
import {
  Accordion, AccordionDetails, AccordionSummary, Alert, Box, Button, Dialog, DialogActions,
  DialogContent, DialogTitle, IconButton, Stack, TextField, Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import { useTranslation } from 'react-i18next'
import { mapApi } from '../../api/mapApi'
import { errorText } from '../../api/requestJson'
import type { MapDiagramType, MapDiagramTypeRegion } from '../../dto/mapDtos'
import { useMapFileImage } from '../../hooks/useMapFileImage'
import { useAppDispatch } from '../../store'
import { loadDiagramTypes } from '../../store/diagramsSlice'

type Props = {
  open: boolean
  onClose: () => void
  /** A type was written or removed: the open diagram is drawn from them again. */
  onSaved?: () => void
}

const emptyRegion = (): MapDiagramTypeRegion => ({
  regionKey: '',
  geometry: { top: 0.1, left: 0.1, width: 0.2, height: 0.1, rotation: 0 },
  styles: { color: '#ffd54f', fontSize: '14px' },
})

/**
 * The types of the mnemoschemes: what draws a node (its background picture) and the regions of
 * shares its children may take instead of a rectangle of their own. A type is shared by every
 * diagram of it, so this edits the whole deployment's dictionary, not one diagram.
 */
export default function MapDiagramTypesDialog({ open, onClose, onSaved }: Props) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const [types, setTypes] = useState<MapDiagramType[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return

    setError(null)
    void mapApi.getDiagramTypes({})
      .then((response) => setTypes(response.diagramTypes ?? []))
      .catch((e) => setError(errorText(e)))
  }, [open])

  const change = (index: number, patch: Partial<MapDiagramType>) =>
    setTypes((rows) => rows.map((row, i) => (i === index ? { ...row, ...patch } : row)))

  const changeRegion = (index: number, regionIndex: number, patch: Partial<MapDiagramTypeRegion>) =>
    setTypes((rows) => rows.map((row, i) => (i === index ? {
      ...row,
      regions: (row.regions ?? []).map((region, j) => (j === regionIndex ? { ...region, ...patch } : region)),
    } : row)))

  const changeRegionGeometry = (
    index: number, regionIndex: number, field: 'top' | 'left' | 'width' | 'height', value: string,
  ) => {
    const region = types[index]?.regions?.[regionIndex]
    if (!region) return
    changeRegion(index, regionIndex, {
      geometry: { ...region.geometry, [field]: Number(value) || 0 },
    })
  }

  const save = async () => {
    setBusy(true)
    setError(null)
    try {
      const saved = await mapApi.updateDiagramTypes(types)
      setTypes(saved.diagramTypes ?? types)
      void dispatch(loadDiagramTypes())
      onSaved?.()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  const remove = async (type: MapDiagramType) => {
    if (!type.id) {
      setTypes((rows) => rows.filter((row) => row !== type))
      return
    }
    if (!window.confirm(t('diagramEditor.confirmDeleteType', { name: type.name }))) return

    setBusy(true)
    setError(null)
    try {
      await mapApi.deleteDiagramTypes([type.id])
      setTypes((rows) => rows.filter((row) => row.id !== type.id))
      void dispatch(loadDiagramTypes())
      onSaved?.()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog open={open} onClose={onClose} maxWidth="md" fullWidth>
      <DialogTitle>{t('diagramEditor.types')}</DialogTitle>
      <DialogContent sx={{ minHeight: 420 }}>
        {error ? <Alert severity="error" sx={{ mb: 1 }}>{error}</Alert> : null}
        <Typography variant="caption" color="text.secondary" display="block" sx={{ mb: 1 }}>
          {t('diagramEditor.typesHint')}
        </Typography>

        {types.map((type, index) => (
          <Accordion key={type.id ?? `new:${index}`} disableGutters>
            <AccordionSummary expandIcon={<ExpandMoreIcon />}>
              <Stack direction="row" spacing={1} alignItems="center" sx={{ flex: 1 }}>
                <Typography sx={{ flex: 1 }}>{type.name || t('diagramEditor.newType')}</Typography>
                <Typography variant="caption" color="text.secondary">
                  {t('diagramEditor.nRegions', { count: type.regions?.length ?? 0 })}
                </Typography>
              </Stack>
            </AccordionSummary>
            <AccordionDetails>
              <Stack spacing={1.5}>
                <TypeRow type={type} onChange={(patch) => change(index, patch)} />
                <Typography variant="overline" color="text.secondary">{t('diagramEditor.regions')}</Typography>
                {(type.regions ?? []).map((region, regionIndex) => (
                  <Stack key={regionIndex} direction="row" spacing={0.5} alignItems="center" flexWrap="wrap" useFlexGap>
                    <TextField
                      size="small"
                      label={t('diagramEditor.regionKey')}
                      value={region.regionKey ?? ''}
                      onChange={(event) => changeRegion(index, regionIndex, { regionKey: event.target.value })}
                      sx={{ width: 150 }}
                    />
                    {(['left', 'top', 'width', 'height'] as const).map((field) => (
                      <TextField
                        key={field}
                        size="small"
                        type="number"
                        label={t(`diagramEditor.${field}`)}
                        value={region.geometry?.[field] ?? 0}
                        onChange={(event) => changeRegionGeometry(index, regionIndex, field, event.target.value)}
                        inputProps={{ step: 0.01, min: 0, max: 1 }}
                        sx={{ width: 92 }}
                      />
                    ))}
                    <IconButton
                      size="small"
                      onClick={() => change(index, {
                        regions: (type.regions ?? []).filter((_, j) => j !== regionIndex),
                      })}
                    >
                      <DeleteOutlineIcon fontSize="small" />
                    </IconButton>
                  </Stack>
                ))}
                <Stack direction="row" spacing={1}>
                  <Button
                    size="small"
                    startIcon={<AddIcon />}
                    onClick={() => change(index, { regions: [...(type.regions ?? []), emptyRegion()] })}
                  >
                    {t('diagramEditor.addRegion')}
                  </Button>
                  <Button size="small" color="error" onClick={() => void remove(type)}>
                    {t('diagramEditor.deleteType')}
                  </Button>
                </Stack>
              </Stack>
            </AccordionDetails>
          </Accordion>
        ))}

        <Button
          size="small"
          startIcon={<AddIcon />}
          sx={{ mt: 1 }}
          onClick={() => setTypes((rows) => [...rows, { name: '', regions: [] }])}
        >
          {t('diagramEditor.addType')}
        </Button>
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>{t('common.close')}</Button>
        <Button variant="contained" disabled={busy} onClick={() => void save()}>{t('common.save')}</Button>
      </DialogActions>
    </Dialog>
  )
}

/** One type's name and background picture; the picture is a row of `files`, uploaded here. */
function TypeRow({ type, onChange }: { type: MapDiagramType; onChange: (patch: Partial<MapDiagramType>) => void }) {
  const { t } = useTranslation()
  const fileRef = useRef<HTMLInputElement>(null)
  const preview = useMapFileImage(type.fileId)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const upload = async (file: File) => {
    setBusy(true)
    setError(null)
    try {
      const info = await mapApi.uploadFile(file)
      if (info.id) onChange({ fileId: info.id })
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Stack direction="row" spacing={1} alignItems="center">
      <TextField
        size="small"
        label={t('maps.editor.name')}
        value={type.name ?? ''}
        onChange={(event) => onChange({ name: event.target.value })}
        sx={{ flex: 1 }}
      />
      <Button size="small" variant="outlined" disabled={busy} onClick={() => fileRef.current?.click()}>
        {t('diagramEditor.background')}
      </Button>
      {preview ? (
        <Box component="img" src={preview} alt="" sx={{ height: 36, maxWidth: 90, objectFit: 'contain', bgcolor: '#000' }} />
      ) : null}
      {error ? <Typography variant="caption" color="error">{error}</Typography> : null}
      <input
        ref={fileRef}
        type="file"
        accept="image/*"
        hidden
        onChange={(event) => {
          const file = event.target.files?.[0]
          event.target.value = ''
          if (file) void upload(file)
        }}
      />
    </Stack>
  )
}
