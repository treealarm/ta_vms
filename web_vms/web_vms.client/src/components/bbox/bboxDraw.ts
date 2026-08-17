import type { AnalyticsDetectionSource } from '../../dto/dtos'

/**
 * Drawing primitives shared by the live and archive bbox overlays.
 *
 * Everything here is deliberately free of any notion of *how fresh* a box is. The live overlay ages
 * boxes out on a TTL because it cannot know whether another one is coming; the archive knows both
 * the previous and the next sample and interpolates between them instead. Those are two different
 * models of time, and only their result — an opacity and a rectangle — belongs in common code.
 */

/** A box ready to be drawn: geometry already normalized, freshness already resolved. */
export type DrawableBox = {
  trackId: number
  kind: string
  confidence: number
  bboxX: number
  bboxY: number
  bboxWidth: number
  bboxHeight: number
  source: AnalyticsDetectionSource
  label?: string | null
  plateText?: string | null
  objectId?: string | null
  /** 0..1; the live overlay fades expiring boxes, the archive always passes 1. */
  opacity: number
  /** Wall-clock instant printed under the label. */
  timeMs: number
}

/** Where the video's image actually sits inside its element, once objectFit:contain is undone. */
export type LetterboxLayout = {
  offX: number
  offY: number
  dispW: number
  dispH: number
  elW: number
  elH: number
}

// Deterministic, well-spread color per track (golden-angle hue rotation). A track and its child
// face/plate share trackId, so they intentionally get the same color.
export function trackColor(trackId: number): string {
  // Golden-angle hue rotation spreads adjacent track ids far apart on the color wheel.
  const hue = (trackId * 137.508) % 360
  return `hsl(${hue}, 85%, 55%)`
}

export const KIND_LABEL: Record<string, string> = {
  person: 'person',
  face: 'face',
  vehicle: 'vehicle',
  license_plate: 'plate',
  animal: 'animal',
}

// Compact form of the re-id object_id for the overlay: ephemeral ids look like "obj-<guid>" (show a
// short stable slice so a reappearance of the same object reads the same), enrolled ids like
// "person:john" / "plate:A123BC" are already short and shown as-is.
export function shortObjectId(id: string): string {
  return id.startsWith('obj-') ? `obj ${id.slice(4, 10)}` : id
}

const FONT_PX = 11
const PAD = 3
const LINE = FONT_PX + 2

/**
 * Undoes objectFit:contain letterboxing, so normalized (0..1) bboxes land on the actual image
 * rather than on the black bars. Returns null while the video has no intrinsic size yet.
 */
export function letterboxLayout(video: HTMLVideoElement): LetterboxLayout | null {
  const elW = video.clientWidth
  const elH = video.clientHeight
  const vW = video.videoWidth
  const vH = video.videoHeight
  if (vW === 0 || vH === 0 || elW === 0 || elH === 0) return null

  const scale = Math.min(elW / vW, elH / vH)
  const dispW = vW * scale
  const dispH = vH * scale
  return { offX: (elW - dispW) / 2, offY: (elH - dispH) / 2, dispW, dispH, elW, elH }
}

/** Draws the boxes onto an already-cleared, already-scaled context. */
export function drawBoxes(ctx: CanvasRenderingContext2D, layout: LetterboxLayout, boxes: DrawableBox[]) {
  ctx.font = `${FONT_PX}px monospace`
  ctx.textBaseline = 'top'

  for (const d of boxes) {
    const x = layout.offX + d.bboxX * layout.dispW
    const y = layout.offY + d.bboxY * layout.dispH
    const w = d.bboxWidth * layout.dispW
    const h = d.bboxHeight * layout.dispH
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
    const time = new Date(d.timeMs).toLocaleTimeString()

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
}
