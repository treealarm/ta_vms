import type { MapAlarmState, MapFigure, MapObjectState, MapStateDescription } from '../dto/mapDtos'
import { ALARM_COLOR, FAULT_COLOR } from './cameraStatus'

// One red and one amber for the whole map: a marker's ring and a shape's fill must agree.
export { ALARM_COLOR, FAULT_COLOR } from './cameraStatus'

export const DEFAULT_NODE_COLOR = '#90a4ae'
/** The state name the alarm adapter writes; the panel shows one alarm chip, not two. */
export const ALARM_STATE = 'ALARM'
/** Reachable but showing nothing: a state beside the connection one, not instead of it. */
export const NO_VIDEO_STATE = 'NO_VIDEO'

export type NodeStyle = {
  color: string
  /** True when the colour comes from the node or one of its descendants alarming. */
  alarming: boolean
  text?: string
}

/**
 * How a node is painted (§4.8): red when it or a descendant is alarmed, amber when it is a part its
 * device no longer reports, otherwise the colour of its first state, otherwise its own visual
 * colour. Presence is read from `present`, never from a fault alarm: the amber mark stays while the
 * operator has not reset the fault and even when no alarm row exists at all. An ALARM state wins,
 * because a live condition is the more urgent fact; the fault is then still visible as a chip.
 */
export function nodeStyle(
  figure: Pick<MapFigure, 'visual'> | undefined,
  state: MapObjectState | undefined,
  alarm: MapAlarmState | undefined,
  descriptions: Record<string, MapStateDescription>,
  present?: boolean,
): NodeStyle {
  const text = figure?.visual?.text || undefined
  const alarming = !!(alarm?.alarm || (alarm?.childrenAlarms ?? 0) > 0)
  if (alarming) return { color: ALARM_COLOR, alarming: true, text }

  if (present === false) return { color: FAULT_COLOR, alarming: false, text }

  // NO_VIDEO travels beside CONNECTED/DISCONNECTED, and the stored order of the set is not
  // guaranteed: the amber is picked by name, never as `states[0]`.
  if (state?.states?.includes(NO_VIDEO_STATE)) {
    const color = descriptions[NO_VIDEO_STATE]?.stateColor || FAULT_COLOR
    return { color, alarming: false, text }
  }

  const first = state?.states?.[0]
  const stateColor = first ? descriptions[first]?.stateColor : undefined
  if (stateColor) return { color: stateColor, alarming: false, text }

  return { color: figure?.visual?.color || DEFAULT_NODE_COLOR, alarming: false, text }
}
