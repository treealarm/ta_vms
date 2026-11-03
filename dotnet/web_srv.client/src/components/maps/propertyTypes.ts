/**
 * The value shapes a map node's abstract properties can take.
 *
 * These are the `visual_type` strings the server stores; the empty string is plain text, and it is
 * also what a property with no type at all means. Square's action-parameter types -- password,
 * coordinates, IP range, credential and enum lists, snapshot, map -- went with the actions
 * themselves and are not carried over, so a stored one reads as text here.
 */
export const PROPERTY_TYPES = ['', '__clr', '__datetime', '__double', '__int'] as const

export type PropertyType = (typeof PROPERTY_TYPES)[number]

/** The i18n key naming each type in the editor's dropdown. */
export const PROPERTY_TYPE_LABELS: Record<PropertyType, string> = {
  '': 'maps.editor.typeText',
  __clr: 'maps.editor.typeColor',
  __datetime: 'maps.editor.typeDateTime',
  __double: 'maps.editor.typeNumber',
  __int: 'maps.editor.typeInteger',
}

/** A type the editor no longer offers reads as plain text rather than disappearing. */
export function normalizeType(type?: string | null): PropertyType {
  return PROPERTY_TYPES.find((known) => known === type) ?? ''
}

/**
 * The value in the spelling its type should be stored in: numbers in one invariant form, instants
 * as ISO 8601 UTC. A value that does not parse is kept exactly as typed -- the same rule the
 * server applies to event properties, so a half-typed value is never lost.
 */
export function normalizeValue(type: string | undefined, raw: string): string {
  switch (normalizeType(type)) {
    case '__double': {
      const number = Number(raw.trim().replace(',', '.'))
      return raw.trim() !== '' && Number.isFinite(number) ? String(number) : raw
    }
    case '__int': {
      const number = Number(raw.trim().replace(',', '.'))
      return raw.trim() !== '' && Number.isInteger(number) ? String(number) : raw
    }
    case '__datetime': {
      const iso = fromLocalInput(raw)
      return iso === '' ? raw : iso
    }
    default:
      return raw
  }
}

/**
 * An ISO instant as the value of a `datetime-local` input, in the viewer's own time zone.
 * An empty or unparseable value shows as an empty input rather than as an invented time.
 */
export function toLocalInput(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''

  const pad = (value: number) => String(value).padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`
    + `T${pad(date.getHours())}:${pad(date.getMinutes())}`
}

/** The `datetime-local` value back to the ISO 8601 UTC instant the server stores. */
export function fromLocalInput(local: string): string {
  if (local === '') return ''
  const date = new Date(local)
  return Number.isNaN(date.getTime()) ? '' : date.toISOString()
}
