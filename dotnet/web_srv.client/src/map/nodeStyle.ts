import type { MapAlarmState, MapFigure, MapObjectState, MapStateDescription } from '../dto/mapDtos'

export const ALARM_COLOR = '#ff1744'
export const DEFAULT_NODE_COLOR = '#90a4ae'

export type NodeStyle = {
  color: string
  /** True when the colour comes from the node or one of its descendants alarming. */
  alarming: boolean
  text?: string
}

/**
 * How a node is painted (§4.8): red when it or a descendant is alarmed, otherwise the colour of
 * its first state, otherwise its own visual colour. Square let `__color` override the state; the
 * typed visual field and this rule put the state first.
 */
export function nodeStyle(
  figure: Pick<MapFigure, 'visual'> | undefined,
  state: MapObjectState | undefined,
  alarm: MapAlarmState | undefined,
  descriptions: Record<string, MapStateDescription>,
): NodeStyle {
  const text = figure?.visual?.text || undefined
  const alarming = !!(alarm?.alarm || (alarm?.childrenAlarms ?? 0) > 0)
  if (alarming) return { color: ALARM_COLOR, alarming: true, text }

  const first = state?.states?.[0]
  const stateColor = first ? descriptions[first]?.stateColor : undefined
  if (stateColor) return { color: stateColor, alarming: false, text }

  return { color: figure?.visual?.color || DEFAULT_NODE_COLOR, alarming: false, text }
}
