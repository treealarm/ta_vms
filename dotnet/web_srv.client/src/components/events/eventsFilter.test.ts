import { describe, expect, it } from 'vitest'

import { DEFAULT_EVENT_COUNT } from '../../store/eventsSlice'
import { eventsFilterFromState } from './eventsFilter'

const start = '2026-11-27T10:00'
const end = '2026-11-27T12:30'
const emptyState = { start: '', end: '', imagesOnly: false, param0: '', param1: '' }

describe('eventsFilterFromState', () => {
  it('turns the typed period into UTC', () => {
    const filter = eventsFilterFromState({ ...emptyState, start, end })

    expect(filter.timeStart).toBe(new Date(start).toISOString())
    expect(filter.timeEnd).toBe(new Date(end).toISOString())
  })

  it('asks for the picked node and everything under it', () => {
    const filter = eventsFilterFromState({ ...emptyState, start, objectId: 'object-1' })

    expect(filter.objects).toEqual(['object-1'])
  })

  it('lets the link subtree win over a picked node', () => {
    const filter = eventsFilterFromState({
      ...emptyState, start, subtree: 'subtree-1', objectId: 'object-1',
    })

    expect(filter.objects).toEqual(['subtree-1'])
  })

  it('keeps the time range when the camera has no nodes', () => {
    const filter = eventsFilterFromState({ ...emptyState, start, end })

    expect(filter.objects).toEqual([])
    expect(filter.timeStart).toBe(new Date(start).toISOString())
    expect(filter.timeEnd).toBe(new Date(end).toISOString())
  })

  it('sends an empty filter as the newest page', () => {
    const filter = eventsFilterFromState(emptyState)

    expect(filter).toEqual({
      timeStart: undefined,
      timeEnd: undefined,
      objects: [],
      imagesOnly: false,
      param0: '',
      param1: '',
      count: DEFAULT_EVENT_COUNT,
    })
  })

  it('passes the extra filters through', () => {
    const filter = eventsFilterFromState({
      ...emptyState, imagesOnly: true, param0: 'open', param1: 'main',
    })

    expect(filter).toMatchObject({ imagesOnly: true, param0: 'open', param1: 'main' })
  })
})
