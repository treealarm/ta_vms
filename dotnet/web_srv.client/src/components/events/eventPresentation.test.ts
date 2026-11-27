import { describe, expect, it } from 'vitest'

import type { MapEvent } from '../../dto/mapDtos'
import {
  episodeFromEvent, eventKind, eventNameKey, humanProps, paramLabelKey, paramValueKey, rowSummary,
  technicalProps,
} from './eventPresentation'

const prop = (propName: string, strVal: string) => ({ propName, strVal })
const event = (over: Partial<MapEvent>): MapEvent => ({
  timestamp: '2026-11-27T10:00:00.000Z',
  ...over,
})

describe('event kind and names', () => {
  it('reads a contact as a sensor', () => {
    expect(eventKind(event({ eventName: 'sensor' }))).toBe('sensor')
    expect(eventNameKey('sensor')).toBe('events.name.sensor')
  })

  it('reads an alarm and its lifecycle events as one kind', () => {
    expect(eventKind(event({ eventName: 'alarm' }))).toBe('alarm')
    expect(eventKind(event({ eventName: 'alarm_closed' }))).toBe('alarm')
    expect(eventNameKey('alarm_closed')).toBe('events.name.alarm')
  })

  it('keeps an unknown event raw', () => {
    expect(eventKind(event({ eventName: 'mystery' }))).toBe('other')
    expect(eventNameKey('mystery')).toBeUndefined()
  })
})

describe('parameters', () => {
  it('names a contact and its states', () => {
    expect(paramLabelKey('sensor', 0)).toBe('events.param.contact')
    expect(paramValueKey('sensor', 'open')).toBe('events.value.open')
    expect(paramValueKey('sensor', 'closed')).toBe('events.value.closed')
  })

  it('names a relay state', () => {
    expect(paramLabelKey('relay', 0)).toBe('events.param.state')
    expect(paramValueKey('relay', 'active')).toBe('events.value.active')
  })

  it('translates an analytics class and nothing else', () => {
    expect(paramValueKey('analytics', 'person')).toBe('analytics.class.person')
    expect(paramValueKey('analytics', undefined)).toBeUndefined()
  })

  it('names the stream and trigger of an alarm', () => {
    expect(paramLabelKey('alarm', 0)).toBe('events.streamName')
    expect(paramLabelKey('alarm', 1)).toBe('events.trigger')
  })

  it('names an arming actor and mode', () => {
    expect(paramLabelKey('arming', 0)).toBe('events.param.actor')
    expect(paramLabelKey('arming', 1)).toBe('events.param.mode')
    expect(paramValueKey('arming', 'armed')).toBe('events.value.armed')
    expect(paramValueKey('arming', 'inherit')).toBe('events.value.inherit')
  })
})

describe('rowSummary', () => {
  it('reads a sensor row as Sensor · Open', () => {
    expect(rowSummary(event({ eventName: 'sensor', param0: 'open' }))).toEqual([
      { key: 'events.name.sensor' },
      { key: 'events.value.open', fallback: 'open' },
    ])
  })

  it('reads a relay row as Relay · Active', () => {
    expect(rowSummary(event({ eventName: 'relay', param0: 'active' }))).toEqual([
      { key: 'events.name.relay' },
      { key: 'events.value.active', fallback: 'active' },
    ])
  })

  it('reads an alarm row with its stream and trigger', () => {
    expect(rowSummary(event({ eventName: 'alarm', param0: 'main', param1: 'motion' }))).toEqual([
      { key: 'events.name.alarm' },
      { text: 'main' },
      { text: 'motion' },
    ])
  })

  it('reads an arming row with its actor and translated mode', () => {
    expect(rowSummary(event({ eventName: 'arming', param0: 'admin', param1: 'armed' }))).toEqual([
      { key: 'events.name.arming' },
      { text: 'admin' },
      { key: 'events.value.armed', fallback: 'armed' },
    ])
  })

  it('keeps an unknown event entirely raw', () => {
    expect(rowSummary(event({ eventName: 'mystery', param0: 'x' }))).toEqual([
      { text: 'mystery' },
      { text: 'x' },
    ])
  })
})

