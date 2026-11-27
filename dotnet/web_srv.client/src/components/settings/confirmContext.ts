import { createContext, useContext } from 'react'
import type { ReactNode } from 'react'

export type ConfirmOptions = {
  /** Names the object, e.g. "Delete camera «cam1»?" — not a generic "Are you sure?". */
  title: string
  /** What disappears together with it, in the operator's words. */
  message: ReactNode
  confirmLabel?: string
  cancelLabel?: string
  /** A red confirm button; the default, because the dialog guards destruction. */
  danger?: boolean
}

/**
 * Asks to confirm a destructive action. Resolves true only when the operator confirms.
 *
 * The provider lives once in `App`. Without it the answer is false — a missing provider must not
 * read as "confirmed".
 */
export const ConfirmContext = createContext<(options: ConfirmOptions) => Promise<boolean>>(
  () => Promise.resolve(false),
)

export function useConfirm() {
  return useContext(ConfirmContext)
}
