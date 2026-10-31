import { useMemo, useRef } from 'react'
import type { ReactNode } from 'react'
import { PendingEditsContext, type PendingEditsValue } from './pendingEditsContext'
import type { ApplyIfDirtyHandle } from './useDirtyGuard'

/** Holds the pending-edit handle of the settings container on screen; see pendingEditsContext. */
export function PendingEditsProvider({ children }: { children: ReactNode }) {
  const handle = useRef<ApplyIfDirtyHandle | null>(null)
  // Built once: the provider must not re-render its consumers when its own children change.
  const value = useMemo<PendingEditsValue>(() => ({
    register: (h) => { handle.current = h },
    applyPending: () => (handle.current ? handle.current.applyIfDirty() : Promise.resolve(true)),
  }), [])

  return (
    <PendingEditsContext.Provider value={value}>{children}</PendingEditsContext.Provider>
  )
}
