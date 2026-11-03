import { useEffect, useRef, useState } from 'react'
import {
  Box, Button, MenuItem, Stack, TextField, Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import { mapApi } from '../../api/mapApi'
import { errorText } from '../../api/requestJson'
import type {
  MapDiagram, MapDiagramType, MapMarker,
} from '../../dto/mapDtos'
import { useAppDispatch, useAppSelector } from '../../store'
import { loadDiagramTypes } from '../../store/diagramsSlice'
import { deleteNode, saveDiagramPlacement } from '../../store/mapEditsSlice'
import { useMapFileImage } from '../../hooks/useMapFileImage'
import DetailToolbar from '../settings/DetailToolbar'
import MapDiagramTypesDialog from './MapDiagramTypesDialog'

type Props = {
  marker: MapMarker
  /** The node's row in the open diagram: its type, its region, its rectangle. */
  element: MapDiagram
  /** The type of the diagram it stands in — its regions are the places it may take. */
  parentType: MapDiagramType | undefined
  onSaved: () => void
  onClose: () => void
}

type Draft = {
  name: string
  typeId: string
  regionId: string
  top: string
  left: string
  width: string
  height: string
  rotation: string
  fileId: string
}

const draftOf = (marker: MapMarker, element: MapDiagram): Draft => ({
  name: marker.name ?? '',
  typeId: element.dgrType ?? '',
  regionId: element.regionId ?? '',
  top: String(element.geometry?.top ?? 0),
  left: String(element.geometry?.left ?? 0),
  width: String(element.geometry?.width ?? 80),
  height: String(element.geometry?.height ?? 80),
  rotation: String(element.geometry?.rotation ?? 0),
  fileId: element.fileId ?? '',
})

/**
 * Where a node stands inside a mnemoscheme: in a region of the parent's type or at its own
 * rectangle, how big it is and how it is turned, which type draws it and which picture it
 * carries of its own. The panel is about the element, not about the node — a replica's name and
 * colour are the map's own business, edited beside the map.
 */
export default function DiagramElementEditor({
  marker, element, parentType, onSaved, onClose,
}: Props) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const types = useAppSelector((s) => s.diagrams.types)
  const initial = draftOf(marker, element)
  const [draft, setDraft] = useState<Draft>(initial)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [typesOpen, setTypesOpen] = useState(false)
  const fileRef = useRef<HTMLInputElement>(null)
  const preview = useMapFileImage(draft.fileId)

  useEffect(() => {
    if (types.length === 0) void dispatch(loadDiagramTypes())
  }, [types.length, dispatch])

  const set = <K extends keyof Draft>(key: K, value: Draft[K]) =>
    setDraft((previous) => ({ ...previous, [key]: value }))

  const dirty = JSON.stringify(draft) !== JSON.stringify(initial)
  const inRegion = draft.regionId !== ''

  const upload = async (file: File) => {
    setBusy(true)
    setError(null)
    try {
      const info = await mapApi.uploadFile(file)
      if (info.id) set('fileId', info.id)
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  const apply = async () => {
    setBusy(true)
    setError(null)
    try {
      await dispatch(saveDiagramPlacement({
        id: marker.id,
        // The node's own parent: the open diagram for a child, the space for the diagram itself.
        parentId: marker.parentId ?? '',
        ownerId: marker.ownerId,
        name: draft.name.trim() || marker.name,
        dgrType: draft.typeId || undefined,
        regionId: inRegion ? draft.regionId : undefined,
        geometry: inRegion ? undefined : {
          top: Number(draft.top) || 0,
          left: Number(draft.left) || 0,
          width: Number(draft.width) || 1,
          height: Number(draft.height) || 1,
          rotation: Number(draft.rotation) || 0,
        },
        fileId: draft.fileId || undefined,
      })).unwrap()
      onSaved()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Stack sx={{ flex: 1, minHeight: 0 }}>
      <DetailToolbar
        title={draft.name || marker.id}
        subtitle={t('maps.node.kind.diagram')}
        dirty={dirty}
        busy={busy}
        onApply={apply}
        onCancel={() => setDraft(initial)}
        onDelete={() => {
          if (!window.confirm(t('maps.editor.confirmDelete', { name: draft.name || marker.id }))) return
          void dispatch(deleteNode(marker.id)).unwrap()
            .then(() => onClose())
            .catch((e) => setError(errorText(e)))
        }}
      />

      <Box sx={{ p: 1.5, display: 'flex', flexDirection: 'column', gap: 1.5, overflowY: 'auto' }}>
        {error ? <Typography color="error" variant="body2">{error}</Typography> : null}

        <TextField
          size="small"
          label={t('maps.editor.name')}
          value={draft.name}
          onChange={(event) => set('name', event.target.value)}
        />

        {/* The type decides what draws the diagram and which places its children may take; the
            dictionary it comes from is edited right here, so an empty one is not a dead end. */}
        <Stack direction="row" spacing={1} alignItems="flex-start">
          <TextField
            size="small"
            select
            label={t('diagramEditor.type')}
            value={draft.typeId}
            onChange={(event) => set('typeId', event.target.value)}
            sx={{ flex: 1 }}
          >
            <MenuItem value="">{t('diagramEditor.noType')}</MenuItem>
            {types.filter((type) => !!type.id).map((type) => (
              <MenuItem key={type.id} value={type.id}>{type.name || type.id}</MenuItem>
            ))}
          </TextField>
          <Button size="small" sx={{ mt: 0.5, whiteSpace: 'nowrap' }} onClick={() => setTypesOpen(true)}>
            {t('diagramEditor.manageTypes')}
          </Button>
        </Stack>
        {types.length === 0 ? (
          <Typography variant="caption" color="text.secondary">{t('diagramEditor.noTypesHint')}</Typography>
        ) : null}

        <TextField
          size="small"
          select
          label={t('diagramEditor.region')}
          value={draft.regionId}
          onChange={(event) => set('regionId', event.target.value)}
          helperText={parentType ? t('diagramEditor.regionHint') : t('diagramEditor.noParentType')}
        >
          <MenuItem value="">{t('diagramEditor.free')}</MenuItem>
          {(parentType?.regions ?? []).map((region) => (
            <MenuItem key={region.regionKey} value={region.regionKey ?? ''}>{region.regionKey}</MenuItem>
          ))}
        </TextField>

        {inRegion ? null : (
          <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap>
            {(['left', 'top', 'width', 'height', 'rotation'] as const).map((field) => (
              <TextField
                key={field}
                size="small"
                type="number"
                label={t(`diagramEditor.${field}`)}
                value={draft[field]}
                onChange={(event) => set(field, event.target.value)}
                sx={{ width: 96 }}
              />
            ))}
          </Stack>
        )}

        <Stack direction="row" spacing={1} alignItems="center">
          <Button size="small" variant="outlined" disabled={busy} onClick={() => fileRef.current?.click()}>
            {t('diagramEditor.background')}
          </Button>
          {preview ? (
            <Box component="img" src={preview} alt="" sx={{ height: 40, maxWidth: 90, objectFit: 'contain', bgcolor: '#000' }} />
          ) : null}
          {draft.fileId ? <Button size="small" onClick={() => set('fileId', '')}>{t('maps.editor.clearIcon')}</Button> : null}
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

        <Button size="small" onClick={onClose} sx={{ alignSelf: 'flex-end' }}>{t('common.close')}</Button>
      </Box>

      <MapDiagramTypesDialog open={typesOpen} onClose={() => setTypesOpen(false)} />
    </Stack>
  )
}
