import { toIsoUtc } from '../../dto/dtos'
import { DEFAULT_EVENT_COUNT } from '../../store/eventsSlice'
import type { EventsFilter } from '../../store/eventsSlice'

export type EventsFilterState = {
  /** The datetime-input values as the operator typed them, in local time. */
  start: string
  end: string
  /** `?node=` from a wall link: the page-wide subtree, which wins over a picked object. */
  subtree?: string | null
  /** A node picked in the journal's own field. */
  objectId?: string | null
  imagesOnly: boolean
  param0: string
  param1: string
}

/**
 * What Apply sends. One object, never both: a link's subtree already names the page, and a picked
 * node picked afterwards only matters where no subtree was given.
 */
export function eventsFilterFromState(state: EventsFilterState): EventsFilter {
  return {
    timeStart: toIsoUtc(state.start) ?? undefined,
    timeEnd: toIsoUtc(state.end) ?? undefined,
    objects: state.subtree ? [state.subtree] : state.objectId ? [state.objectId] : [],
    imagesOnly: state.imagesOnly,
    param0: state.param0,
    param1: state.param1,
    count: DEFAULT_EVENT_COUNT,
  }
}
