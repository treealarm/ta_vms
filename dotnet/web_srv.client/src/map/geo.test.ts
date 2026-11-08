import { describe, expect, it } from 'vitest'
import type { CameraPlacementDto } from '../dto/dtos'
import type { MapFigure } from '../dto/mapDtos'
import { figureAtPlacement } from './geo'

const placement = {
  cameraId: 'cam-1', spaceId: 's-1', isReference: true,
  lat: 38.9928120, lon: -76.9378925, heightM: 2.8,
} as CameraPlacementDto

const at = (id: string, cameraId: string, lat: number, lon: number): MapFigure => ({
  id, cameraId, geometry: { type: 'Point', coord: [{ lat, lon }] },
})

describe('figureAtPlacement', () => {
  it('finds the replica standing exactly on the placement', () => {
    const figure = at('placed', 'cam-1', placement.lat, placement.lon)
    expect(figureAtPlacement([figure], placement)?.id).toBe('placed')
  })

  it('leaves another replica of the same camera alone', () => {
    // The demo's readings diagram is a replica of the same camera, a few metres away. It is a node
    // of its own on the canvas, not the picture the camera's marker stands for.
    const placed = at('placed', 'cam-1', placement.lat, placement.lon)
    const readings = at('readings', 'cam-1', 38.9928060, -76.9379290)
    expect(figureAtPlacement([readings, placed], placement)?.id).toBe('placed')
    expect(figureAtPlacement([readings], placement)).toBeUndefined()
  })

  it('ignores a figure of another camera or without a point', () => {
    expect(figureAtPlacement(
      [at('other', 'cam-2', placement.lat, placement.lon), { id: 'bare', cameraId: 'cam-1' }],
      placement,
    )).toBeUndefined()
  })
})
