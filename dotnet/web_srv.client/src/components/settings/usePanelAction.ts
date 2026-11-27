import { useCallback, useRef, useState } from 'react'
import { errorText } from '../../api/requestJson'

export type PanelAction = {
  busy: boolean
  /** The last failed operation, ready for the panel's toolbar. */
  error: string | null
  clearError: () => void
  /**
   * Runs one panel operation. Resolves true on success; on failure the message is kept for the
   * toolbar and false is returned — callers never have to catch, and navigation guards can use
   * the boolean directly.
   */
  run: (action: () => Promise<void>) => Promise<boolean>
}

/**
 * The busy/error state of the actions a settings detail pane performs — save, apply, delete.
 *
 * The error belongs to the panel that ran the action rather than to a page-wide channel: it is
 * shown beside the buttons, disappears when the next action starts, and is dismissed by the user.
 * A second call while one is in flight returns false instead of racing the first one.
 */
export function usePanelAction(): PanelAction {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const inFlight = useRef(false)

  const clearError = useCallback(() => setError(null), [])

  const run = useCallback(async (action: () => Promise<void>): Promise<boolean> => {
    if (inFlight.current) return false
    inFlight.current = true
    setBusy(true)
    setError(null)
    try {
      await action()
      return true
    } catch (e) {
      setError(errorText(e))
      return false
    } finally {
      inFlight.current = false
      setBusy(false)
    }
  }, [])

  return { busy, error, clearError, run }
}
