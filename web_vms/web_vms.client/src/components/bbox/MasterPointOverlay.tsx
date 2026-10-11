import { useCallback, useEffect, useRef } from 'react'
import type { RefObject } from 'react'
import { letterboxLayout } from './bboxDraw'

export type MasterPoint = {
  masterX: number
  masterY: number
  /** Presets only: a point without one cannot aim, and its zone is drawn as a hole. */
  presetToken?: string | null
  /** Drawn on the marker in place of its position in the list, for points numbered by something else. */
  label?: string
}

interface Props {
  videoRef: RefObject<HTMLVideoElement | null>
  points: MasterPoint[]
  /** Index of the point being edited, drawn apart from the settled ones. */
  activeIndex?: number | null
  /** Index of the point selected in the list, so the two stay in step. */
  selectedIndex?: number | null
  /**
   * Shade each point's catchment — which part of the master's frame sends the slave to it.
   *
   * Only meaningful for the presets method, where aiming really is a lookup of the nearest marked
   * point and the frame really is carved into zones. The coordinates method interpolates instead:
   * every spot maps to its own position and there are no zones to show.
   */
  showZones?: boolean
  /** Fired with 0..1 frame coordinates. Absent makes the overlay read-only. */
  onPick?: (x: number, y: number) => void
  /** Fired continuously while a marked spot is dragged. */
  onMovePoint?: (index: number, x: number, y: number) => void
  /** Fired once when a drag ends, so the move can be written without a request per mouse move. */
  onCommitPoint?: (index: number) => void
  onSelectPoint?: (index: number) => void
  /** Where in the picture the pointer is, 0..1, while it moves over it without dragging; null when it leaves. */
  onHover?: (at: { x: number; y: number } | null) => void
}

const RADIUS = 8
/** Slop around a marker for grabbing it: a 8px dot is smaller than a person aims with a mouse. */
const GRAB_RADIUS = 14

// One hue per marked point, walked around the circle so neighbours never share a colour.
const ZONE_HUES = [200, 30, 130, 280, 55, 330, 165, 15, 250, 95]

// The zone map is painted at a fraction of the display size and scaled up. Aiming is a
// nearest-point lookup, so the boundaries are straight lines the eye reads fine from a coarse
// grid — and painting per display pixel would be a distance computation per point per pixel on
// every resize.
const ZONE_SCALE = 4

/**
 * The marked spots of a master/slave survey, drawn over the master's picture.
 *
 * Canvas and the same letterbox arithmetic as BboxCanvas, for the same reason: a marker has to sit
 * on the spot in the *picture* the operator clicked, and the video element is letterboxed inside
 * whatever box the layout gives it. Doing this with positioned DOM elements would need the same
 * arithmetic and re-run React on every resize.
 */
