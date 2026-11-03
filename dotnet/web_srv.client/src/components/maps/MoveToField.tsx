import { useMemo, useState } from 'react'
import {
  Button, Dialog, DialogActions, DialogContent, DialogTitle, List, ListItemButton, ListItemText,
  TextField, Typography,
} from '@mui/material'
import DriveFileMoveOutlinedIcon from '@mui/icons-material/DriveFileMoveOutlined'
import { useTranslation } from 'react-i18next'
import { isUnder } from '../../map/tree'
import { useAppDispatch, useAppSelector } from '../../store'
import { moveNode } from '../../store/mapEditsSlice'

type Props = {
  /** The node to move. Its name and owner travel back with it. */
  nodeId?: string | null
  /** The canvas open in the editor: a move can only stay inside it, or go into one of its spaces. */
  canvasId: string
}

type Target = { id: string; name: string; kind: string }

/**
 * Moves a node somewhere else in the tree. Geometry never makes a node a child of a container --
 * the administrator does, and this is the only way through: a space, or any node drawn on the same
 * canvas. The tree is refused a move under the node's own descendant, here and on the server.
 */
export default function MoveToField({ nodeId, canvasId }: Props) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const markers = useAppSelector((s) => s.objects.markers)
  const canvases = useAppSelector((s) => s.objects.canvases)
  const spaces = useAppSelector((s) => s.spatial.spaces)
  const marker = nodeId ? markers[nodeId] : undefined

  const [open, setOpen] = useState(false)
  const [filter, setFilter] = useState('')

  const targets = useMemo(() => {
    if (!nodeId) return [] as Target[]

    const onCanvas = (canvases[canvasId]?.ids ?? [])
      .filter((id) => id !== nodeId && !isUnder(markers, id, nodeId))
      .map((id) => ({
        id,
        name: markers[id]?.name || id,
        kind: markers[id]?.nodeKind ?? 'object',
      }))

    // Every space can hold a node; one on this canvas keeps it drawn here, one nested elsewhere
    // takes it to that space's own canvas.
    const spaceTargets: Target[] = spaces
      .filter((s) => s.id && s.id !== nodeId && !isUnder(markers, s.id, nodeId))
      .map((s) => ({ id: s.id!, name: s.name, kind: 'space' }))

    const seen = new Set<string>()
    return [...spaceTargets, ...onCanvas]
      .filter((target) => (seen.has(target.id) ? false : (seen.add(target.id), true)))
      .sort((a, b) => a.kind.localeCompare(b.kind) || a.name.localeCompare(b.name))
  }, [nodeId, canvasId, canvases, markers, spaces])

  if (!nodeId || !marker) return null

  const needle = filter.trim().toLocaleLowerCase()
  const shown = needle
    ? targets.filter((target) => target.name.toLocaleLowerCase().includes(needle))
    : targets

  const move = (target: Target) => {
    void dispatch(moveNode({
      node: { id: marker.id, parentId: target.id, ownerId: marker.ownerId, name: marker.name },
      canvasId,
    }))
    setOpen(false)
    setFilter('')
  }

  return (
    <>
      <Button
        size="small"
        variant="outlined"
        startIcon={<DriveFileMoveOutlinedIcon fontSize="small" />}
        onClick={() => setOpen(true)}
      >
        {t('maps.editor.moveTo')}
      </Button>

      <Dialog open={open} onClose={() => setOpen(false)} maxWidth="xs" fullWidth>
        <DialogTitle>{t('maps.editor.moveToTitle', { name: marker.name || marker.id })}</DialogTitle>
        <DialogContent>
          <Typography variant="caption" color="text.secondary" display="block" sx={{ mb: 1 }}>
            {t('maps.editor.moveToHint')}
          </Typography>
          <TextField
            size="small"
            fullWidth
            autoFocus
            placeholder={t('maps.editor.moveToFind')}
            value={filter}
            onChange={(event) => setFilter(event.target.value)}
            sx={{ mb: 1 }}
          />
          <List dense sx={{ maxHeight: 320, overflowY: 'auto' }}>
            {shown.map((target) => (
              <ListItemButton
                key={target.id}
                selected={target.id === marker.parentId}
                onClick={() => move(target)}
              >
                <ListItemText
                  primary={target.name}
                  secondary={t(`maps.node.kind.${target.kind}`)}
                />
              </ListItemButton>
            ))}
            {shown.length === 0 && (
              <Typography variant="body2" color="text.secondary" sx={{ px: 1, py: 1 }}>
                {t('maps.editor.moveToNothing')}
              </Typography>
            )}
          </List>
        </DialogContent>
        <DialogActions>
          <Button size="small" onClick={() => setOpen(false)}>{t('common.close')}</Button>
        </DialogActions>
      </Dialog>
    </>
  )
}
