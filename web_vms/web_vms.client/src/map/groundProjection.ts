import type { GeoPointDto, GroundMatrix } from '../dto/dtos'

/**
 * Where a point of a fixed camera's picture is on the ground, by the matrix its calibration hands
 * out; null beyond the horizon. Only the multiplication lives here: the fit is the server's.
 */
export function project(m: GroundMatrix, x: number, y: number): GeoPointDto | null {
  const w = m[6] * x + m[7] * y + m[8]
  if (!(w > 0)) return null
  return { lat: (m[0] * x + m[1] * y + m[2]) / w, lon: (m[3] * x + m[4] * y + m[5]) / w }
}

/** Detections whose box stands on the ground: the bottom of a face or a plate is not the floor. */
const ON_THE_GROUND = new Set(['person', 'vehicle', 'animal', 'unknown'])

type Box = { kind: string; bboxX: number; bboxY: number; bboxWidth: number; bboxHeight: number }

/** The point of a box that touches the ground — feet, wheels — or null for a box that does not stand. */
export function footOf(box: Box): { x: number; y: number } | null {
  if (!ON_THE_GROUND.has(box.kind)) return null
  return { x: box.bboxX + box.bboxWidth / 2, y: box.bboxY + box.bboxHeight }
}

