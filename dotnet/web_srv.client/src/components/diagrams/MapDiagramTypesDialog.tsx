import { useEffect, useRef, useState } from 'react'
import {
  Accordion, AccordionDetails, AccordionSummary, Alert, Box, Button, Dialog, DialogActions,
  DialogContent, DialogTitle, Stack, TextField, Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import { useTranslation } from 'react-i18next'
import { mapApi } from '../../api/mapApi'
import { errorText } from '../../api/requestJson'
import type { MapDiagramType } from '../../dto/mapDtos'
import { useMapFileImage } from '../../hooks/useMapFileImage'
import { useAppDispatch } from '../../store'
import { loadDiagramTypes } from '../../store/diagramsSlice'
import DiagramTypeRegionEditor from './DiagramTypeRegionEditor'

type Props = {
  open: boolean
  onClose: () => void
  /** A type was written or removed: the open diagram is drawn from them again. */
  onSaved?: () => void
}

const round = (value: number) => Math.round(value * 1000) / 1000

/**
 * What the server would refuse anyway, said before the round trip: every region needs a key of its
 * own, and a region outside the paper would have no picture under it.
 */
function problemsOf(type: MapDiagramType, t: (key: string, options?: Record<string, unknown>) => string): string[] {
  const problems: string[] = []
  const keys = new Set<string>()
  const claimed = new Set<string>()

  for (const region of type.regions ?? []) {
    const key = region.regionKey?.trim() ?? ''
    if (key === '') {
      problems.push(t('diagramEditor.problemNoKey', { name: type.name }))
      continue
    }
    if (keys.has(key)) problems.push(t('diagramEditor.problemDuplicateKey', { name: type.name, key }))
    keys.add(key)

    // A region that names a part is a binding, and the server refuses the same things: an empty
    // way of finding it, a position that is not one, and two regions on one part.
    if (region.part) {
      const value = (region.part.matchValue ?? '').trim()
      if (value === '') {
        problems.push(t('diagramEditor.problemBindingNoValue', { name: type.name, key }))
      } else if (region.part.matchBy === 'index'
        && !(Number.isInteger(Number(value)) && Number(value) >= 1)) {
        problems.push(t('diagramEditor.problemBindingIndex', { name: type.name, key }))
      } else {
        const claimedKey = `${region.part.partKind ?? ''}|${region.part.matchBy ?? ''}|${value}`
        if (claimed.has(claimedKey)) {
          problems.push(t('diagramEditor.problemBindingDuplicate', { name: type.name }))
        }
        claimed.add(claimedKey)
      }
    }

    const geometry = region.geometry
    if (!geometry) {
      problems.push(t('diagramEditor.problemNoGeometry', { name: type.name, key }))
      continue
    }

    const left = geometry.left ?? 0
    const top = geometry.top ?? 0
    const width = geometry.width ?? 0
    const height = geometry.height ?? 0
    const outside = left < 0 || top < 0 || width <= 0 || height <= 0
      || round(left + width) > 1 || round(top + height) > 1
    if (outside) problems.push(t('diagramEditor.problemOutside', { name: type.name, key }))
  }

  return problems
}

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

  const save = async () => {
    const problems = types.flatMap((type) => problemsOf(type, t))
    if (problems.length > 0) {
      setError(problems.join(' '))
      return
    }

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
                <DiagramTypeRegionEditor type={type} onChange={(patch) => change(index, patch)} />
                <Button size="small" color="error" onClick={() => void remove(type)}>
                  {t('diagramEditor.deleteType')}
                </Button>
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

  const isTemplate = Boolean(type.sourceKind)

  return (
    <Stack spacing={1}>
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

      {/* A type is an ordinary mnemoscheme until it names a device type; the model fields only
          mean something then. */}
      <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap" useFlexGap>
        <TextField
          size="small"
          select
          SelectProps={{ native: true }}
          label={t('diagramEditor.sourceKind')}
          value={type.sourceKind ?? ''}
          onChange={(event) => onChange({
            sourceKind: event.target.value || undefined,
            ...(event.target.value ? {} : { autoAttach: false }),
          })}
          sx={{ width: 200 }}
        >
          <option value="">{t('diagramEditor.sourceKindNone')}</option>
          <option value="camera">{t('diagramEditor.sourceKindCamera')}</option>
        </TextField>

        <TextField
          size="small"
          label={t('diagramEditor.deviceManufacturer')}
          disabled={!isTemplate}
          value={type.deviceManufacturer ?? ''}
          onChange={(event) => onChange({ deviceManufacturer: event.target.value })}
          sx={{ width: 200 }}
        />
        <TextField
          size="small"
          label={t('diagramEditor.deviceModel')}
          disabled={!isTemplate}
          value={type.deviceModel ?? ''}
          onChange={(event) => onChange({ deviceModel: event.target.value })}
          sx={{ width: 240 }}
        />
        <label>
          <input
            type="checkbox"
            disabled={!isTemplate}
            checked={Boolean(type.autoAttach)}
            onChange={(event) => onChange({ autoAttach: event.target.checked })}
          />
          {' '}{t('diagramEditor.autoAttach')}
        </label>
      </Stack>
      {isTemplate ? (
        <Typography variant="caption" color="text.secondary">
          {t('diagramEditor.templateHint')}
        </Typography>
      ) : null}
    </Stack>
  )
}
