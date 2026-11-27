import type { EpisodeDto } from '../../dto/dtos'
import type { MapEvent } from '../../dto/mapDtos'
import { eventProp } from '../../store/eventsSlice'

/** The dictionary the journal speaks: the protocol's name, read as one kind. */
export type EventKindName = 'sensor' | 'relay' | 'analytics' | 'alarm' | 'arming' | 'other'

/** What the archive card can say about the selected row. */
export type ArchiveSelectionState = 'none' | 'pending' | 'noArchive' | 'ready'

export const isAlarmEvent = (event: MapEvent): boolean =>
  event.eventName === 'alarm' || (event.eventName ?? '').startsWith('alarm_')

export function eventKind(event: MapEvent): EventKindName {
  if (isAlarmEvent(event)) return 'alarm'
  switch (event.eventName) {
    case 'sensor': return 'sensor'
    case 'relay': return 'relay'
    case 'analytics': return 'analytics'
    case 'arming': return 'arming'
    default: return 'other'
  }
}

const NAME_KEYS: Partial<Record<EventKindName, string>> = {
  sensor: 'events.name.sensor',
  relay: 'events.name.relay',
  analytics: 'events.name.analytics',
  alarm: 'events.name.alarm',
  arming: 'events.name.arming',
}

/** The event's name as a translation key; undefined for a name the journal does not know. */
export function eventNameKey(name?: string): string | undefined {
  return NAME_KEYS[eventKind({ eventName: name })]
}

const PARAM_LABEL_KEYS: Record<EventKindName, (string | undefined)[]> = {
  sensor: ['events.param.contact'],
  relay: ['events.param.state'],
  analytics: ['events.param.class'],
  alarm: ['events.streamName', 'events.trigger'],
  arming: ['events.param.actor', 'events.param.mode'],
  other: [],
}

/** What param 0 or 1 means for this kind of event, as a translation key. */
export function paramLabelKey(kind: EventKindName, index: 0 | 1): string | undefined {
  return PARAM_LABEL_KEYS[kind][index]
}

const VALUE_KEYS: Partial<Record<EventKindName, Record<string, string>>> = {
  sensor: { open: 'events.value.open', closed: 'events.value.closed' },
  relay: { active: 'events.value.active', inactive: 'events.value.inactive' },
  arming: {
    any: 'events.value.any',
    armed: 'events.value.armed',
    disarmed: 'events.value.disarmed',
    inherit: 'events.value.inherit',
  },
}

/**
 * The value as a translation key. An analytics class is always a key, because the class list is
 * the analytics module's: an unknown class falls back to its raw name at the call site.
 */
export function paramValueKey(kind: EventKindName, raw?: string): string | undefined {
  if (!raw) return undefined
  if (kind === 'analytics') return `analytics.class.${raw}`
  return VALUE_KEYS[kind]?.[raw]
}

/** One part of a row's summary: a translation key (with its fallback) or text to show as it is. */
export type SummaryToken = { key: string; fallback?: string } | { text: string }

/** 'Sensor · Open', 'Alarm · main · Motion', or the raw protocol line for an unknown event. */
export function rowSummary(event: MapEvent): SummaryToken[] {
  const kind = eventKind(event)
  const tokens: SummaryToken[] = []

  const nameKey = eventNameKey(event.eventName)
  if (nameKey) tokens.push({ key: nameKey })
  else if (event.eventName) tokens.push({ text: event.eventName })

  for (const raw of [event.param0, event.param1]) {
    if (!raw) continue
    const valueKey = paramValueKey(kind, raw)
    if (valueKey) tokens.push({ key: valueKey, fallback: raw })
    else tokens.push({ text: raw })
  }

  return tokens
}

