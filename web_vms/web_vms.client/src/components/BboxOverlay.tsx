import { useCallback, useEffect, useRef, type RefObject } from 'react'
import { useLiveDetections } from '../hooks/useLiveDetections'

interface Props {
  videoRef: RefObject<HTMLVideoElement | null>
  cameraId: string
}

// Deterministic, well-spread color per track (golden-angle hue rotation). A track and its child
// face/plate share trackId, so they intentionally get the same color.
function trackColor(trackId: number): string {
  // Golden-angle hue rotation spreads adjacent track ids far apart on the color wheel.
  const hue = (trackId * 137.508) % 360
  return `hsl(${hue}, 85%, 55%)`
}

const KIND_LABEL: Record<string, string> = {
  person: 'person',
  face: 'face',
  vehicle: 'vehicle',
  license_plate: 'plate',
  animal: 'animal',
}

// Compact form of the re-id object_id for the overlay: ephemeral ids look like "obj-<guid>" (show a
// short stable slice so a reappearance of the same object reads the same), enrolled ids like
// "person:john" / "plate:A123BC" are already short and shown as-is.
function shortObjectId(id: string): string {
  return id.startsWith('obj-') ? `obj ${id.slice(4, 10)}` : id
}

const FONT_PX = 11
const PAD = 3
const LINE = FONT_PX + 2

/**
 * Draws analytics bounding boxes over the live <video> on a <canvas>. Canvas (not SVG) is
 * deliberate: it updates several times a second without any React reconciliation or text layout, so
 * the overlay stays cheap on the main thread and never makes the live video stutter. The detection
 * hook lives here (not in CameraStream) so frequent detection updates re-render only this component,
 * leaving the <video> element's subtree untouched.
 *
 * Reads the video's intrinsic size vs. its rendered size to undo objectFit:contain letterboxing so
 * normalized (0..1) bboxes land on the actual image, not the black bars.
 */
export default function BboxOverlay({ videoRef, cameraId }: Props) {
  const detections = useLiveDetections(cameraId)
  const canvasRef = useRef<HTMLCanvasElement>(null)
  const detectionsRef = useRef(detections)

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

    const vW = video.videoWidth
    const vH = video.videoHeight
    if (vW === 0 || vH === 0 || elW === 0 || elH === 0) return

    // objectFit: contain — the image is scaled uniformly and centered, leaving letterbox bars.
    const scale = Math.min(elW / vW, elH / vH)
    const dispW = vW * scale
    const dispH = vH * scale
    const offX = (elW - dispW) / 2
    const offY = (elH - dispH) / 2

    ctx.font = `${FONT_PX}px monospace`
    ctx.textBaseline = 'top'

    for (const d of detectionsRef.current) {
      const x = offX + d.bboxX * dispW
      const y = offY + d.bboxY * dispH
      const w = d.bboxWidth * dispW
      const h = d.bboxHeight * dispH
      const color = trackColor(d.trackId)

      ctx.globalAlpha = d.opacity
      ctx.strokeStyle = color
      ctx.lineWidth = 2
      // Boxes the camera reported itself are dashed, so an operator can tell at a glance which
      // ones come from the CV detector and which from the camera's own analytics. Both keep the
      // per-track color, since a dashed and a solid box on the same object should still read as
      // related rather than as two unrelated colors.
      ctx.setLineDash(d.source === 'onvif' ? [6, 4] : [])
      ctx.strokeRect(x, y, w, h)
      ctx.setLineDash([])

      const main = [
        // 'unknown' means the producer did not say what the object is — most often a camera that
        // tracks motion without classifying it. Printing the word would read as a failure, so the
        // label just starts with the track id instead.
        d.kind === 'unknown' ? null : KIND_LABEL[d.kind] ?? d.kind,
        // The producer's own, narrower word ('Truck' under vehicle) when it has one — this is
        // where a camera's free-form ONVIF class survives the coarse kind enum.
        d.label ? `(${d.label})` : null,
        d.source === 'onvif' ? 'cam' : null,
        `#${d.trackId}`,
        `${Math.round(d.confidence * 100)}%`,
        d.objectId ? shortObjectId(d.objectId) : null,
        d.plateText,
      ]
        .filter(Boolean)
        .join(' ')
      const time = new Date(d.detectedAt).toLocaleTimeString()

      const boxW = Math.max(ctx.measureText(main).width, ctx.measureText(time).width) + PAD * 2
      const boxH = LINE * 2 + PAD * 2
      // Prefer the label above the box; if it would clip off the top, place it just inside.
      const labelY = y - boxH >= 0 ? y - boxH : y

      ctx.fillStyle = 'rgba(0,0,0,0.6)'
      ctx.fillRect(x, labelY, boxW, boxH)
      ctx.fillStyle = color
      ctx.fillText(main, x + PAD, labelY + PAD)
      ctx.fillStyle = '#ddd'
      ctx.fillText(time, x + PAD, labelY + PAD + LINE)
    }
    ctx.globalAlpha = 1
  }, [videoRef])

  // Redraw when detections change (also keep the ref fresh for resize-driven redraws).
  useEffect(() => {
    detectionsRef.current = detections
    draw()
  }, [detections, draw])

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
