import { useEffect } from 'react'
import { MenuItem, TextField } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../../store'
import { loadLevels } from '../../store/levelsSlice'
import { saveZoomLevel } from '../../store/mapEditsSlice'
import { loadNodes } from '../../store/objectsSlice'

type Props = {
  /** The node whose own shape carries the layer. */
  nodeId?: string | null
}

/**
 * The scale layer a node is drawn on: one of the dictionary's layers, or none, which always shows.
 * The layer lives on the node's shape, so the shape is read here even when it is not drawn on the
 * open canvas — a space's own footprint belongs to its parent's canvas.
 */
export default function ZoomLevelField({ nodeId }: Props) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const levels = useAppSelector((s) => s.levels.levels)
  const figure = useAppSelector((s) => (nodeId ? s.objects.figures[nodeId] : undefined))

  useEffect(() => {
    if (nodeId) void dispatch(loadNodes([nodeId]))
  }, [nodeId, dispatch])

  useEffect(() => {
    if (levels.length === 0) void dispatch(loadLevels())
  }, [levels.length, dispatch])

  if (!nodeId) return null

  return (
    <TextField
      size="small"
      select
      label={t('maps.editor.zoomLevel')}
      value={figure?.zoomLevel ?? ''}
      disabled={!figure}
      helperText={figure ? undefined : t('maps.editor.zoomNeedsShape')}
      onChange={(event) => {
        if (!figure) return
        void dispatch(saveZoomLevel({ ...figure, zoomLevel: event.target.value || undefined }))
      }}
    >
      <MenuItem value="">{t('maps.editor.alwaysVisible')}</MenuItem>
      {levels.filter((level) => !!level.zoomLevel).map((level) => (
        <MenuItem key={level.id ?? level.zoomLevel} value={level.zoomLevel}>{level.zoomLevel}</MenuItem>
      ))}
    </TextField>
  )
}
