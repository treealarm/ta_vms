/**
 * A plan picture uploaded from the browser. The server stores and serves raster images only; an
 * SVG is drawn into a canvas here and sent as PNG, so no SVG document is ever served from the API.
 */

export type PreparedPlanImage = {
  file: File
  widthPx: number
  heightPx: number
  /** True when an SVG source was converted, for the caller's own wording. */
  fromSvg: boolean
}

export type PlanImageProblem = 'unreadable' | 'tooLarge' | 'unsupportedUnit'

export class PlanImageError extends Error {
  readonly problem: PlanImageProblem

  constructor(problem: PlanImageProblem = 'unreadable') {
    super(problem)
    this.problem = problem
  }
}

/** The longest side a plan may be uploaded at, and the total pixel budget. */
const MAX_SIDE = 4096
const MAX_PIXELS = 16_000_000

/** CSS pixel sizes of the units an SVG may use, at 96 dpi. */
const UNIT_PX: Record<string, number> = {
  '': 1,
  px: 1,
  pt: 96 / 72,
  pc: 96 / 6,
  in: 96,
  cm: 96 / 2.54,
  mm: 96 / 25.4,
}

type ParsedLength = { value: number } | { unit: true } | null

function parseLength(raw: string | null): ParsedLength {
  if (!raw) return null
  const match = /^([0-9]*\.?[0-9]+)\s*([a-z%]*)$/i.exec(raw.trim())
  if (!match) return null
  const value = Number(match[1])
  const unit = match[2].toLowerCase()
  if (!Number.isFinite(value)) return null
  const scale = UNIT_PX[unit]
  // A percentage has no intrinsic size; an unknown unit must not be silently read as pixels.
  return scale === undefined ? { unit: true } : { value: value * scale }
}

function tagAttribute(tag: string, name: string): string | null {
  const match = new RegExp(`${name}\\s*=\\s*["']([^"']+)["']`, 'i').exec(tag)
  return match?.[1]?.trim() ?? null
}

/**
 * The intrinsic size of an SVG document, in CSS pixels. Physical units are converted at 96 dpi;
 * a document without a usable size has none and is refused rather than guessed.
 */
export function svgIntrinsicSize(svgText: string): { width: number; height: number } | null {
  const root = /<svg\b[^>]*>/i.exec(svgText)?.[0]
  if (!root) return null

  const width = parseLength(tagAttribute(root, 'width'))
  const height = parseLength(tagAttribute(root, 'height'))
  if (width && 'value' in width && height && 'value' in height && width.value > 0 && height.value > 0)
    return { width: width.value, height: height.value }

  const viewBox = tagAttribute(root, 'viewBox')
  if (viewBox) {
    const parts = viewBox.split(/[\s,]+/).map(Number)
    if (parts.length === 4 && parts.every(Number.isFinite) && parts[2] > 0 && parts[3] > 0)
      return { width: parts[2], height: parts[3] }
  }

  if ((width && 'unit' in width) || (height && 'unit' in height))
    throw new PlanImageError('unsupportedUnit')
  return null
}

function loadImage(url: string): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const image = new Image()
    image.onload = () => resolve(image)
    image.onerror = () => reject(new PlanImageError('unreadable'))
    image.src = url
  })
}

function toPng(canvas: HTMLCanvasElement): Promise<Blob> {
  return new Promise((resolve, reject) => {
    canvas.toBlob(
      (blob) => (blob ? resolve(blob) : reject(new PlanImageError('unreadable'))),
      'image/png')
  })
}

function pngName(name: string): string {
  const base = name.replace(/\.[^.]+$/, '') || 'plan'
  return `${base}.png`
}

/**
 * Prepares a plan picture for upload: a raster file is measured and passed through; an SVG is
 * sized, capped and drawn to PNG. Nothing is inserted into the document, so no SVG script runs.
 */
export async function preparePlanImage(file: File, maxBytes: number): Promise<PreparedPlanImage> {
  const isSvg = file.type === 'image/svg+xml' || /\.svg$/i.test(file.name)
  if (!isSvg) {
    if (file.size > maxBytes) throw new PlanImageError('tooLarge')
    try {
      const bitmap = await createImageBitmap(file)
      const size = { width: bitmap.width, height: bitmap.height }
      bitmap.close()
      if (size.width <= 0 || size.height <= 0) throw new PlanImageError('unreadable')
      return { file, widthPx: size.width, heightPx: size.height, fromSvg: false }
    } catch (error) {
      throw error instanceof PlanImageError ? error : new PlanImageError('unreadable')
    }
  }

  const text = await file.text().catch(() => {
    throw new PlanImageError('unreadable')
  })
  const intrinsic = svgIntrinsicSize(text)
  if (!intrinsic) throw new PlanImageError('unreadable')

  const scale = Math.min(
    1,
    MAX_SIDE / Math.max(intrinsic.width, intrinsic.height),
    Math.sqrt(MAX_PIXELS / (intrinsic.width * intrinsic.height)),
  )
  const widthPx = Math.max(1, Math.round(intrinsic.width * scale))
  const heightPx = Math.max(1, Math.round(intrinsic.height * scale))

  const url = URL.createObjectURL(new Blob([text], { type: 'image/svg+xml' }))
  try {
    const image = await loadImage(url)
    const canvas = document.createElement('canvas')
    canvas.width = widthPx
    canvas.height = heightPx
    const context = canvas.getContext('2d')
    if (!context) throw new PlanImageError('unreadable')
    context.drawImage(image, 0, 0, widthPx, heightPx)
    const blob = await toPng(canvas)
    if (blob.size > maxBytes) throw new PlanImageError('tooLarge')
    return {
      file: new File([blob], pngName(file.name), { type: 'image/png' }),
      widthPx,
      heightPx,
      fromSvg: true,
    }
  } catch (error) {
    // A drawing that pulls in an external resource taints the canvas; either way it is unreadable
    // here rather than a picture that silently failed to render.
    throw error instanceof PlanImageError ? error : new PlanImageError('unreadable')
  } finally {
    URL.revokeObjectURL(url)
  }
}
