import { useRef, useState } from 'react'
import { Box } from '@mui/material'
import type { MapFigure } from '../../dto/mapDtos'
import { figurePoint } from './diagramGeometry'
import { visualImageUrl } from '../../map/visualImage'

type Props = {
  figures: MapFigure[]
  /** The paper, in the diagram's own pixels: a figure's coordinates are those pixels. */
  paper: { width: number; height: number }
  /** Pixels of paper per pixel of screen, so a drag can be divided back. */
  scale: number
  selectedId?: string | null
  editing?: boolean
  onPick?: (figure: MapFigure) => void
  /** A figure was dragged: its geometry moves by these paper pixels. */
  onMoved?: (figure: MapFigure, dx: number, dy: number) => void
}

const DEFAULT_POINT = 14
const DEFAULT_SHAPE = '#7fd1ff'

const isShape = (figure: MapFigure) =>
  figure.geometry?.type === 'LineString' || figure.geometry?.type === 'Polygon'

/**
 * The free objects drawn on a diagram's paper: lines and polygons under everything, points —
 * a dot, a caption or an icon — above it. They are ordinary figures of the same canvas, so the
 * administrator edits them with the node editor, exactly as on a plan.
 */
export default function DiagramFigures({
  figures, paper, scale, selectedId, editing, onPick, onMoved,
}: Props) {
  const [drag, setDrag] = useState<{ id: string; dx: number; dy: number } | null>(null)
  const dragRef = useRef<{ id: string; dx: number; dy: number } | null>(null)
  // A drag ends before the click that follows it: this says the click is the tail of one.
  const draggedRef = useRef(false)

  const beginDrag = (figure: MapFigure, event: React.PointerEvent) => {
    if (!editing || !onMoved) return
    event.stopPropagation()

    const startX = event.clientX
    const startY = event.clientY
    let moved = false
    draggedRef.current = false

    const move = (event: PointerEvent) => {
      const next = {
        id: figure.id,
        dx: (event.clientX - startX) / scale,
        dy: (event.clientY - startY) / scale,
      }
      if (Math.abs(next.dx) + Math.abs(next.dy) > 0.5) moved = true
      dragRef.current = next
      setDrag(next)
    }
    const up = () => {
      window.removeEventListener('pointermove', move)
      window.removeEventListener('pointerup', up)
      const final = dragRef.current
      dragRef.current = null
      setDrag(null)
      draggedRef.current = moved
      if (moved && final) onMoved(figure, final.dx, final.dy)
    }

    window.addEventListener('pointermove', move)
    window.addEventListener('pointerup', up)
  }

  const shapes = figures.filter(isShape)
  const points = figures.filter((figure) => !isShape(figure))

  return (
    <>
      {shapes.length > 0 ? (
        <svg
          style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', pointerEvents: 'none' }}
          viewBox={`0 0 ${paper.width} ${paper.height}`}
          preserveAspectRatio="none"
        >
          {shapes.map((figure) => {
            const points = (figure.geometry?.coord ?? [])
              .map((coord) => { const at = figurePoint(coord); return `${at.x},${at.y}` })
              .join(' ')
            const colour = figure.visual?.color || DEFAULT_SHAPE
            const selected = figure.id === selectedId

            return figure.geometry?.type === 'Polygon' ? (
              <polygon
                key={figure.id}
                points={points}
                fill={colour}
                fillOpacity={selected ? 0.3 : 0.15}
                stroke={colour}
                strokeWidth={selected ? 3 : 2}
              />
            ) : (
              <polyline
                key={figure.id}
                points={points}
                fill="none"
                stroke={colour}
                strokeWidth={selected ? 3 : 2}
              />
            )
          })}
        </svg>
      ) : null}

      {points.map((figure) => {
        const coord = figure.geometry?.coord?.[0]
        const at = figurePoint(coord)
        const moved = drag?.id === figure.id ? drag : null
        const selected = figure.id === selectedId
        const width = figure.visual?.imageWidth || 0
        const height = figure.visual?.imageHeight || 0
        const colour = figure.visual?.color || DEFAULT_SHAPE
        const rotation = figure.visual?.rotationDeg ?? 0

        return (
          <Box
            key={figure.id}
            title={figure.name}
            onPointerDown={(event) => beginDrag(figure, event)}
            onClick={(event) => {
              // A drag is not a pick: the pointer went somewhere and the node moved with it.
              if (draggedRef.current) { draggedRef.current = false; return }
              event.stopPropagation()
              onPick?.(figure)
            }}
            sx={{
              position: 'absolute',
              left: at.x + (moved?.dx ?? 0),
              top: at.y + (moved?.dy ?? 0),
              transform: 'translate(-50%, -50%)',
              zIndex: 3,
              cursor: editing ? 'move' : 'pointer',
              outline: selected ? '2px solid' : 'none',
              outlineColor: '#4fc3f7',
              outlineOffset: '1px',
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
            }}
          >
            {figure.visual?.image ? (
              <img
                src={visualImageUrl(figure.visual.image)}
                alt=""
                draggable={false}
                style={{
                  width: width || 28,
                  height: height || 28,
                  objectFit: 'contain',
                  transform: `rotate(${rotation}deg)`,
                  pointerEvents: 'none',
                }}
              />
            ) : figure.visual?.text ? (
              <Box
                component="span"
                sx={{
                  px: 0.5,
                  color: colour,
                  fontSize: 12,
                  whiteSpace: 'nowrap',
                  textShadow: '0 1px 2px #000',
                  transform: `rotate(${rotation}deg)`,
                  pointerEvents: 'none',
                }}
              >
                {figure.visual.text}
              </Box>
            ) : (
              <Box
                component="span"
                sx={{
                  width: DEFAULT_POINT,
                  height: DEFAULT_POINT,
                  borderRadius: '50%',
                  bgcolor: colour,
                  display: 'block',
                  pointerEvents: 'none',
                }}
              />
            )}
          </Box>
        )
      })}
    </>
  )
}
