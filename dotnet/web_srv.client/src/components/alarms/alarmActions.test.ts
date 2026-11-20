import { describe, expect, it } from 'vitest'
import type { MapAlarm, MapMarker } from '../../dto/mapDtos'
import {
  acknowledgeNeedsComment, ackBlockedKey, alarmsForNode, canCloseAlarm, canResetFault,
  closeNeedsComment, isFault, resetBlockedKey,
} from './alarmActions'

// The fixtures these tests share.
const CAMERA_ID = 'cam-1'
const CAMERA_NODE = 'camera-node'
const PART_NODE = 'part-node'
const REPLICA = 'replica'
const SPACE = 'space'
const SENSOR_KEY = 'sensor:1'

const camera: MapMarker = { id: CAMERA_NODE, cameraId: CAMERA_ID, nodeKind: 'camera' }
const part: MapMarker = { id: PART_NODE, cameraId: CAMERA_ID, nodeKind: 'part' }
const replica: MapMarker = { id: REPLICA, ownerId: PART_NODE, cameraId: CAMERA_ID, nodeKind: 'part' }
const space: MapMarker = { id: SPACE, nodeKind: 'space' }

const alarm = (objectId: string, extra: Partial<MapAlarm> = {}): MapAlarm => ({
  id: `${objectId}:${SENSOR_KEY}`,
  objectId,
  cameraId: CAMERA_ID,
  source: 'sensor',
  status: 'active',
  conditionActive: true,
  canAck: true,
  ...extra,
})

const sensorAlarm = alarm(PART_NODE)
const cameraAlarm = alarm(CAMERA_NODE, { source: 'retention' })
const otherCameraAlarm = alarm('another-part', { cameraId: 'cam-2' })
const all = [sensorAlarm, cameraAlarm, otherCameraAlarm]

const fault = (extra: Partial<MapAlarm> = {}): MapAlarm =>
  alarm(PART_NODE, { source: 'fault', ...extra })

describe('alarmsForNode', () => {
  it('a camera shows its own and its sensors alarms', () => {
    expect(alarmsForNode(all, camera)).toEqual([sensorAlarm, cameraAlarm])
  })

  it('a part shows only its own alarm', () => {
    expect(alarmsForNode(all, part)).toEqual([sensorAlarm])
  })

  it('a replica answers with its owner alarms', () => {
    expect(alarmsForNode(all, replica)).toEqual([sensorAlarm])
  })

  it('a space has no device alarms', () => {
    expect(alarmsForNode(all, space)).toEqual([])
  })
})

describe('ackBlockedKey', () => {
  it('names a closed alarm', () => {
    expect(ackBlockedKey(alarm(PART_NODE, { status: 'closed', ackBlockedReason: 'closed' })))
      .toBe('alarms.blocked.closed')
  })

  it('names a missing right', () => {
    expect(ackBlockedKey(alarm(PART_NODE, { canAck: false, ackBlockedReason: 'no_right' })))
      .toBe('alarms.blocked.noRight')
  })

  it('is empty when the action is available', () => {
    expect(ackBlockedKey(sensorAlarm)).toBeNull()
  })

  it('says a returned fault is reset rather than acknowledged', () => {
    expect(ackBlockedKey(fault({ status: 'returned', conditionActive: false, ackBlockedReason: 'reset_required' })))
      .toBe('alarms.blocked.resetRequired')
  })
})

describe('isFault', () => {
  it('separates a fault from the condition it stands beside', () => {
    expect(isFault(fault())).toBe(true)
    expect(isFault(sensorAlarm)).toBe(false)
  })
})

describe('canResetFault', () => {
  it('offers the reset to a returned fault the caller may acknowledge', () => {
    expect(canResetFault(fault({ status: 'returned', conditionActive: false }))).toBe(true)
  })

  it('does not offer it while the part is still absent', () => {
    expect(canResetFault(fault({ conditionActive: true }))).toBe(false)
  })

  it('does not offer it without the right', () => {
    expect(canResetFault(fault({ status: 'returned', conditionActive: false, canAck: false }))).toBe(false)
  })

  it('does not offer it to a returned condition alarm', () => {
    expect(canResetFault(alarm(PART_NODE, { status: 'returned', conditionActive: false }))).toBe(false)
  })
})

describe('resetBlockedKey', () => {
  it('is empty when the reset is available', () => {
    expect(resetBlockedKey(fault({ status: 'returned', conditionActive: false }))).toBeNull()
  })

  it('names the part that has not come back', () => {
    expect(resetBlockedKey(fault())).toBe('alarms.faultStillPresent')
  })

  it('names a missing right', () => {
    expect(resetBlockedKey(fault({ status: 'returned', conditionActive: false, canAck: false })))
      .toBe('alarms.blocked.noRight')
  })

  it('names a closed row', () => {
    expect(resetBlockedKey(fault({ status: 'closed', conditionActive: false })))
      .toBe('alarms.blocked.closed')
  })
})

describe('acknowledgeNeedsComment', () => {
  it('is set for a live condition on an armed node', () => {
    expect(acknowledgeNeedsComment(alarm(PART_NODE, { ackRequiresComment: true }))).toBe(true)
  })

  it('is unset otherwise', () => {
    expect(acknowledgeNeedsComment(sensorAlarm)).toBe(false)
  })
})

describe('canCloseAlarm', () => {
  it('offers a returned alarm', () => {
    expect(canCloseAlarm(alarm(PART_NODE, { status: 'returned', conditionActive: false }))).toBe(true)
  })

  it('offers an absent parts live alarm', () => {
    expect(canCloseAlarm(alarm(PART_NODE, { deviceAbsent: true }))).toBe(true)
  })

  it('does not offer a present parts live alarm', () => {
    expect(canCloseAlarm(sensorAlarm)).toBe(false)
  })

  it('never offers the ordinary close to a fault, which is reset instead', () => {
    expect(canCloseAlarm(fault({ status: 'returned', conditionActive: false, deviceAbsent: true })))
      .toBe(false)
  })
})

describe('closeNeedsComment', () => {
  it('is required only for an absent part whose condition is live', () => {
    expect(closeNeedsComment(alarm(PART_NODE, { deviceAbsent: true }))).toBe(true)
    expect(closeNeedsComment(alarm(PART_NODE, { status: 'returned', conditionActive: false }))).toBe(false)
  })

  it('is always required for a fault reset', () => {
    expect(closeNeedsComment(fault({ status: 'returned', conditionActive: false }))).toBe(true)
  })
})
