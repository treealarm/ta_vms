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

/** A fault: the device stopped reporting a part. It is not the ALARM state and has its own action. */
export function isFault(alarm: MapAlarm): boolean {
  return alarm.source === 'fault'
}

/** The `alarms.blocked.*` key saying why acknowledge is unavailable, or null when it is. */
export function ackBlockedKey(alarm: MapAlarm): string | null {
  if (alarm.ackBlockedReason === 'closed') return 'alarms.blocked.closed'
  if (alarm.ackBlockedReason === 'no_right') return 'alarms.blocked.noRight'
  // The server's answer for a returned fault: acknowledging is not how it is settled.
  if (alarm.ackBlockedReason === 'reset_required') return 'alarms.blocked.resetRequired'
  return null
}

/** A live condition on an armed node may be acknowledged, but only with a comment. */
export function acknowledgeNeedsComment(alarm: MapAlarm): boolean {
  return !!alarm.ackRequiresComment
}

/**
 * A fault whose part reports again is reset, never acknowledged: the part coming back ends the
 * condition, and the operator's comment closes the record. Only a returned fault can be reset; an
 * active one is still a live absence.
 */
export function canResetFault(alarm: MapAlarm): boolean {
  return isFault(alarm) && alarm.status === 'returned' && !alarm.conditionActive && !!alarm.canAck
}

/** The `alarms.*` key saying why the reset is unavailable, or null when it is available. */
export function resetBlockedKey(alarm: MapAlarm): string | null {
  if (alarm.status === 'closed') return 'alarms.blocked.closed'
  if (!alarm.canAck) return 'alarms.blocked.noRight'
  if (alarm.conditionActive) return 'alarms.faultStillPresent'
  return null
}

/**
 * Whether the ordinary close is offered. A returned condition alarm is closed by saying whether it
 * was real; a part the device no longer reports cannot clear by itself, and an operator closes it
 * by hand. A fault has no ordinary close at all: its only way out is `reset`.
 */
export function canCloseAlarm(alarm: MapAlarm): boolean {
  if (isFault(alarm)) return false
  return alarm.status === 'returned' || !!alarm.deviceAbsent
}

/** A close of a live condition — only an absent part has one — needs its comment; so does a reset. */
export function closeNeedsComment(alarm: MapAlarm): boolean {
  if (isFault(alarm)) return true
  return !!alarm.deviceAbsent && !!alarm.conditionActive
}
