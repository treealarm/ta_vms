import { useEffect, useImperativeHandle } from 'react'

/** Uniform imperative handle exposed by every admin settings panel. */
export type ApplyIfDirtyHandle = { applyIfDirty: () => Promise<boolean> }

/**
 * Registers the applyIfDirty imperative handle and the beforeunload warning
 * for a settings detail form.
 *
 * `apply` must resolve true on success or when there is nothing to save;
 * false blocks the pending navigation (tab switch / list selection change).
 */
export function useDirtyGuard(
  ref: React.Ref<ApplyIfDirtyHandle> | undefined,
  dirty: boolean,
  apply: () => Promise<boolean>,
): void {
  useImperativeHandle(ref, () => ({
    applyIfDirty: () => (dirty ? apply() : Promise.resolve(true)),
  }), [dirty, apply])

  useEffect(() => {
    if (!dirty) return
    const handler = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = '' }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [dirty])
}
