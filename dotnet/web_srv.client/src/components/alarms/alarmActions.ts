import type { MapAlarm, MapMarker } from '../../dto/mapDtos'

/**
 * The alarms that belong to what the operator selected. A camera shows every alarm of its device —
 * its own and its sensors' — because the circle on the map is the camera while the condition may
 * stand on one of its parts. A part shows only its own, and any other node only what stands on it.
 * A replica answers with its owner's alarms.
 */
export function alarmsForNode(alarms: MapAlarm[], node: MapMarker): MapAlarm[] {
  const canonicalId = node.ownerId ?? node.id
  return alarms.filter((alarm) =>
    node.nodeKind === 'camera'
      ? !!node.cameraId && alarm.cameraId === node.cameraId
      : alarm.objectId === canonicalId,
  )
}

/** The `alarms.blocked.*` key saying why the action is unavailable, or null when it is available. */
export function ackBlockedKey(alarm: MapAlarm): string | null {
  if (alarm.ackBlockedReason === 'closed') return 'alarms.blocked.closed'
  if (alarm.ackBlockedReason === 'no_right') return 'alarms.blocked.noRight'
  return null
}

/** A live condition on an armed node may be acknowledged, but only with a comment. */
export function acknowledgeNeedsComment(alarm: MapAlarm): boolean {
  return !!alarm.ackRequiresComment
}

/**
 * Whether the close action is offered. A returned alarm is closed by saying whether it was real;
 * a part the device no longer reports cannot clear by itself, and an operator closes it by hand.
 */
export function canCloseAlarm(alarm: MapAlarm): boolean {
  return alarm.status === 'returned' || !!alarm.deviceAbsent
}

/** A close of a live condition — only an absent part has one — needs its comment. */
export function closeNeedsComment(alarm: MapAlarm): boolean {
  return !!alarm.deviceAbsent && !!alarm.conditionActive
}
