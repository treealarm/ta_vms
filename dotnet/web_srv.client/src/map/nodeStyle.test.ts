import { describe, expect, it } from 'vitest'
import type { MapStateDescription } from '../dto/mapDtos'
import { ALARM_COLOR, DEFAULT_NODE_COLOR, FAULT_COLOR, nodeStyle } from './nodeStyle'

const OPEN = 'OPEN'
const OPEN_COLOR = '#00c853'

const descriptions: Record<string, MapStateDescription> = {
  [OPEN]: { state: OPEN, stateColor: OPEN_COLOR },
}

const alarming = { alarm: true }
const quiet = { alarm: false }

describe('nodeStyle', () => {
  it('paints a part its device does not report amber', () => {
    const style = nodeStyle(undefined, undefined, quiet, descriptions, false)

    expect(style.color).toBe(FAULT_COLOR)
    expect(style.alarming).toBe(false)
  })

  it('keeps a live condition alarm red over the fault', () => {
    const style = nodeStyle(undefined, undefined, alarming, descriptions, false)

    expect(style.color).toBe(ALARM_COLOR)
    expect(style.alarming).toBe(true)
  })

  it('paints a present part from its state', () => {
    const style = nodeStyle(undefined, { states: [OPEN] }, quiet, descriptions, true)

    expect(style.color).toBe(OPEN_COLOR)
  })

  it('leaves a node without presence to its own colour', () => {
    const style = nodeStyle(undefined, undefined, quiet, descriptions, undefined)

    expect(style.color).toBe(DEFAULT_NODE_COLOR)
  })
})
