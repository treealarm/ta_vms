import { describe, expect, it } from 'vitest'
import { visualImageUrl } from './visualImage'

// A visual row is bytes with no mime: a PNG icon has always worked, and an SVG one — a diagram's
// background used as an icon — used to be handed to the browser as a broken PNG.
const svg = btoa('<svg xmlns="http://www.w3.org/2000/svg" width="44" height="28"/>')
const png = btoa('\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR')

describe('visualImageUrl', () => {
  it('tells an SVG from a PNG by the bytes it starts with', () => {
    expect(visualImageUrl(svg)).toBe(`data:image/svg+xml;base64,${svg}`)
    expect(visualImageUrl(png)).toBe(`data:image/png;base64,${png}`)
  })

  it('passes a data URL through, and an absent picture stays absent', () => {
    expect(visualImageUrl('data:image/png;base64,AAAA')).toBe('data:image/png;base64,AAAA')
    expect(visualImageUrl(null)).toBe('')
    expect(visualImageUrl(undefined)).toBe('')
  })
})
