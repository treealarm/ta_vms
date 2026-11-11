import { useEffect } from 'react'
import { Button, Stack, TextField } from '@mui/material'
import { useTranslation } from 'react-i18next'
import type { MapDeviceRef, MapMarker } from '../../dto/mapDtos'
import { useAppDispatch, useAppSelector } from '../../store'
import { loadNodes } from '../../store/objectsSlice'

type Props = {
  /** The node the panel shows. */
  node: MapMarker
  /** The value of the name field: a draft's while an editor holds one, the node's name otherwise. */
  name?: string
  /** Hands the name field to the caller: the administrator's editor, and only it, edits it. */
  onNameChange?: (name: string) => void
  /** The device this node is the diagram of, from its own read. */
  device?: MapDeviceRef
  /** Where a replica's owner leads, when the panel can go there. */
  onSelect?: (id: string) => void
  /** Where the camera a diagram draws leads, when the panel can go there. */
  onSelectCamera?: (cameraId: string) => void
}

/**
 * What a node is, in the panel beside it: its name in a field — a long name in a heading is cut
 * with an ellipsis, and this is the name the reader came to see — its id, and the node it stands
 * for: the owner of a replica, or the device a diagram draws. The last two are the links between
 * the map and the logical tree, and neither is visible anywhere else in the panel.
 */
export default function NodeIdentity({
  node, name, onNameChange, device, onSelect, onSelectCamera,
}: Props) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const markers = useAppSelector((s) => s.objects.markers)
  const owner = node.ownerId ? markers[node.ownerId] : undefined

  // A picture of a camera names the camera's canonical node, which the canvas may not have read
  // yet: the label is the name with the id, and half of it is one read away.
  useEffect(() => {
    if (node.ownerId && !markers[node.ownerId]) void dispatch(loadNodes([node.ownerId]))
  }, [node.ownerId, markers, dispatch])

  return (
    <Stack spacing={1}>
      <TextField
        size="small"
        label={t('maps.editor.name')}
        value={name ?? node.name ?? ''}
        onChange={onNameChange ? (event) => onNameChange(event.target.value) : undefined}
        slotProps={{ input: onNameChange ? undefined : { readOnly: true } }}
      />
      <TextField
        size="small"
        label={t('maps.editor.objectId')}
        value={node.id}
        slotProps={{ input: { readOnly: true } }}
        sx={{ '& input': { fontFamily: 'monospace', fontSize: 12 } }}
      />

      {node.ownerId ? (
        <Stack direction="row" spacing={1} alignItems="center">
          <TextField
            size="small"
            fullWidth
            label={t('maps.editor.replicaOf')}
            value={owner?.name ? `${owner.name} · ${node.ownerId}` : node.ownerId}
            slotProps={{ input: { readOnly: true } }}
            sx={{ '& input': { fontSize: 12 } }}
          />
          {onSelect ? (
            <Button size="small" sx={{ flexShrink: 0 }} onClick={() => onSelect(node.ownerId!)}>
              {t('maps.node.owner')}
            </Button>
          ) : null}
        </Stack>
      ) : null}

      {device?.nodeId ? (
        <Stack direction="row" spacing={1} alignItems="center">
          <TextField
            size="small"
            fullWidth
            label={t('maps.node.deviceOf')}
            value={device.name ? `${device.name} · ${device.nodeId}` : device.nodeId}
            slotProps={{ input: { readOnly: true } }}
            sx={{ '& input': { fontSize: 12 } }}
          />
          {onSelectCamera && device.sourceId ? (
            <Button size="small" sx={{ flexShrink: 0 }} onClick={() => onSelectCamera(device.sourceId!)}>
              {t('maps.node.showDevice')}
            </Button>
          ) : null}
        </Stack>
      ) : null}
    </Stack>
  )
}
