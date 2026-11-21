import type { ArmSource } from '../../dto/dtos'
import { guardInheritedColor } from '../../theme/ntTheme'

export type GuardSurface = 'device' | 'container'

export type GuardInput = {
  armed: boolean
  source: ArmSource | '' | null
  /** Excluded from an armed zone rather than simply off. */
  bypassed?: boolean
  /** When the exclusion ends; null or absent for an indefinite one. */
  bypassUntil?: string | null
  surface: GuardSurface
}

export type GuardState = {
  /** i18n key of the state caption; the timed ones take a `time` interpolation. */
  labelKey: string
  color: string
  /** The caption carries a deadline the component formats. */
  timed: boolean
  /** The node is excluded from an armed zone. */
  bypassed: boolean
  /** The device can be handed back to its zone here; a container has its `Inherit` button. */
  canReturnToZone: boolean
}

/**
 * One reading of `armed`/`source`/`bypassed` for every surface. The order matters: an explicit
 * exclusion is not "off guard", a deadline outlives the state it was set with, and only then does
 * the ordinary device/container vocabulary apply.
 */
export function guardState(input: GuardInput): GuardState {
  const bypassed = input.bypassed === true
  const timed = Boolean(input.bypassUntil)
  const inherited = input.source === 'inherited' || input.source === 'space'

  const labelKey = bypassed
    ? (timed ? 'control.guardBypassedUntil' : 'control.guardBypassed')
    : (!input.armed && timed)
      ? 'control.guardOffUntil'
      : input.surface === 'container'
        ? (input.armed
            ? (inherited ? 'control.guardOnInherited' : 'control.guardOn')
            : (inherited ? 'control.guardOffInherited' : 'control.guardOff'))
        : input.source === 'space'
          ? (input.armed ? 'onvifSettings.deviceIoGuardOnZone' : 'onvifSettings.deviceIoGuardOffZone')
          : input.source === 'camera'
            ? (input.armed ? 'onvifSettings.deviceIoGuardOnCamera' : 'onvifSettings.deviceIoGuardOffCamera')
            : (input.armed ? 'onvifSettings.deviceIoGuardOn' : 'onvifSettings.deviceIoGuardOff')

  const color = bypassed
    ? 'warning.main'
    : !input.armed
      ? 'text.secondary'
      : inherited
        ? guardInheritedColor
        : 'success.main'

  return {
    labelKey,
    color,
    timed,
    bypassed,
    canReturnToZone: input.surface === 'device' && bypassed,
  }
}
