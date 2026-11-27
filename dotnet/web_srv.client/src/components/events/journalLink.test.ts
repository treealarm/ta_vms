import { describe, expect, it } from 'vitest'
import type { MapEvent, MapMarker } from '../../dto/mapDtos'
import { cameraNodesOf, journalLinkFilter, lookupLinkedEvent, MAX_DEEP_LINK_PAGES } from './journalLink'

const linkedCamera = 'camera-1'
const cameraDeviceNode = 'camera-1-device'
const cameraSensorPart = 'camera-1-motion'
const otherCamera = 'camera-2'
const otherCameraNode = 'camera-2-device'
const linkedNode = 'ground-floor'
const cursor = '2026-11-27T10:00:00.000Z'
const linkedEvent = 'event-1'
const olderEvent = 'event-2'

const row = (id: string): MapEvent => ({ id, timestamp: cursor, objectId: linkedNode })

describe('the journal filter a wall link opens with', () => {
  it('starts the page at the cursor the wall sent', () => {
    const filter = journalLinkFilter({ node: null, before: cursor, cameraNodeIds: [] })
    expect(filter.timeEnd).toBe(cursor)
  })

  it('reads the camera through its own nodes', () => {
    const filter = journalLinkFilter({
      node: null, before: cursor, cameraNodeIds: [cameraDeviceNode],
    })
    expect(filter.objects).toEqual([cameraDeviceNode])
  })

  it('prefers the node the map named over the camera', () => {
    const filter = journalLinkFilter({
      node: linkedNode, before: cursor, cameraNodeIds: [cameraDeviceNode],
    })
    expect(filter.objects).toEqual([linkedNode])
  })

  it('leaves the objects empty for a camera the map does not carry', () => {
    const filter = journalLinkFilter({ node: null, before: cursor, cameraNodeIds: [] })
    expect(filter.objects).toEqual([])
    expect(filter.timeEnd).toBe(cursor)
  })

  it('ignores a cursor that is not a time', () => {
    const filter = journalLinkFilter({ node: null, before: 'yesterday', cameraNodeIds: [] })
    expect(filter.timeEnd).toBeUndefined()
  })
})

describe('the nodes that carry a camera', () => {
  const markers: MapMarker[] = [
    { id: cameraDeviceNode, cameraId: linkedCamera },
    { id: cameraSensorPart, cameraId: linkedCamera },
    { id: otherCameraNode, cameraId: otherCamera },
  ]

  it('keeps the device and its parts and drops everything else', () => {
    expect(cameraNodesOf(markers, linkedCamera)).toEqual([cameraDeviceNode, cameraSensorPart])
  })
})

describe('finding the event a wall link named', () => {
  it('is found when it is among the rows', () => {
    expect(lookupLinkedEvent([row(linkedEvent)], linkedEvent, true, 0)).toBe('found')
  })

  it('is found on a page appended while looking', () => {
    expect(lookupLinkedEvent([row(olderEvent), row(linkedEvent)], linkedEvent, true, 2)).toBe('found')
  })

  it('stays pending while the journal has older pages to try', () => {
    expect(lookupLinkedEvent([row(olderEvent)], linkedEvent, true, 0)).toBe('pending')
  })

  it('is not found once the journal runs out', () => {
    expect(lookupLinkedEvent([], linkedEvent, false, 0)).toBe('notFound')
  })

  it('stops after the page cap even when the journal has more', () => {
    expect(lookupLinkedEvent([], linkedEvent, true, MAX_DEEP_LINK_PAGES)).toBe('notFound')
  })
})
