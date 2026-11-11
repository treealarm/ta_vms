import { useRef, useState } from 'react'
import {
  Box, Button, Divider, IconButton, MenuItem, Stack, TextField, Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import type {
  MapExtraProperty, MapFigure, MapLevel, MapMarker, MapObjProps,
} from '../../dto/mapDtos'
import { useAppDispatch } from '../../store'
import { saveNodeDetails } from '../../store/mapEditsSlice'
import DetailToolbar from '../settings/DetailToolbar'
import NodeIdentity from '../objects/NodeIdentity'
import PropertyValueField from './PropertyValueField'
import { visualImageUrl } from '../../map/visualImage'
import {
  PROPERTY_TYPES, PROPERTY_TYPE_LABELS, normalizeType, normalizeValue,
} from './propertyTypes'

type Props = {
  node: MapMarker
  /** The node's shape, when it has one: it carries the zoom layer. */
  figure: MapFigure | null
  /** What the map says about the node besides its shape: properties and look. */
  loaded: MapObjProps | null
  levels: MapLevel[]
  /** Whether the canvas this stands on is a map: a plan has one scale and no layers. */
  showLayer?: boolean
  onDelete: () => void
  onClose: () => void
}

type Draft = {
  name: string
  color: string
  text: string
  rotation: string
  zoomLevel: string
  image: { data: string; width: number; height: number } | null
  properties: MapExtraProperty[]
}

const draftOf = (node: MapMarker, figure: MapFigure | null, loaded: MapObjProps | null): Draft => {
  const visual = loaded?.visual ?? figure?.visual
  return {
    name: node.name ?? '',
    color: visual?.color ?? '',
    text: visual?.text ?? '',
    rotation: visual?.rotationDeg === undefined ? '' : String(visual.rotationDeg),
    zoomLevel: figure?.zoomLevel ?? '',
    image: visual?.image
      ? { data: visual.image, width: visual.imageWidth ?? 0, height: visual.imageHeight ?? 0 }
      : null,
    // A type the editor no longer offers is shown as plain text, the shape the value has anyway.
    properties: (loaded?.extraProps ?? figure?.extraProps ?? [])
      .map((property) => ({ ...property, visualType: normalizeType(property.visualType) })),
  }
}

const same = (a: Draft, b: Draft) => JSON.stringify(a) === JSON.stringify(b)

/**
 * The administrator's editor for one map node: its name, its abstract properties and how it is
 * drawn — colour, icon, rotation, caption and the scale layer it belongs to. Nothing saves until
 * Apply: a node has several fields that belong together, unlike a drag on the canvas.
 */
export default function ObjectPropsEditor({ node, figure, loaded, levels, showLayer, onDelete, onClose }: Props) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const navigate = useNavigate()
  const initial = draftOf(node, figure, loaded)
  const [draft, setDraft] = useState<Draft>(initial)
  const fileRef = useRef<HTMLInputElement>(null)

  const set = <K extends keyof Draft>(key: K, value: Draft[K]) =>
    setDraft((previous) => ({ ...previous, [key]: value }))

  const setProperty = (index: number, patch: Partial<MapExtraProperty>) =>
    setDraft((previous) => ({
      ...previous,
      properties: previous.properties.map((property, i) => (i === index ? { ...property, ...patch } : property)),
    }))

  /** What goes to the server; `ownerId` must travel back or a replica loses its owner. */
  const payload = () => {
    if (draft.name.trim() === '') return { nodeRow: null, updatedFigure: null }

    const nodeRow = {
      id: node.id,
      parentId: node.parentId,
      ownerId: node.ownerId,
      name: draft.name.trim(),
    }

    return {
      nodeRow,
      updatedFigure: figure
        ? { ...figure, ...nodeRow, zoomLevel: draft.zoomLevel || undefined, extraProps: [] }
        : undefined,
    }
  }

  const apply = () => {
    const { nodeRow, updatedFigure } = payload()
    if (!nodeRow) return

    void dispatch(saveNodeDetails({
      node: nodeRow,
      properties: draft.properties
        .filter((property) => !!property.propName)
        .map((property) => ({
          ...property,
          strVal: normalizeValue(property.visualType, property.strVal ?? ''),
        })),
      visual: {
        objectId: node.id,
        color: draft.color,
        text: draft.text,
        rotationDeg: draft.rotation === '' ? undefined : Number(draft.rotation),
        image: draft.image?.data,
        imageWidth: draft.image?.width,
        imageHeight: draft.image?.height,
      },
      figure: updatedFigure,
    }))
  }

  const pickImage = async (file: File) => {
    const bitmap = await createImageBitmap(file)
    const { width, height } = bitmap
    bitmap.close()

    const reader = new FileReader()
    reader.onload = () => {
      const url = String(reader.result ?? '')
      const comma = url.indexOf(',')
      if (comma > 0) set('image', { data: url.slice(comma + 1), width, height })
    }
    reader.readAsDataURL(file)
  }

  const dirty = !same(draft, initial)

  return (
    <Stack sx={{ minHeight: 0, flex: 1 }}>
      <DetailToolbar
        title={draft.name || node.id}
        subtitle={t(`maps.node.kind.${node.nodeKind ?? 'object'}`)}
        dirty={dirty}
        onApply={apply}
        onCancel={() => setDraft(initial)}
        onDelete={() => {
          if (window.confirm(t('maps.editor.confirmDelete', { name: draft.name || node.id }))) onDelete()
        }}
      />

      <Box sx={{ p: 1.5, display: 'flex', flexDirection: 'column', gap: 1.5, overflowY: 'auto' }}>
        {/* The name is what the administrator came to change; the id and the node this one stands
            for are what every other surface names it by. */}
        <NodeIdentity
          node={node}
          name={draft.name}
          onNameChange={(name) => set('name', name)}
          device={loaded?.device}
          onSelect={(id) => navigate(`/admin/nodes?node=${encodeURIComponent(id)}`)}
        />

        <Divider />

        <Typography variant="overline" color="text.secondary">{t('maps.editor.visual')}</Typography>
        <Stack direction="row" spacing={1} alignItems="center">
          <Box
            component="input"
            type="color"
            value={/^#[0-9a-f]{6}$/i.test(draft.color) ? draft.color : '#90a4ae'}
            onChange={(event: React.ChangeEvent<HTMLInputElement>) => set('color', event.target.value)}
            sx={{ width: 40, height: 32, p: 0, border: 0, bgcolor: 'transparent', cursor: 'pointer' }}
          />
          <TextField
            size="small"
            label={t('maps.editor.color')}
            value={draft.color}
            onChange={(event) => set('color', event.target.value)}
            sx={{ flex: 1 }}
          />
        </Stack>
        <Stack direction="row" spacing={1}>
          <TextField
            size="small"
            type="number"
            label={t('maps.editor.rotation')}
            value={draft.rotation}
            onChange={(event) => set('rotation', event.target.value)}
            sx={{ width: 130 }}
          />
          <TextField
            size="small"
            label={t('maps.editor.caption')}
            value={draft.text}
            onChange={(event) => set('text', event.target.value)}
            sx={{ flex: 1 }}
          />
        </Stack>

        <Stack direction="row" spacing={1} alignItems="center">
          <Button size="small" variant="outlined" onClick={() => fileRef.current?.click()}>
            {t('maps.editor.icon')}
          </Button>
          {draft.image ? (
            <>
              <Box
                component="img"
                alt=""
                src={visualImageUrl(draft.image.data)}
                sx={{ width: 28, height: 28, objectFit: 'contain', bgcolor: '#000', borderRadius: 0.5 }}
              />
              <Button size="small" onClick={() => set('image', null)}>{t('maps.editor.clearIcon')}</Button>
            </>
          ) : null}
          <input
            ref={fileRef}
            type="file"
            accept="image/*"
            hidden
            onChange={(event) => {
              const file = event.target.files?.[0]
              event.target.value = ''
              if (file) void pickImage(file)
            }}
          />
        </Stack>

        {figure && showLayer ? (
          <TextField
            size="small"
            select
            label={t('maps.editor.zoomLevel')}
            value={draft.zoomLevel}
            onChange={(event) => set('zoomLevel', event.target.value)}
          >
            <MenuItem value="">{t('maps.editor.alwaysVisible')}</MenuItem>
            {levels.filter((level) => !!level.zoomLevel).map((level) => (
              <MenuItem key={level.id ?? level.zoomLevel} value={level.zoomLevel}>{level.zoomLevel}</MenuItem>
            ))}
          </TextField>
        ) : null}

        <Divider />

        <Stack direction="row" alignItems="center">
          <Typography variant="overline" color="text.secondary" sx={{ flex: 1 }}>
            {t('maps.editor.properties')}
          </Typography>
          <Button
            size="small"
            startIcon={<AddIcon />}
            onClick={() => setDraft((previous) => ({
              ...previous,
              properties: [...previous.properties, { propName: '', strVal: '', visualType: '' }],
            }))}
          >
            {t('maps.editor.addProperty')}
          </Button>
        </Stack>
        {draft.properties.map((property, index) => (
          <Stack key={index} spacing={0.5} sx={{ border: 1, borderColor: 'divider', borderRadius: 1, p: 1 }}>
            <Stack direction="row" spacing={0.5} alignItems="center">
              <TextField
                size="small"
                label={t('maps.editor.propName')}
                value={property.propName ?? ''}
                onChange={(event) => setProperty(index, { propName: event.target.value })}
                sx={{ flex: 1 }}
              />
              <IconButton
                size="small"
                onClick={() => setDraft((previous) => ({
                  ...previous,
                  properties: previous.properties.filter((_, i) => i !== index),
                }))}
              >
                <DeleteOutlineIcon fontSize="small" />
              </IconButton>
            </Stack>
            <PropertyValueField
              type={property.visualType}
              value={property.strVal ?? ''}
              label={t('maps.editor.propValue')}
              onChange={(strVal) => setProperty(index, { strVal })}
            />
            <TextField
              size="small"
              select
              label={t('maps.editor.propType')}
              value={property.visualType ?? ''}
              onChange={(event) => setProperty(index, { visualType: event.target.value })}
            >
              {PROPERTY_TYPES.map((type) => (
                <MenuItem key={type || 'plain'} value={type}>
                  {t(PROPERTY_TYPE_LABELS[type])}
                </MenuItem>
              ))}
            </TextField>
          </Stack>
        ))}

        <Button size="small" onClick={onClose} sx={{ alignSelf: 'flex-end' }}>{t('common.close')}</Button>
      </Box>
    </Stack>
  )
}
