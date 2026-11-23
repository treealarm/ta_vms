import { describe, expect, it } from 'vitest'
import type { MapStateDescription } from '../dto/mapDtos'
import { ALARM_COLOR, DEFAULT_NODE_COLOR, FAULT_COLOR, NO_VIDEO_STATE, nodeStyle } from './nodeStyle'

const OPEN = 'OPEN'
const OPEN_COLOR = '#00c853'
const CONNECTED = 'CONNECTED'
const CONNECTED_COLOR = '#00ff00'
const NO_VIDEO_COLOR = '#ffc107'

const descriptions: Record<string, MapStateDescription> = {
  [OPEN]: { state: OPEN, stateColor: OPEN_COLOR },
  [CONNECTED]: { state: CONNECTED, stateColor: CONNECTED_COLOR },
  [NO_VIDEO_STATE]: { state: NO_VIDEO_STATE, stateColor: NO_VIDEO_COLOR },
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

  it('paints NO_VIDEO amber whatever position the connection state takes', () => {
    const connectedFirst = nodeStyle(
      undefined, { states: [CONNECTED, NO_VIDEO_STATE] }, quiet, descriptions, true)
    const noVideoFirst = nodeStyle(
      undefined, { states: [NO_VIDEO_STATE, CONNECTED] }, quiet, descriptions, true)

    expect(connectedFirst.color).toBe(NO_VIDEO_COLOR)
    expect(connectedFirst.alarming).toBe(false)
    expect(noVideoFirst.color).toBe(NO_VIDEO_COLOR)
  })

  it('keeps a live condition alarm red over NO_VIDEO', () => {
    const style = nodeStyle(
      undefined, { states: [CONNECTED, NO_VIDEO_STATE] }, alarming, descriptions, true)

    expect(style.color).toBe(ALARM_COLOR)
    expect(style.alarming).toBe(true)
  })

  it('keeps the fault amber over NO_VIDEO', () => {
    const style = nodeStyle(
      undefined, { states: [CONNECTED, NO_VIDEO_STATE] }, quiet, descriptions, false)

    expect(style.color).toBe(FAULT_COLOR)
  })

  it('paints a connected camera from its own state when no video is lost', () => {
    const style = nodeStyle(undefined, { states: [CONNECTED] }, quiet, descriptions, true)

    expect(style.color).toBe(CONNECTED_COLOR)
  })
})
