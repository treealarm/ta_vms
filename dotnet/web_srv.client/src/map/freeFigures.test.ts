import { describe, expect, it } from 'vitest'
import { freeFigureAt } from './freeFigures'

const at = { lat: 38.9929, lon: -76.9379 }

describe('freeFigureAt', () => {
  it('makes a caption a point node whose visual text is the caption', () => {
    const details = freeFigureAt('text', at, 'node-1', 'plan-1', 'Text 1', 'Label')

    expect(details.figure?.geometry).toEqual({ type: 'Point', coord: [at] })
    expect(details.visual.text).toBe('Label')
    expect(details.visual.image).toBeUndefined()
    expect(details.node.parentId).toBe('plan-1')
    expect(details.properties).toEqual([])
  })

  it('makes a picture a point node carrying its bytes and natural size', () => {
    const details = freeFigureAt(
      'picture', at, 'node-2', 'plan-1', 'Picture 1', '', { data: 'AAAA', width: 64, height: 48 })

    expect(details.figure?.geometry).toEqual({ type: 'Point', coord: [at] })
    expect(details.visual.text).toBeUndefined()
    expect(details.visual.image).toBe('AAAA')
    expect(details.visual.imageWidth).toBe(64)
    expect(details.visual.imageHeight).toBe(48)
  })
})
