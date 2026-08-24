import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'

/**
 * The camera another page asked this one to open, consumed once.
 *
 * Selection in these containers is local state, not a route, so arriving with a camera in mind
 * needs a query parameter — and that parameter has to be dropped as soon as it is read. Left in
 * place it would start lying the moment the operator clicks a different camera, and a reload would
 * then yank the selection back to whatever the link said.
 *
 * The value is captured on the first render and never changes afterwards, so consumers can pass it
 * straight to useMasterDetailSelection without it moving under them.
 */
export function useCameraDeepLink(): { cameraId: string | null; tab: string | null } {
  const [params, setParams] = useSearchParams()
  const [captured] = useState(() => ({ cameraId: params.get('camera'), tab: params.get('tab') }))

  useEffect(() => {
    if (!captured.cameraId && !captured.tab) return
    setParams((prev) => {
      const next = new URLSearchParams(prev)
      next.delete('camera')
      next.delete('tab')
      return next
      // replace: arriving here is not a history entry of its own, and Back should leave the page
      // rather than re-apply the link.
    }, { replace: true })
  }, [captured, setParams])

  return captured
}
