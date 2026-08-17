import { useCallback, useEffect, useRef, type RefObject } from 'react'
import { drawBoxes, letterboxLayout, type DrawableBox } from './bboxDraw'

interface Props {
  videoRef: RefObject<HTMLVideoElement | null>
  boxes: DrawableBox[]
}

/**
 * Draws bounding boxes over a <video> on a <canvas>.
 *
 * Canvas (not SVG) is deliberate: it updates several times a second without any React reconciliation
 * or text layout, so the overlay stays cheap on the main thread and never makes the video stutter.
 * For the same reason the boxes are held in a ref — a resize-driven redraw must not need a render.
 */
export default function BboxCanvas({ videoRef, boxes }: Props) {
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const boxesRef = useRef(boxes)

  const draw = useCallback(() => {
    const canvas = canvasRef.current
    const video = videoRef.current
    if (!canvas || !video) return
    const ctx = canvas.getContext('2d')
    if (!ctx) return

    const elW = video.clientWidth
    const elH = video.clientHeight
    const dpr = window.devicePixelRatio || 1

    // Resize the backing store to the element's device pixels only when it actually changed —
    // assigning canvas.width every frame would clear and thrash it needlessly.
    const bw = Math.round(elW * dpr)
    const bh = Math.round(elH * dpr)
    if (canvas.width !== bw || canvas.height !== bh) {
      canvas.width = bw
      canvas.height = bh
    }
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0)
    ctx.clearRect(0, 0, elW, elH)

    const layout = letterboxLayout(video)
    if (!layout) return

    drawBoxes(ctx, layout, boxesRef.current)
  }, [videoRef])

  // Redraw when the boxes change (also keep the ref fresh for resize-driven redraws).
  useEffect(() => {
    boxesRef.current = boxes
    draw()
  }, [boxes, draw])

  // The video element resizes with the monitor grid, and its intrinsic size is unknown until
  // metadata loads — redraw on both without going through React state.
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

  return (
    <canvas
      ref={canvasRef}
      style={{
        position: 'absolute',
        inset: 0,
        width: '100%',
        height: '100%',
        pointerEvents: 'none',
        zIndex: 3,
      }}
    />
  )
}
