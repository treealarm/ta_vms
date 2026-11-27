import type { MapEvent, MapMarker } from '../../dto/mapDtos'
import { toLocalDatetimeInputValue } from '../../dto/dtos'
import { emptyEventsFilter, type EventsFilter } from '../../store/eventsSlice'

/** How many older pages the journal appends while looking for the event a link named. */
export const MAX_DEEP_LINK_PAGES = 5

export type JournalLink = {
  /** The node the map named; it wins over the camera's own nodes. */
  node: string | null
  /** The keyset cursor the wall sent: the page starts just after this instant. */
  before: string | null
  /** The map nodes that carry the linked camera; empty when it has none. */
  cameraNodeIds: readonly string[]
}

/**
 * The filter a wall link opens the journal with: the node the map named, or the nodes of the
 * camera the operator was watching, bounded by the cursor the wall sent.
 *
 * A camera with no nodes leaves the objects empty on purpose — the page then says that only the
 * time range was applied, rather than passing the whole journal off as the camera's events.
 */
export function journalLinkFilter(link: JournalLink): EventsFilter {
  const cursor = link.before && !Number.isNaN(Date.parse(link.before)) ? link.before : undefined

  return {
    ...emptyEventsFilter,
    timeEnd: cursor,
    objects: link.node ? [link.node] : [...link.cameraNodeIds],
  }
}

/** The cursor as the period's "To" field shows it: local time, empty when it is not a time. */
export function localCursorValue(before: string | null): string {
  if (!before) return ''

  const date = new Date(before)
  return Number.isNaN(date.getTime()) ? '' : toLocalDatetimeInputValue(date)
}

/**
 * The nodes that carry a camera: its device node and its parts. The search also matches names and
 * part tokens, so a camera id can come back beside nodes that merely mention it.
 */
export function cameraNodesOf(markers: readonly MapMarker[], cameraId: string): string[] {
  return markers
    .filter((marker) => marker.cameraId === cameraId)
    .map((marker) => marker.id)
}

export type EventLookup = 'pending' | 'found' | 'notFound'

/**
 * Where the journal stands looking for the event a link named: it is in the rows, it may still be
 * on an older page (until the page cap), or it is not in the range the filter asks for.
 */
export function lookupLinkedEvent(
  rows: readonly MapEvent[],
  eventUuid: string,
  hasMore: boolean,
  pagesFetched: number,
): EventLookup {
  if (rows.some((event) => event.id === eventUuid)) return 'found'
  if (hasMore && pagesFetched < MAX_DEEP_LINK_PAGES) return 'pending'
  return 'notFound'
}