describe('humanProps', () => {
  it('puts a contact under Contact and shows its state', () => {
    const fields = humanProps(event({ eventName: 'sensor', param0: 'closed' }))

    expect(fields[0]).toMatchObject({ labelKey: 'events.eventName', value: 'sensor', valueKey: 'events.name.sensor' })
    expect(fields[1]).toMatchObject({ labelKey: 'events.param.contact', value: 'closed', valueKey: 'events.value.closed' })
  })

  it('adds the confidence of an analytics detection', () => {
    const fields = humanProps(event({
      eventName: 'analytics',
      param0: 'person',
      extraProps: [prop('confidence', '0.91')],
    }))

    expect(fields).toContainEqual({ labelKey: 'events.prop.confidence', value: '0.91', format: 'percent' })
  })

  it('adds the end of an alarm', () => {
    const end = '2026-11-27T10:05:00.000Z'
    const fields = humanProps(event({
      eventName: 'alarm',
      param0: 'main',
      param1: 'motion',
      extraProps: [prop('alarm_end_ts', end)],
    }))

    expect(fields).toContainEqual({ labelKey: 'events.prop.alarmEndTs', value: end, format: 'time' })
  })

  it('adds the deadline of an arming change', () => {
    const until = '2026-11-27T12:00:00.000Z'
    const fields = humanProps(event({
      eventName: 'arming',
      param0: 'admin',
      param1: 'disarmed',
      extraProps: [prop('until', until)],
    }))

    expect(fields).toContainEqual({ labelKey: 'events.prop.until', value: until, format: 'time' })
  })

  it('falls back to Param 0 for an unknown event', () => {
    const fields = humanProps(event({ eventName: 'mystery', param0: 'x' }))

    expect(fields[1]).toMatchObject({ labelKey: 'events.param0', value: 'x' })
  })

  it('works on an event without extra props', () => {
    const fields = humanProps(event({ eventName: 'sensor', param0: 'open' }))

    expect(fields.length).toBeGreaterThan(0)
    expect(fields.every((field) => field.labelKey.length > 0)).toBe(true)
  })
})

describe('episodeFromEvent', () => {
  const objectId = 'object-1'
  const cameraId = 'camera-1'
  const alarmEnd = '2026-11-27T10:05:00.000Z'

  it('takes the stream and trigger of an alarm', () => {
    const episode = episodeFromEvent(event({
      id: 'event-1',
      objectId,
      eventName: 'alarm',
      param0: 'main',
      param1: 'motion',
      extraProps: [prop('alarm_end_ts', alarmEnd)],
    }), cameraId)

    expect(episode).toMatchObject({
      cameraId,
      objectId,
      streamName: 'main',
      trigger: 'motion',
      alarmEndTs: alarmEnd,
    })
  })

  it('does not pass a contact as a stream name', () => {
    const episode = episodeFromEvent(event({
      id: 'event-2',
      objectId,
      eventName: 'sensor',
      param0: 'open',
      param1: 'also-not-a-trigger',
    }), cameraId)

    expect(episode?.cameraId).toBe(cameraId)
    expect(episode?.streamName).toBeUndefined()
    expect(episode?.trigger).toBeUndefined()
  })

  it('is null when no camera stands behind the event', () => {
    expect(episodeFromEvent(event({ objectId }), undefined)).toBeNull()
    expect(episodeFromEvent(event({ eventName: 'sensor' }), cameraId)).toBeNull()
  })
})

describe('technicalProps', () => {
  it('carries the identifiers and every non-image property', () => {
    const fields = technicalProps(event({
      id: 'event-1',
      objectId: 'object-1',
      extraProps: [
        prop('camera_id', 'camera-1'),
        { propName: 'picture', strVal: 'file-1', visualType: 'image_fs' },
      ],
    }))

    expect(fields).toEqual([
      { name: 'event_id', labelKey: 'events.eventId', value: 'event-1' },
      { name: 'object_id', labelKey: 'events.objectId', value: 'object-1' },
      { name: 'camera_id', labelKey: 'events.prop.cameraId', value: 'camera-1' },
    ])
  })

  it('keeps an unknown key under its own name', () => {
    const fields = technicalProps(event({ extraProps: [prop('mystery', '42')] }))

    expect(fields).toEqual([{ name: 'mystery', labelKey: undefined, value: '42' }])
  })

  it('is empty for an event without properties', () => {
    expect(technicalProps(event({ eventName: 'sensor', param0: 'open' }))).toEqual([])
  })
})
