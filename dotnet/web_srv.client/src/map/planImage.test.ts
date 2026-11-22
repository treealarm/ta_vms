import { describe, expect, it } from 'vitest'
import { PlanImageError, svgIntrinsicSize } from './planImage'

const svg = (attributes: string) => `<svg ${attributes}><rect width="1" height="1"/></svg>`

describe('svgIntrinsicSize', () => {
  it('reads width and height attributes', () => {
    expect(svgIntrinsicSize(svg('width="1745" height="1000"'))).toEqual({
      width: 1745,
      height: 1000,
    })
  })

  it('accepts px explicitly', () => {
    expect(svgIntrinsicSize(svg('width="800px" height="600px"'))).toEqual({
      width: 800,
      height: 600,
    })
  })

  it('converts physical units at 96 dpi instead of reading them as pixels', () => {
    expect(svgIntrinsicSize(svg('width="25.4mm" height="2.54cm"'))).toEqual({
      width: 96,
      height: 96,
    })
    expect(svgIntrinsicSize(svg('width="72pt" height="1in"'))).toEqual({
      width: 96,
      height: 96,
    })
  })

  it('falls back to the viewBox when the attributes are absent', () => {
    expect(svgIntrinsicSize(svg('viewBox="0 0 1745 1000"'))).toEqual({
      width: 1745,
      height: 1000,
    })
  })

  it('falls back to the viewBox when an attribute uses a percentage', () => {
    expect(svgIntrinsicSize(svg('width="100%" height="100%" viewBox="0 0 640 400"'))).toEqual({
      width: 640,
      height: 400,
    })
  })

  it('refuses an unknown unit rather than treating it as pixels', () => {
    expect(() => svgIntrinsicSize(svg('width="10furlongs" height="4furlongs"')))
      .toThrow(PlanImageError)
  })

  it('has no size without attributes or a usable viewBox', () => {
    expect(svgIntrinsicSize(svg(''))).toBeNull()
    expect(svgIntrinsicSize(svg('viewBox="0 0 0 0"'))).toBeNull()
    expect(svgIntrinsicSize('not an svg')).toBeNull()
  })
})
