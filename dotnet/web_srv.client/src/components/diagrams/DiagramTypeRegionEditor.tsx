import { useRef, useState } from 'react'
import {
  Box, Button, IconButton, Stack, TextField, Tooltip, Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import RotateRightIcon from '@mui/icons-material/RotateRight'
import { useTranslation } from 'react-i18next'
import type { MapDiagramCoordinate, MapDiagramType, MapDiagramTypeRegion } from '../../dto/mapDtos'
import { useMapFileImage } from '../../hooks/useMapFileImage'
import { clampRegion, normalizeRect, type SharePoint, type ShareRect } from './diagramGeometry'

type Props = {
  type: MapDiagramType
  /** A region was drawn, moved, resized or styled: the dialog writes the type back. */
  onChange: (patch: Partial<MapDiagramType>) => void
}

type Handle = 'nw' | 'n' | 'ne' | 'e' | 'se' | 's' | 'sw' | 'w'

const HANDLES: Handle[] = ['nw', 'n', 'ne', 'e', 'se', 's', 'sw', 'w']

/** A region no smaller than this is a drawing; a stray click is not. */
const MIN_DRAW = 0.02

const DEFAULT_STYLES: Record<string, string> = { color: '#ffd54f', fontSize: '14px' }

const handlePlacement: Record<Handle, Record<string, string | number>> = {
  nw: { left: -5, top: -5 },
  n: { left: '50%', top: -5, marginLeft: '-5px' },
  ne: { right: -5, top: -5 },
  e: { right: -5, top: '50%', marginTop: '-5px' },
  se: { right: -5, bottom: -5 },
  s: { left: '50%', bottom: -5, marginLeft: '-5px' },
  sw: { left: -5, bottom: -5 },
  w: { left: -5, top: '50%', marginTop: '-5px' },
}

const cursorOf: Record<Handle, string> = {
  nw: 'nwse-resize', n: 'ns-resize', ne: 'nesw-resize', e: 'ew-resize',
  se: 'nwse-resize', s: 'ns-resize', sw: 'nesw-resize', w: 'ew-resize',
}

const shareRect = (geometry?: MapDiagramCoordinate): ShareRect => ({
  left: geometry?.left ?? 0,
  top: geometry?.top ?? 0,
  width: geometry?.width ?? 0,
  height: geometry?.height ?? 0,
  rotation: geometry?.rotation ?? 0,
})

const clamp01 = (value: number) => Math.min(1, Math.max(0, value))

/**
 * The regions of one diagram type, drawn on its background: drag on the empty paper to make one,
 * drag a region to move it, its corners and edges to resize it and the knob above it to turn it.
 * The numeric fields under the paper edit the same rectangle exactly; they remain because a mouse
 * cannot say "0.125".
 */
export default function DiagramTypeRegionEditor({ type, onChange }: Props) {
  const { t } = useTranslation()
  const paperRef = useRef<HTMLDivElement | null>(null)
  const [selected, setSelected] = useState<number | null>(null)
  const [draft, setDraft] = useState<ShareRect | null>(null)
  const [aspect, setAspect] = useState(4 / 3)

  const background = useMapFileImage(type.fileId)
  const regions = type.regions ?? []
  const chosen = selected !== null && selected >= 0 && selected < regions.length ? regions[selected] : null

  /** A pointer position in shares of the paper, kept on the paper. */
  const pointOf = (event: PointerEvent | React.PointerEvent): SharePoint | null => {
    const bounds = paperRef.current?.getBoundingClientRect()
    if (!bounds || bounds.width <= 0 || bounds.height <= 0) return null
    return {
      x: clamp01((event.clientX - bounds.left) / bounds.width),
      y: clamp01((event.clientY - bounds.top) / bounds.height),
    }
  }

  /** Follows a pointer drag until it is let go. */
  const follow = (move: (point: SharePoint) => void, done?: (point: SharePoint) => void) => {
    const onMove = (event: PointerEvent) => {
      const point = pointOf(event)
      if (point) move(point)
    }
    const onUp = (event: PointerEvent) => {
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('pointerup', onUp)
      const point = pointOf(event)
      if (point) done?.(point)
    }
    window.addEventListener('pointermove', onMove)
    window.addEventListener('pointerup', onUp)
  }

  const changeRegion = (index: number, patch: Partial<MapDiagramTypeRegion>) =>
    onChange({
      regions: regions.map((region, i) => (i === index ? { ...region, ...patch } : region)),
    })

  const setGeometry = (index: number, geometry: ShareRect) =>
    changeRegion(index, { geometry: clampRegion(geometry) })

  const addRegion = (geometry: ShareRect) => {
    const keys = new Set(regions.map((region) => region.regionKey))
    let number = regions.length + 1
    while (keys.has(`region-${number}`)) number += 1

    onChange({
      regions: [...regions, {
        regionKey: `region-${number}`,
        geometry: clampRegion(geometry),
        styles: { ...DEFAULT_STYLES },
      }],
    })
    setSelected(regions.length)
  }

  /** Drawing on the empty paper: a press, a drag, and a region the size of it. */
  const beginDraw = (event: React.PointerEvent) => {
    if (event.button !== 0) return
    const start = pointOf(event)
    if (!start) return

    setSelected(null)
    setDraft(normalizeRect(start, start))
    follow(
      (point) => setDraft(normalizeRect(start, point)),
      (point) => {
        setDraft(null)
        const rect = normalizeRect(start, point)
        if (rect.width >= MIN_DRAW && rect.height >= MIN_DRAW) addRegion(rect)
      },
    )
  }

  const beginMove = (index: number, event: React.PointerEvent) => {
    if (event.button !== 0) return
    event.stopPropagation()
    const start = pointOf(event)
    if (!start) return

    setSelected(index)
    const geometry = shareRect(regions[index].geometry)
    follow((point) => setGeometry(index, {
      ...geometry,
      left: geometry.left + point.x - start.x,
      top: geometry.top + point.y - start.y,
    }))
  }

  const beginResize = (index: number, handle: Handle, event: React.PointerEvent) => {
    if (event.button !== 0) return
    event.stopPropagation()
    const start = pointOf(event)
    if (!start) return

    const geometry = shareRect(regions[index].geometry)
    follow((point) => {
      let left = geometry.left
      let top = geometry.top
      let right = geometry.left + geometry.width
      let bottom = geometry.top + geometry.height
      const dx = point.x - start.x
      const dy = point.y - start.y

      if (handle.includes('w')) left = Math.min(right, Math.max(0, left + dx))
      if (handle.includes('e')) right = Math.max(left, Math.min(1, right + dx))
      if (handle.includes('n')) top = Math.min(bottom, Math.max(0, top + dy))
      if (handle.includes('s')) bottom = Math.max(top, Math.min(1, bottom + dy))

      setGeometry(index, {
        left, top, width: right - left, height: bottom - top, rotation: geometry.rotation,
      })
    })
  }

  /** The knob above the region turns it; the angle is measured from its centre in pixels. */
  const beginRotate = (index: number, event: React.PointerEvent) => {
    if (event.button !== 0) return
    event.stopPropagation()
    const bounds = paperRef.current?.getBoundingClientRect()
    if (!bounds || bounds.width <= 0 || bounds.height <= 0) return

    const geometry = shareRect(regions[index].geometry)
    const centreX = bounds.left + bounds.width * (geometry.left + geometry.width / 2)
    const centreY = bounds.top + bounds.height * (geometry.top + geometry.height / 2)
    const angleOf = (clientX: number, clientY: number) =>
      Math.round(((Math.atan2(clientY - centreY, clientX - centreX) * 180) / Math.PI + 90 + 360) % 360)

    setSelected(index)
    const apply = (clientX: number, clientY: number) =>
      changeRegion(index, { geometry: { ...geometry, rotation: angleOf(clientX, clientY) } })

    const onMove = (drag: PointerEvent) => apply(drag.clientX, drag.clientY)
    const onUp = (drag: PointerEvent) => {
      window.removeEventListener('pointermove', onMove)
      window.removeEventListener('pointerup', onUp)
      apply(drag.clientX, drag.clientY)
    }
    window.addEventListener('pointermove', onMove)
    window.addEventListener('pointerup', onUp)
  }

  const editNumber = (index: number, field: 'left' | 'top' | 'width' | 'height' | 'rotation', value: string) => {
    const geometry = shareRect(regions[index].geometry)
    const parsed = Number(value)
    setGeometry(index, { ...geometry, [field]: Number.isFinite(parsed) ? parsed : 0 })
  }

  return (
    <Stack spacing={1}>
      <Box
        ref={paperRef}
        onPointerDown={beginDraw}
        sx={{
          position: 'relative',
          width: '100%',
          aspectRatio: String(aspect),
          bgcolor: '#151515',
          border: 1,
          borderColor: 'divider',
          overflow: 'hidden',
          cursor: 'crosshair',
          touchAction: 'none',
          userSelect: 'none',
        }}
      >
        {background ? (
          <Box
            component="img"
            src={background}
            alt=""
            draggable={false}
            onLoad={(event) => {
              const image = event.currentTarget
              if (image.naturalWidth > 0 && image.naturalHeight > 0) {
                setAspect(image.naturalWidth / image.naturalHeight)
              }
            }}
            sx={{ position: 'absolute', inset: 0, width: '100%', height: '100%', objectFit: 'contain', pointerEvents: 'none' }}
          />
        ) : null}

        {regions.map((region, index) => {
          const geometry = shareRect(region.geometry)
          const isSelected = index === selected

          return (
            <Box
              key={`${region.regionKey}:${index}`}
              onPointerDown={(event) => beginMove(index, event)}
              sx={{
                position: 'absolute',
                left: `${geometry.left * 100}%`,
                top: `${geometry.top * 100}%`,
                width: `${geometry.width * 100}%`,
                height: `${geometry.height * 100}%`,
                transform: `rotate(${geometry.rotation}deg)`,
                border: '1px dashed',
                borderColor: isSelected ? 'primary.light' : 'rgba(255,255,255,0.45)',
                bgcolor: isSelected ? 'rgba(100,181,246,0.14)' : 'rgba(255,255,255,0.04)',
                cursor: 'move',
                display: 'flex',
                alignItems: 'center',
                justifyContent: 'center',
                overflow: 'visible',
              }}
            >
              <Typography
                variant="caption"
                noWrap
                sx={{ px: 0.5, color: region.styles?.color ?? DEFAULT_STYLES.color, fontSize: 12 }}
              >
                {region.regionKey || t('diagramEditor.regionKey')}
              </Typography>

              {isSelected ? HANDLES.map((handle) => (
                <Box
                  key={handle}
                  onPointerDown={(event) => beginResize(index, handle, event)}
                  sx={{
                    position: 'absolute',
                    width: 10,
                    height: 10,
                    bgcolor: 'primary.light',
                    border: '1px solid #fff',
                    cursor: cursorOf[handle],
                    ...handlePlacement[handle],
                  }}
                />
              )) : null}

              {isSelected ? (
                <Tooltip title={t('diagramEditor.rotate')}>
                  <Box
                    onPointerDown={(event) => beginRotate(index, event)}
                    sx={{
                      position: 'absolute',
                      left: '50%',
                      top: -26,
                      marginLeft: '-9px',
                      width: 18,
                      height: 18,
                      borderRadius: '50%',
                      bgcolor: 'primary.light',
                      border: '1px solid #fff',
                      cursor: 'grab',
                      display: 'flex',
                      alignItems: 'center',
                      justifyContent: 'center',
                    }}
                  >
                    <RotateRightIcon sx={{ fontSize: 12 }} />
                  </Box>
                </Tooltip>
              ) : null}
            </Box>
          )
        })}

        {draft ? (
          <Box
            sx={{
              position: 'absolute',
              left: `${draft.left * 100}%`,
              top: `${draft.top * 100}%`,
              width: `${draft.width * 100}%`,
              height: `${draft.height * 100}%`,
              border: '1px dashed',
              borderColor: 'primary.light',
              bgcolor: 'rgba(100,181,246,0.12)',
              pointerEvents: 'none',
            }}
          />
        ) : null}
      </Box>

      <Stack direction="row" spacing={1} alignItems="center">
        <Typography variant="caption" color="text.secondary" sx={{ flex: 1 }}>
          {t('diagramEditor.drawHint')}
        </Typography>
        <Button
          size="small"
          startIcon={<AddIcon />}
          onClick={() => addRegion({ left: 0.1, top: 0.1, width: 0.2, height: 0.1, rotation: 0 })}
        >
          {t('diagramEditor.addRegion')}
        </Button>
      </Stack>

      {chosen ? (
        <Stack spacing={1} sx={{ p: 1, border: 1, borderColor: 'divider', borderRadius: 1 }}>
          <Stack direction="row" spacing={0.5} alignItems="center" flexWrap="wrap" useFlexGap>
            <TextField
              size="small"
              label={t('diagramEditor.regionKey')}
              value={chosen.regionKey ?? ''}
              onChange={(event) => changeRegion(selected!, { regionKey: event.target.value })}
              sx={{ width: 150 }}
            />
            <TextField
              size="small"
              type="color"
              label={t('diagramEditor.color')}
              value={chosen.styles?.color ?? DEFAULT_STYLES.color}
              onChange={(event) => changeRegion(selected!, {
                styles: { ...chosen.styles, color: event.target.value },
              })}
              InputLabelProps={{ shrink: true }}
              sx={{ width: 76 }}
            />
            <TextField
              size="small"
              type="color"
              label={t('diagramEditor.backgroundColor')}
              value={chosen.styles?.backgroundColor ?? '#000000'}
              onChange={(event) => changeRegion(selected!, {
                styles: { ...chosen.styles, backgroundColor: event.target.value },
              })}
              InputLabelProps={{ shrink: true }}
              sx={{ width: 92 }}
            />
            <TextField
              size="small"
              label={t('diagramEditor.fontSize')}
              value={chosen.styles?.fontSize ?? DEFAULT_STYLES.fontSize}
              onChange={(event) => changeRegion(selected!, {
                styles: { ...chosen.styles, fontSize: event.target.value },
              })}
              sx={{ width: 96 }}
            />
            <Box sx={{ flex: 1 }} />
            <IconButton
              size="small"
              title={t('diagramEditor.deleteRegion')}
              onClick={() => {
                onChange({ regions: regions.filter((_, i) => i !== selected) })
                setSelected(null)
              }}
            >
              <DeleteOutlineIcon fontSize="small" />
            </IconButton>
          </Stack>

          {/* A region may stand for a part of a real device — and only for a part that is
              configured on it. One way of finding it per region; the token is the one that
              survives a firmware update. */}
          <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap" useFlexGap>
            {chosen.part ? (
              <>
                <TextField
                  size="small"
                  select
                  SelectProps={{ native: true }}
                  label={t('diagramEditor.partKind')}
                  value={chosen.part.partKind ?? 'relay'}
                  onChange={(event) => changeRegion(selected!, {
                    part: { ...chosen.part, partKind: event.target.value },
                  })}
                  sx={{ width: 150 }}
                >
                  <option value="digital_input">{t('diagramEditor.partKindInput')}</option>
                  <option value="relay">{t('diagramEditor.partKindRelay')}</option>
                </TextField>
                <TextField
                  size="small"
                  select
                  SelectProps={{ native: true }}
                  label={t('diagramEditor.matchBy')}
                  value={chosen.part.matchBy ?? 'token'}
                  onChange={(event) => changeRegion(selected!, {
                    part: { ...chosen.part, matchBy: event.target.value },
                  })}
                  sx={{ width: 130 }}
                >
                  <option value="token">{t('diagramEditor.matchByToken')}</option>
                  <option value="name">{t('diagramEditor.matchByName')}</option>
                  <option value="index">{t('diagramEditor.matchByIndex')}</option>
                </TextField>
                <TextField
                  size="small"
                  label={t('diagramEditor.matchValue')}
                  value={chosen.part.matchValue ?? ''}
                  onChange={(event) => changeRegion(selected!, {
                    part: { ...chosen.part, matchValue: event.target.value },
                  })}
                  sx={{ width: 160 }}
                />
                <Button
                  size="small"
                  onClick={() => changeRegion(selected!, { part: undefined })}
                >
                  {t('diagramEditor.clearBinding')}
                </Button>
              </>
            ) : (
              <Button
                size="small"
                startIcon={<AddIcon />}
                onClick={() => changeRegion(selected!, {
                  part: { partKind: 'relay', matchBy: 'token', matchValue: '' },
                })}
              >
                {t('diagramEditor.bindPart')}
              </Button>
            )}
          </Stack>

          <Stack direction="row" spacing={0.5} flexWrap="wrap" useFlexGap>
            {(['left', 'top', 'width', 'height', 'rotation'] as const).map((field) => (
              <TextField
                key={field}
                size="small"
                type="number"
                label={t(`diagramEditor.${field}`)}
                value={Number(shareRect(chosen.geometry)[field].toFixed(3))}
                onChange={(event) => editNumber(selected!, field, event.target.value)}
                InputLabelProps={{ shrink: true }}
                inputProps={field === 'rotation' ? { step: 15 } : { step: 0.01 }}
                sx={{ width: 92 }}
              />
            ))}
          </Stack>
        </Stack>
      ) : null}
    </Stack>
  )
}