export default function MasterPointOverlay({
  videoRef, points, activeIndex, selectedIndex, showZones,
  onPick, onMovePoint, onCommitPoint, onSelectPoint, onHover,
}: Props) {
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const pointsRef = useRef(points)
  const activeRef = useRef(activeIndex)
  const selectedRef = useRef(selectedIndex)
  const zonesRef = useRef(showZones)
  // Handlers are attached to the <video> once and must not be re-bound on every render, so what
  // they need is read through refs rather than closed over.
  const callbacks = useRef({ onPick, onMovePoint, onCommitPoint, onSelectPoint, onHover })
  const drag = useRef<{ index: number; moved: boolean } | null>(null)

  const draw = useCallback(() => {
    const canvas = canvasRef.current
    const video = videoRef.current
    if (!canvas || !video) return

    const layout = letterboxLayout(video)
    if (!layout) return

    const dpr = window.devicePixelRatio || 1
    canvas.width = layout.elW * dpr
    canvas.height = layout.elH * dpr
    canvas.style.width = `${layout.elW}px`
    canvas.style.height = `${layout.elH}px`

    const ctx = canvas.getContext('2d')
    if (!ctx) return
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
    ctx.clearRect(0, 0, layout.elW, layout.elH)

    if (zonesRef.current && pointsRef.current.length > 0)
      drawZones(ctx, layout, pointsRef.current)

    ctx.font = 'bold 11px monospace'
    ctx.textAlign = 'center'
    ctx.textBaseline = 'middle'

    pointsRef.current.forEach((point, index) => {
      const x = layout.offX + (point.masterX * layout.dispW)
      const y = layout.offY + (point.masterY * layout.dispH)
      const active = index === activeRef.current
      const selected = index === selectedRef.current

      if (selected) {
        ctx.beginPath()
        ctx.arc(x, y, RADIUS + 5, 0, Math.PI * 2)
        ctx.strokeStyle = 'rgba(255,255,255,0.9)'
        ctx.lineWidth = 2
        ctx.stroke()
      }

      ctx.beginPath()
      ctx.arc(x, y, RADIUS, 0, Math.PI * 2)
      ctx.fillStyle = active ? 'rgba(255,167,38,0.9)' : 'rgba(33,150,243,0.85)'
      ctx.fill()
      ctx.lineWidth = 2
      ctx.strokeStyle = 'rgba(0,0,0,0.7)'
      ctx.stroke()

      ctx.fillStyle = '#fff'
      ctx.fillText(point.label ?? String(index + 1), x, y + 0.5)
    })
  }, [videoRef])

  useEffect(() => {
    pointsRef.current = points
    activeRef.current = activeIndex
    selectedRef.current = selectedIndex
    zonesRef.current = showZones
    callbacks.current = { onPick, onMovePoint, onCommitPoint, onSelectPoint, onHover }
    draw()
  }, [
    points, activeIndex, selectedIndex, showZones,
    onPick, onMovePoint, onCommitPoint, onSelectPoint, onHover, draw,
  ])

  useEffect(() => {
    const video = videoRef.current
    if (!video) return
    const ro = new ResizeObserver(() => draw())
    ro.observe(video)
    video.addEventListener('loadedmetadata', draw)
    return () => {
      ro.disconnect()
      video.removeEventListener('loadedmetadata', draw)
    }
  }, [videoRef, draw])

  useEffect(() => {
    const video = videoRef.current
    if (!video) return

    /** Where in the picture, 0..1, or null for a click on the letterbox bars. */
    const frameAt = (event: PointerEvent): { x: number; y: number } | null => {
      const layout = letterboxLayout(video)
      if (!layout) return null
      const rect = video.getBoundingClientRect()
      const x = (event.clientX - rect.left - layout.offX) / layout.dispW
      const y = (event.clientY - rect.top - layout.offY) / layout.dispH
      if (x < 0 || x > 1 || y < 0 || y > 1) return null
      return { x, y }
    }

    const hit = (at: { x: number; y: number }): number | null => {
      const layout = letterboxLayout(video)
      if (!layout) return null
      // Topmost first, so the marker drawn over another is the one grabbed.
      for (let i = pointsRef.current.length - 1; i >= 0; i -= 1) {
        const dx = (pointsRef.current[i].masterX - at.x) * layout.dispW
        const dy = (pointsRef.current[i].masterY - at.y) * layout.dispH
        if (Math.sqrt((dx * dx) + (dy * dy)) <= GRAB_RADIUS) return i
      }
      return null
    }

    const onDown = (event: PointerEvent) => {
      const at = frameAt(event)
      if (!at) return
      const index = hit(at)
      if (index === null) return
      // Grabbing an existing marker is never also a click on the picture, so the pick handler must
      // not see it: that would drop a second marker on top of the one being moved.
      event.preventDefault()
      event.stopPropagation()
      drag.current = { index, moved: false }
      video.setPointerCapture(event.pointerId)
      callbacks.current.onSelectPoint?.(index)
    }

    const onMove = (event: PointerEvent) => {
      if (!drag.current) {
        callbacks.current.onHover?.(frameAt(event))
        return
      }
      const at = frameAt(event)
      if (!at) return
      drag.current.moved = true
      callbacks.current.onMovePoint?.(drag.current.index, at.x, at.y)
    }

    const onUp = (event: PointerEvent) => {
      if (!drag.current) return
      const wasDragging = drag.current
      drag.current = null
      if (video.hasPointerCapture(event.pointerId))
        video.releasePointerCapture(event.pointerId)
      if (wasDragging.moved) {
        // Swallow the click the browser sends after a drag, or it would mark a fresh spot where
        // the operator merely let go.
        event.preventDefault()
        event.stopPropagation()
        // Written now rather than on every pointermove: a drag across the picture is dozens of
        // positions and only the last one is a decision.
        callbacks.current.onCommitPoint?.(wasDragging.index)
      }
    }

    const onClick = (event: MouseEvent) => {
      if (!callbacks.current.onPick) return
      const layout = letterboxLayout(video)
      if (!layout) return
      const rect = video.getBoundingClientRect()
      const x = (event.clientX - rect.left - layout.offX) / layout.dispW
      const y = (event.clientY - rect.top - layout.offY) / layout.dispH
      // The letterbox bars are not part of the picture, so a click on one means nothing.
      if (x < 0 || x > 1 || y < 0 || y > 1) return
      // A click that landed on an existing marker selects it rather than adding a neighbour a few
      // pixels away — which is almost never what was meant.
      const index = hit({ x, y })
      if (index !== null) {
        callbacks.current.onSelectPoint?.(index)
        return
      }
      event.preventDefault()
      event.stopPropagation()
      callbacks.current.onPick(x, y)
    }

    video.addEventListener('pointerdown', onDown)
    video.addEventListener('pointermove', onMove)
    video.addEventListener('pointerup', onUp)
    video.addEventListener('pointercancel', onUp)
    video.addEventListener('click', onClick)
    const onLeave = () => callbacks.current.onHover?.(null)
    video.addEventListener('pointerleave', onLeave)
    return () => {
      video.removeEventListener('pointerleave', onLeave)
      video.removeEventListener('pointerdown', onDown)
      video.removeEventListener('pointermove', onMove)
      video.removeEventListener('pointerup', onUp)
      video.removeEventListener('pointercancel', onUp)
      video.removeEventListener('click', onClick)
    }
  }, [videoRef])

  return (
    <canvas
      ref={canvasRef}
      style={{ position: 'absolute', inset: 0, pointerEvents: 'none' }}
    />
  )
}

