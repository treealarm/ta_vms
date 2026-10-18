import { createContext, useContext, useEffect, useRef } from 'react'
import type { ReactNode } from 'react'
import type { ApplyIfDirtyHandle } from './useDirtyGuard'

/**
 * One place to ask "is a settings form holding unsaved edits, and can they be saved now?".
 *
 * Settings forms already auto-apply when the selection or the tab changes, but that guard is wired
 * inside each container, so it only covers movement within it. Leaving the section entirely — the
 * Admin/Operator switch — is plain routing: the container unmounts and the edit goes with it,
 * silently. The behaviour then depended on which way the operator happened to leave, which is not
 * something they can be expected to know.
 *
 * The container registers its handle here; whoever navigates away from it calls applyPending first.
 */
type PendingEditsValue = {
  register: (handle: ApplyIfDirtyHandle | null) => void
  /** Saves whatever is pending. False means the save failed and the caller should stay put. */
  applyPending: () => Promise<boolean>
}

const PendingEditsContext = createContext<PendingEditsValue>({
  register: () => {},
  applyPending: () => Promise.resolve(true),
})

export function PendingEditsProvider({ children }: { children: ReactNode }) {
  const handle = useRef<ApplyIfDirtyHandle | null>(null)
  const value = useRef<PendingEditsValue>({
    register: (h) => { handle.current = h },
    applyPending: () => handle.current ? handle.current.applyIfDirty() : Promise.resolve(true),
  })
  return (
    <PendingEditsContext.Provider value={value.current}>{children}</PendingEditsContext.Provider>
  )
}

/** For a settings container: publishes its form's apply handle while it is mounted. */
export function useRegisterPendingEdits(handle: React.RefObject<ApplyIfDirtyHandle | null>) {
  const { register } = useContext(PendingEditsContext)
  useEffect(() => {
    register({ applyIfDirty: () => handle.current?.applyIfDirty() ?? Promise.resolve(true) })
    return () => register(null)
  }, [register, handle])
}

/** For anything that navigates away from a settings container. */
export function usePendingEdits() {
  return useContext(PendingEditsContext).applyPending
}
