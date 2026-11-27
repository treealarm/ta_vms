import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'

/**
 * The camera, the cursor and the event a wall link asked the journal to open, consumed once.
 *
 * The journal's filter is local state, not a route, so a link has to bring its own parameters —
 * and they have to be dropped as soon as they are read. Left in place they would start lying the
 * moment the operator changes the filter, and a reload would yank the page back to whatever the
 * link said. `node` is not ours: it names a subtree the page keeps showing, so it stays.
 *
 * The values are captured on the first render and never change afterwards, so consumers can seed
 * their state from them without it moving under them.
 */
export function useJournalDeepLink(): {
  cameraId: string | null
  before: string | null
  eventUuid: string | null
} {
  const [params, setParams] = useSearchParams()
  const [captured] = useState(() => ({
    cameraId: params.get('cameraId'),
    before: params.get('before'),
    eventUuid: params.get('eventUuid'),
  }))

  useEffect(() => {
    if (!captured.cameraId && !captured.before && !captured.eventUuid) return
    setParams((prev) => {
      const next = new URLSearchParams(prev)
      next.delete('cameraId')
      next.delete('before')
      next.delete('eventUuid')
      return next
      // replace: arriving here is not a history entry of its own, and Back should leave the page
      // rather than re-apply the link.
    }, { replace: true })
  }, [captured, setParams])

  return captured
}