/** One row's archive episode, or null when the event has no camera behind it. */
export function episodeFromEvent(event: MapEvent, cameraId: string | undefined): EpisodeDto | null {
  if (!event.objectId || !cameraId) return null

  // param0/param1 are the stream and the trigger only on an alarm; on every other kind they are
  // the contact, the class, the actor and the like, and an archive card must not look for a
  // stream that shares their name.
  const alarm = isAlarmEvent(event)

  return {
    eventUuid: event.id ?? '',
    eventId: event.id ?? '',
    objectId: event.objectId,
    cameraId,
    streamName: alarm ? event.param0 || undefined : undefined,
    alarmStartTs: event.timestamp ?? new Date(0).toISOString(),
    alarmEndTs: eventProp(event, 'alarm_end_ts'),
    trigger: alarm ? event.param1 || undefined : undefined,
  }
}

export type FieldFormat = 'text' | 'time' | 'percent'

export type HumanField = {
  /** Translation key of the label. */
  labelKey: string
  value: string
  /** Set when the value itself translates; the raw value is the fallback. */
  valueKey?: string
  format?: FieldFormat
}

/**
 * The card's readable half: the event in the reader's words, its parameters under their meaning,
 * and the one fact each kind carries beyond them. Everything else is technical.
 */
export function humanProps(event: MapEvent): HumanField[] {
  const kind = eventKind(event)
  const fields: HumanField[] = []

  fields.push({
    labelKey: 'events.eventName',
    value: event.eventName ?? '',
    valueKey: eventNameKey(event.eventName),
  })

  const params: (string | undefined)[] = [event.param0, event.param1]
  params.forEach((raw, index) => {
    if (!raw) return
    fields.push({
      labelKey: paramLabelKey(kind, index as 0 | 1) ?? (index === 0 ? 'events.param0' : 'events.param1'),
      value: raw,
      valueKey: paramValueKey(kind, raw),
    })
  })

  if (kind === 'analytics') {
    const confidence = eventProp(event, 'confidence')
    if (confidence) fields.push({ labelKey: 'events.prop.confidence', value: confidence, format: 'percent' })
  }
  if (kind === 'alarm') {
    const end = eventProp(event, 'alarm_end_ts')
    if (end) fields.push({ labelKey: 'events.prop.alarmEndTs', value: end, format: 'time' })
  }
  if (kind === 'arming') {
    const until = eventProp(event, 'until')
    if (until) fields.push({ labelKey: 'events.prop.until', value: until, format: 'time' })
  }

  if (event.eventPriority !== undefined) {
    fields.push({ labelKey: 'events.priority', value: String(event.eventPriority) })
  }
  if (event.timestamp) {
    fields.push({ labelKey: 'events.time', value: event.timestamp, format: 'time' })
  }

  return fields
}

const PROP_LABEL_KEYS: Record<string, string> = {
  camera_id: 'events.prop.cameraId',
  part_kind: 'events.prop.partKind',
  token: 'events.prop.token',
  polarity_known: 'events.prop.polarityKnown',
  observed_at: 'events.prop.observedAt',
  kind: 'events.prop.class',
  confidence: 'events.prop.confidence',
  detection_id: 'events.prop.detectionId',
  reid_object_id: 'events.prop.objectId',
  alarm_id: 'events.prop.alarmId',
  alarm_end_ts: 'events.prop.alarmEndTs',
  until: 'events.prop.until',
  expired: 'events.prop.expired',
  contact: 'events.param.contact',
  active: 'events.param.state',
}

export type TechnicalField = { name: string; labelKey?: string; value: string }

/**
 * Everything the card does not say in words: the identifiers, the protocol's own property names
 * and any key this build has never heard of. Nothing is dropped — an unknown key keeps its name.
 */
export function technicalProps(event: MapEvent): TechnicalField[] {
  const fields: TechnicalField[] = []
  if (event.id) fields.push({ name: 'event_id', labelKey: 'events.eventId', value: event.id })
  if (event.objectId) fields.push({ name: 'object_id', labelKey: 'events.objectId', value: event.objectId })

  for (const prop of event.extraProps ?? []) {
    if (prop.visualType === 'image_fs') continue
    const name = prop.propName ?? ''
    fields.push({ name, labelKey: PROP_LABEL_KEYS[name], value: prop.strVal ?? '' })
  }

  return fields
}
