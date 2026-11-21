/**
 * A `datetime-local` value is a wall-clock reading in the browser's zone. The server stores an
 * absolute instant, so the conversion happens once, here: the browser resolves the local time
 * (including a DST gap or overlap) and the API only ever sees the resulting UTC instant.
 */
export function localDateTimeToIso(value: string): string | null {
  if (!value) return null
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? null : date.toISOString()
}

/** The deadline as the operator reads it, in the language the UI is in. */
export function formatUntil(iso: string, locale: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return iso
  return new Intl.DateTimeFormat(locale, {
    dateStyle: 'short',
    timeStyle: 'short',
  }).format(date)
}
