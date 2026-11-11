import { useMemo } from 'react'
import { Box, Typography } from '@mui/material'
import type {
  MapAlarmState, MapDiagramContent, MapMarker, MapObjectState, MapStateDescription, MapValue,
} from '../../dto/mapDtos'
import { useMapFileImage } from '../../hooks/useMapFileImage'
import { nodeStyle } from '../../map/nodeStyle'
import { visualImageUrl } from '../../map/visualImage'
import { elementRect, regionRect, type Rect } from './diagramGeometry'

const SELECTED_COLOR = '#64b5f6'

type Props = {
  marker: MapMarker
  /** Where it stands in its parent's canvas, already resolved. */
  rect: Rect
  content: MapDiagramContent
  values: Record<string, MapValue[]>
  states: Record<string, MapObjectState>
  alarms: Record<string, MapAlarmState>
  descriptions: Record<string, MapStateDescription>
  /** How many levels below this one are still drawn. */
  depth: number
  selectedId: string | null
  onPick: (marker: MapMarker) => void
  /** The editor's canvas: an element is dragged by its body to another place or region. */
  onMove?: (marker: MapMarker, rect: Rect, event: React.PointerEvent) => void
  /** False for the open diagram itself: it is the paper, not an element on it. */
  movable?: boolean
  /** The element being dragged, at the rectangle it is currently over. */
  dragged?: { id: string; rect: Rect } | null
  /**
   * Draw the regions of the element's type, key and all, even where no value fills them. The
   * administrator's paper shows the places an element may take; an operator sees only values.
   */
  showRegions?: boolean
}

/**
 * One node of a mnemoscheme: its type's picture (or its own icon) stretched over its rectangle,
 * the values of its type's regions written into them, its children drawn on top — nested by
 * region or by their own coordinates — and a coloured frame when its state or an alarm says so.
 */
