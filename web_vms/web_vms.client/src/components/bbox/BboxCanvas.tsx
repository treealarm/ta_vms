import { useCallback, useEffect, useRef, useState, type RefObject } from 'react'
import { Menu, MenuItem } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { drawBoxes, letterboxLayout, type DrawableBox } from './bboxDraw'

interface Props {
  videoRef: RefObject<HTMLVideoElement | null>
  boxes: DrawableBox[]
  onFindSimilar?: (box: DrawableBox) => void
  /** Point this camera's automatic tracking at the clicked object. Absent where it cannot. */
  onTrackObject?: (box: DrawableBox) => void
}

/**
 * Draws bounding boxes over a <video> on a <canvas>.
 *
 * Canvas (not SVG) is deliberate: it updates several times a second without any React reconciliation
 * or text layout, so the overlay stays cheap on the main thread and never makes the video stutter.
 * For the same reason the boxes are held in a ref — a resize-driven redraw must not need a render.
 */
export default function BboxCanvas({ videoRef, boxes, onFindSimilar, onTrackObject }: Props) {
  const { t } = useTranslation()
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const boxesRef = useRef(boxes)
  const [menu, setMenu] = useState<{ x: number; y: number; box: DrawableBox } | null>(null)

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

  useEffect(() => {
    const video = videoRef.current
    if (!video || (!onFindSimilar && !onTrackObject)) return

    const hitTest = (clientX: number, clientY: number): DrawableBox | null => {
      const rect = video.getBoundingClientRect()
      const x = clientX - rect.left
      const y = clientY - rect.top
      const layout = letterboxLayout(video)
      if (!layout) return null
      // Walk from topmost (last drawn) to bottom.
      for (let i = boxesRef.current.length - 1; i >= 0; i -= 1) {
        const box = boxesRef.current[i]
        const bx = layout.offX + box.bboxX * layout.dispW
        const by = layout.offY + box.bboxY * layout.dispH
        const bw = box.bboxWidth * layout.dispW
        const bh = box.bboxHeight * layout.dispH
        if (x >= bx && x <= bx + bw && y >= by && y <= by + bh) return box
      }
      return null
    }

    const onVideoClick = (event: MouseEvent) => {
      const box = hitTest(event.clientX, event.clientY)
      if (!box) return
      // Stop the monitor-slot focus handler / tile drag from seeing this click.
      event.preventDefault()
      event.stopPropagation()
      setMenu({ x: event.clientX, y: event.clientY, box })
    }

    video.addEventListener('click', onVideoClick)
    return () => video.removeEventListener('click', onVideoClick)
  }, [videoRef, onFindSimilar, onTrackObject])

  return (
    <>
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
      <Menu
        open={menu !== null}
        onClose={() => setMenu(null)}
        anchorReference="anchorPosition"
        anchorPosition={menu ? { top: menu.y, left: menu.x } : undefined}
      >
        {onTrackObject && (
          <MenuItem
            onClick={(event) => {
              event.preventDefault()
              event.stopPropagation()
              const box = menu?.box
              setMenu(null)
              if (!box) return
              onTrackObject(box)
            }}
          >
            {t('tracking.trackObject')}
          </MenuItem>
        )}
        {onFindSimilar && (
          <MenuItem
            onClick={(event) => {
              event.preventDefault()
              event.stopPropagation()
              const box = menu?.box
              setMenu(null)
              if (!box) return
              // Defer past MUI Menu/Modal teardown — otherwise React Router 7 can keep the
              // previous splat match (/operator/monitors) and the address bar never settles on
              // Object findings.
              queueMicrotask(() => onFindSimilar(box))
            }}
          >
            {t('forensic.findSimilar')}
          </MenuItem>
        )}
      </Menu>
    </>
  )
}