/**
 * Paints each marked point's catchment: the part of the frame from which it is the nearest.
 *
 * This is the aiming rule drawn literally rather than an illustration of it — the same nearest-point
 * test the slave runs, evaluated over the picture.
 *
 * The shading fades with distance from the point that owns it, and that is a concession to the eye
 * rather than to the arithmetic. A flat Voronoi cell reads as a wall that the marked spots stand
 * either side of, when the thing being shown is the opposite: the spot is the subject, and the area
 * is its reach. Fading from the point outwards puts the emphasis back on it, while the boundaries
 * stay exactly where the algorithm puts them — there is no radius here, every part of the frame
 * belongs to somebody.
 *
 * A point with no preset is drawn as a hole rather than a colour. Its zone is real — the slave will
 * still resolve there — but there is nothing to send the camera to, so that patch of the master's
 * view is dead, and showing it as absence says so better than any count of missing presets.
 */
function drawZones(
  ctx: CanvasRenderingContext2D,
  layout: { offX: number; offY: number; dispW: number; dispH: number },
  points: MasterPoint[],
) {
  const w = Math.max(1, Math.round(layout.dispW / ZONE_SCALE))
  const h = Math.max(1, Math.round(layout.dispH / ZONE_SCALE))

  const map = document.createElement('canvas')
  map.width = w
  map.height = h
  const mapCtx = map.getContext('2d')
  if (!mapCtx) return

  const image = mapCtx.createImageData(w, h)
  for (let y = 0; y < h; y += 1) {
    for (let x = 0; x < w; x += 1) {
      // Cell centres, so a zone's colour describes the area the cell covers rather than its corner.
      const fx = (x + 0.5) / w
      const fy = (y + 0.5) / h

      let nearest = 0
      let best = Infinity
      let second = Infinity
      for (let i = 0; i < points.length; i += 1) {
        const dx = points[i].masterX - fx
        const dy = points[i].masterY - fy
        const distance = (dx * dx) + (dy * dy)
        if (distance < best) {
          second = best
          best = distance
          nearest = i
        } else if (distance < second) {
          second = distance
        }
      }

      const at = ((y * w) + x) * 4
      if (!points[nearest].presetToken) {
        // Left transparent: the hole is the message.
        image.data[at + 3] = 0
        continue
      }

      const [r, g, b] = hueToRgb(ZONE_HUES[nearest % ZONE_HUES.length])
      image.data[at] = r
      image.data[at + 1] = g
      image.data[at + 2] = b
      // Strongest at the marked spot and fading outwards, with a floor so a zone never disappears
      // and its extent stays readable. The second-nearest distance keeps the edge visible where two
      // zones meet far from either point, which is exactly where the boundary is easiest to miss.
      const near = 1 - Math.min(1, Math.sqrt(best) / 0.35)
      const edge = second === Infinity
        ? 0
        : 1 - Math.min(1, Math.abs(Math.sqrt(second) - Math.sqrt(best)) / 0.05)
      image.data[at + 3] = Math.round(28 + (70 * near) + (60 * edge))
    }
  }
  mapCtx.putImageData(image, 0, 0)

  ctx.save()
  // Smoothed: the fade is continuous, and blocky steps in it would read as structure that is not
  // there. The boundaries stay legible through the edge term above rather than through hard pixels.
  ctx.imageSmoothingEnabled = true
  ctx.drawImage(map, layout.offX, layout.offY, layout.dispW, layout.dispH)
  ctx.restore()
}

function hueToRgb(hue: number): [number, number, number] {
  // Fixed saturation and lightness; only the hue distinguishes zones, so they read as one family.
  const s = 0.75
  const l = 0.55
  const c = (1 - Math.abs((2 * l) - 1)) * s
  const x = c * (1 - Math.abs(((hue / 60) % 2) - 1))
  const m = l - (c / 2)
  const [r, g, b] =
    hue < 60 ? [c, x, 0] :
    hue < 120 ? [x, c, 0] :
    hue < 180 ? [0, c, x] :
    hue < 240 ? [0, x, c] :
    hue < 300 ? [x, 0, c] : [c, 0, x]
  return [
    Math.round((r + m) * 255),
    Math.round((g + m) * 255),
    Math.round((b + m) * 255),
  ]
}