export default function DiagramElement({
  marker, rect, content, values, states, alarms, descriptions, depth, selectedId, onPick,
  onMove, movable = true, dragged, showRegions = false,
}: Props) {
  // While it is dragged, the element follows the pointer instead of the rectangle it was read at.
  const shown = dragged?.id === marker.id ? dragged.rect : rect
  const diagram = content.content?.find((candidate) => candidate.id === marker.id)
  const type = content.dgrTypes?.find((candidate) => candidate.id === diagram?.dgrType)
  const visual = content.visuals?.find((candidate) => candidate.objectId === marker.id)

  const fileId = diagram?.fileId || type?.fileId
  const fileUrl = useMapFileImage(fileId)
  const icon = visual?.image ? visualImageUrl(visual.image) : null
  const picture = fileUrl ?? icon

  // A replica's states and alarms are its owner's: the map answers them under the replica's id.
  const state = states[marker.id]
  const alarm = alarms[marker.id]
  const style = nodeStyle({ visual }, state, alarm, descriptions)
  const highlighted = style.alarming || (state?.states?.length ?? 0) > 0
  const selected = marker.id === selectedId
  const ownValues = values[marker.id] ?? []

  // The picture is small and its states are what an operator reads first: the tooltip says them
  // in the dictionary's own words, not in the codes the device reports.
  const describesStates = (state?.states ?? [])
    .map((name) => descriptions[name]?.stateDescr || name)
    .filter(Boolean)

  const children = useMemo(
    () => (depth > 0 ? (content.children ?? []).filter((child) => child.parentId === marker.id) : []),
    [content.children, marker.id, depth],
  )

  return (
    <Box
      onClick={(event) => { event.stopPropagation(); onPick(marker) }}
      onPointerDown={onMove && movable ? (event) => onMove(marker, shown, event) : undefined}
      title={[marker.name, ...describesStates].filter(Boolean).join(' · ')}
      sx={{
        position: 'absolute',
        left: shown.left,
        top: shown.top,
        width: shown.width,
        height: shown.height,
        cursor: onMove && movable ? 'move' : 'pointer',
        // Above the free shapes drawn on the paper (2), below the captions and the shape being drawn.
        zIndex: selected ? 3 : 1,
        // Selection must not be read as an alarm: the alarm frame below is red, this one blue.
        outline: selected ? '2px solid' : 'none',
        outlineColor: SELECTED_COLOR,
        outlineOffset: '1px',
      }}
    >
      {picture ? (
        <img
          src={picture}
          alt=""
          draggable={false}
          style={{
            width: '100%',
            height: '100%',
            objectFit: 'fill',
            transform: `rotate(${shown.rotation}deg)`,
            pointerEvents: 'none',
          }}
        />
      ) : (
        <Box
          sx={{
            width: '100%',
            height: '100%',
            border: '1px dashed',
            borderColor: 'divider',
            display: 'flex',
            flexDirection: 'column',
            alignItems: 'center',
            justifyContent: 'center',
            overflow: 'hidden',
          }}
        >
          <Typography variant="caption" color="text.secondary" noWrap>{marker.name}</Typography>
          {/* An element without a picture of its own — a region of a device's template holding one
              of its parts — writes its state where the template left room for it, in the state's
              colour, as the readings panel writes a value into its region. */}
          {describesStates.length > 0 ? (
            <Typography variant="caption" noWrap sx={{ color: style.color }}>
              {describesStates.join(' · ')}
            </Typography>
          ) : null}
        </Box>
      )}

      {showRegions ? (type?.regions ?? []).map((region) => {
        const area = regionRect(region, shown)
        if (!area) return null

        return (
          <Box
            key={`region:${region.regionKey}`}
            sx={{
              position: 'absolute',
              left: area.left,
              top: area.top,
              width: area.width,
              height: area.height,
              transform: `rotate(${area.rotation}deg)`,
              border: '1px dashed',
              borderColor: 'rgba(255,255,255,0.55)',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              pointerEvents: 'none',
            }}
          >
            <Typography
              variant="caption"
              noWrap
              sx={{
                px: 0.25,
                color: region.styles?.color ?? '#ffffff',
                backgroundColor: 'rgba(0,0,0,0.45)',
                fontSize: 11,
              }}
            >
              {region.regionKey}
            </Typography>
          </Box>
        )
      }) : null}

      {(type?.regions ?? []).map((region) => {
        const value = ownValues.find((candidate) => candidate.name === region.regionKey)
        const area = regionRect(region, shown)
        if (!value || !area) return null

        return (
          <Box
            key={region.regionKey}
            sx={{
              position: 'absolute',
              left: area.left,
              top: area.top,
              width: area.width,
              height: area.height,
              overflow: 'hidden',
              pointerEvents: 'none',
            }}
            style={{
              color: region.styles?.color,
              fontSize: region.styles?.fontSize,
              backgroundColor: region.styles?.backgroundColor,
            }}
          >
            <Typography variant="caption" noWrap>{`${value.name}: ${valueText(value)}`}</Typography>
          </Box>
        )
      })}

      {children.map((child) => {
        const childDiagram = content.content?.find((candidate) => candidate.id === child.id)
        const childRect = elementRect(childDiagram, type, shown)
        if (!childRect) return null

        return (
          <DiagramElement
            key={child.id}
            marker={child}
            rect={childRect}
            content={content}
            values={values}
            states={states}
            alarms={alarms}
            descriptions={descriptions}
            depth={depth - 1}
            selectedId={selectedId}
            onPick={onPick}
            onMove={onMove}
            dragged={dragged}
          />
        )
      })}

      {highlighted ? (
        <Box
          sx={{
            position: 'absolute',
            inset: 0,
            border: `3px solid ${style.color}`,
            bgcolor: style.color,
            opacity: 0.25,
            pointerEvents: 'none',
          }}
        />
      ) : null}
    </Box>
  )
}

/** A value as the region writes it: the number or the text it carries. */
function valueText(value: MapValue): string {
  const carried = value.value
  if (!carried) return ''
  if (carried.stringValue !== undefined) return carried.stringValue
  if (carried.doubleValue !== undefined) return carried.doubleValue.toString()
  if (carried.intValue !== undefined) return carried.intValue.toString()
  return ''
}
