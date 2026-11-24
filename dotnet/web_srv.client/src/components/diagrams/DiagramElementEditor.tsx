import { useEffect, useMemo, useRef, useState } from 'react'
import {
  Box, Button, MenuItem, Stack, TextField, Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import { mapApi } from '../../api/mapApi'
import { errorText } from '../../api/requestJson'
import type {
  MapDeviceDiagramBinding, MapDiagram, MapDiagramType, MapMarker,
} from '../../dto/mapDtos'
import { useAppDispatch, useAppSelector } from '../../store'
import { loadDiagramTypes } from '../../store/diagramsSlice'
import { loadCameras } from '../../store/liveSlice'
import { deleteNode, saveDiagramPlacement } from '../../store/mapEditsSlice'
import { useMapFileImage } from '../../hooks/useMapFileImage'
import DetailToolbar from '../settings/DetailToolbar'
import NodeIdentity from '../objects/NodeIdentity'
import NodeStateChips from '../objects/NodeStateChips'
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

const draftOf = (marker: MapMarker, element: MapDiagram): Draft => {
  // A representation's own rectangle is its placement; its geometry is the paper of the diagram it
  // shows, and editing that here would resize the diagram in every copy of it.
  const rect = element.isRepresentation ? element.placement : element.geometry
  return {
    name: marker.name ?? '',
    typeId: element.dgrType ?? '',
    regionId: element.regionId ?? '',
    top: String(rect?.top ?? 0),
    left: String(rect?.left ?? 0),
    width: String(rect?.width ?? 80),
    height: String(rect?.height ?? 80),
    rotation: String(rect?.rotation ?? 0),
    fileId: element.isRepresentation ? '' : element.fileId ?? '',
  }
}

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
  // The element is the device's own node or a replica of it: its states come from the device, and
  // are read here rather than painted into the region, which is the template's picture.
  const state = useAppSelector((s) => s.states.states[marker.id])
  const alarm = useAppSelector((s) => s.states.alarms[marker.id])
  const descriptions = useAppSelector((s) => s.states.descriptions)
  const initial = draftOf(marker, element)
  const [draft, setDraft] = useState<Draft>(initial)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [typesOpen, setTypesOpen] = useState(false)
  const fileRef = useRef<HTMLInputElement>(null)
  const preview = useMapFileImage(draft.fileId)
  // A representation's content — its type and picture — belongs to the diagram it shows. Its
  // place, including the region, is still its own; that is what a copy is.
  const copy = element.isRepresentation === true

  // The device a diagram node is bound to, and the camera and template the form offers next. A
  // replica is the diagram's own content and cannot be bound, so the block is a diagram's only.
  const camerasById = useAppSelector((s) => s.live.cameraStatesById)
  const isDiagram = marker.nodeKind === 'diagram'
  const [binding, setBinding] = useState<MapDeviceDiagramBinding | null>(null)
  const [deviceCameraId, setDeviceCameraId] = useState('')
  const [deviceTypeId, setDeviceTypeId] = useState('')
  const cameras = useMemo(
    () => Object.values(camerasById).sort((a, b) => a.name.localeCompare(b.name)),
    [camerasById],
  )

  useEffect(() => {
    if (types.length === 0) void dispatch(loadDiagramTypes())
  }, [types.length, dispatch])

  useEffect(() => {
    if (copy || !isDiagram) return
    void mapApi.getDeviceDiagramBinding(marker.id)
      .then((answer) => setBinding(answer))
      .catch(() => setBinding({ bound: false }))
  }, [copy, isDiagram, marker.id])

  useEffect(() => {
    if (copy || !isDiagram) return
    if (Object.keys(camerasById).length === 0) void dispatch(loadCameras())
  }, [copy, isDiagram, camerasById, dispatch])

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
        dgrType: copy ? undefined : draft.typeId || undefined,
        regionId: inRegion ? draft.regionId : undefined,
        // A region replaces the element's place in its parent, not the canvas it opens on: the
        // rectangle shown below stays its own paper and travels unchanged. For a representation
        // the paper is the owner's, so only its placement travels.
        geometry: inRegion ? (copy ? element.placement : element.geometry) : {
          top: Number(draft.top) || 0,
          left: Number(draft.left) || 0,
          width: Number(draft.width) || 1,
          height: Number(draft.height) || 1,
          rotation: Number(draft.rotation) || 0,
        },
        fileId: copy ? undefined : draft.fileId || undefined,
      })).unwrap()
      onSaved()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  /** Binds this diagram to the chosen camera: the map maintains it from then on. */
  const bindDevice = async () => {
    if (!deviceCameraId) return
    setBusy(true)
    setError(null)
    try {
      await mapApi.bindDeviceDiagram({
        nodeId: marker.id,
        sourceKind: 'camera',
        sourceId: deviceCameraId,
        diagramTypeId: deviceTypeId || undefined,
      })
      setBinding(await mapApi.getDeviceDiagramBinding(marker.id))
      setDeviceTypeId('')
      onSaved()
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  /** Takes the diagram away from its device; it stays an ordinary mnemoscheme. */
  const unbindDevice = async () => {
    setBusy(true)
    setError(null)
    try {
      await mapApi.unbindDeviceDiagram(marker.id)
      setBinding({ bound: false })
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

      <Stack direction="row" spacing={0.5} useFlexGap alignItems="center" sx={{ px: 1.5, pt: 1, flexWrap: 'wrap' }}>
        <Typography variant="caption" color="text.secondary" sx={{ mr: 0.5 }}>
          {t('maps.node.states')}
        </Typography>
        {alarm?.alarm || marker.present === false || (state?.states?.length ?? 0) > 0 ? (
          <NodeStateChips state={state} alarm={alarm} descriptions={descriptions} present={marker.present} />
        ) : (
          <Typography variant="caption" color="text.secondary">{t('maps.node.noStates')}</Typography>
        )}
      </Stack>

      <Box sx={{ p: 1.5, display: 'flex', flexDirection: 'column', gap: 1.5, overflowY: 'auto' }}>
        {error ? <Typography color="error" variant="body2">{error}</Typography> : null}

        <NodeIdentity
          node={marker}
          name={draft.name}
          onNameChange={(name) => set('name', name)}
        />

        {/* A diagram drawn by hand can be made a device's own: the map maintains it from then on —
            the template that describes the device and a replica per configured part — while the
            drawing itself stays the administrator's. A replica shows someone else's content and is
            not a device's diagram, so the block belongs to a diagram node only. */}
        {isDiagram && !copy && binding ? (
          <Box sx={{ border: 1, borderColor: 'divider', borderRadius: 1, p: 1.5 }}>
            <Typography variant="subtitle2" sx={{ mb: 1 }}>
              {t('diagramEditor.device')}
            </Typography>
            {binding.bound ? (
              <Stack direction="row" spacing={1} alignItems="center" useFlexGap flexWrap="wrap">
                <Typography variant="body2" sx={{ flex: 1 }}>
                  {t('diagramEditor.deviceBound', {
                    name: camerasById[binding.sourceId ?? '']?.name || binding.sourceId,
                  })}
                </Typography>
                <Button size="small" disabled={busy} onClick={() => void unbindDevice()}>
                  {t('diagramEditor.unbindDevice')}
                </Button>
              </Stack>
            ) : (
              <Stack spacing={1}>
                <TextField
                  size="small"
                  select
                  label={t('diagramEditor.deviceCamera')}
                  value={deviceCameraId}
                  onChange={(event) => setDeviceCameraId(event.target.value)}
                >
                  {cameras.map((camera) => (
                    <MenuItem key={camera.cameraId} value={camera.cameraId}>
                      {camera.name || camera.cameraId}
                    </MenuItem>
                  ))}
                </TextField>
                <TextField
                  size="small"
                  select
                  label={t('diagramEditor.deviceTemplate')}
                  value={deviceTypeId}
                  onChange={(event) => setDeviceTypeId(event.target.value)}
                >
                  <MenuItem value="">{t('diagramEditor.noTemplate')}</MenuItem>
                  {types.filter((type) => type.sourceKind === 'camera').map((type) => (
                    <MenuItem key={type.id} value={type.id}>{type.name || type.id}</MenuItem>
                  ))}
                </TextField>
                <Typography variant="caption" color="text.secondary">
                  {t('diagramEditor.deviceHint')}
                </Typography>
                <Button
                  size="small"
                  variant="outlined"
                  disabled={busy || !deviceCameraId}
                  onClick={() => void bindDevice()}
                >
                  {t('diagramEditor.bindDevice')}
                </Button>
              </Stack>
            )}
          </Box>
        ) : null}

        {/* The type decides what draws the diagram and which places its children may take; the
            dictionary it comes from is edited right here, so an empty one is not a dead end. */}
        <Stack direction="row" spacing={1} alignItems="flex-start">
          <TextField
            size="small"
            select
            label={t('diagramEditor.type')}
            value={draft.typeId}
            onChange={(event) => set('typeId', event.target.value)}
            disabled={copy}
            helperText={copy ? t('diagramEditor.representationHint') : undefined}
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
          {/* The saved region is not in the parent's type any more: the select shows what the row
              says instead of silently displaying "free", and choosing another region repairs it. */}
          {draft.regionId && !(parentType?.regions ?? [])
            .some((region) => region.regionKey === draft.regionId) ? (
              <MenuItem value={draft.regionId}>
                {t('diagramEditor.missingRegion', { region: draft.regionId })}
              </MenuItem>
            ) : null}
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

        {copy ? null : (
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
        )}

        <Button size="small" onClick={onClose} sx={{ alignSelf: 'flex-end' }}>{t('common.close')}</Button>
      </Box>

      <MapDiagramTypesDialog open={typesOpen} onClose={() => setTypesOpen(false)} />
    </Stack>
  )
}
