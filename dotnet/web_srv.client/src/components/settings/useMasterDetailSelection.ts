import { useEffect, useImperativeHandle, useRef, useState } from 'react'
import type { ApplyIfDirtyHandle } from './useDirtyGuard'

// Discriminated selection: a real item id can never collide with create mode
// (a plain 'new' string sentinel would clash with user-typed ids like "new").
type Selection = { kind: 'new' } | { kind: 'id'; id: string } | null

/**
 * Shared state machine for master-detail settings containers: current
 * selection, dirty-guarded switching (auto-apply before leaving a form,
 * blocked when the save fails), and applyIfDirty forwarding from the
 * container's imperative handle to the mounted detail form.
 */
export function useMasterDetailSelection<T>(
  ref: React.Ref<ApplyIfDirtyHandle> | undefined,
  items: T[],
  getId: (item: T) => string,
  /**
   * Item to select once, when it first appears in `items` — for arriving from another page with a
   * camera already in mind. Applied a single time: lists load asynchronously, so this has to wait
   * for the item rather than fire on mount, and after that the operator's own clicks own the
   * selection. An id that never shows up simply never applies.
   */
  initialId?: string | null,
) {
  const [selection, setSelection] = useState<Selection>(null)
  const initialApplied = useRef(false)

  useEffect(() => {
    if (initialApplied.current || !initialId) return
    if (!items.some((item) => getId(item) === initialId)) return
    initialApplied.current = true
    setSelection({ kind: 'id', id: initialId })
  }, [initialId, items, getId])

  const detailRef = useRef<ApplyIfDirtyHandle>(null)
  // Serializes guarded switches so a rapid second click cannot start a
  // concurrent applyIfDirty save for the same dirty form
  const switching = useRef(false)

  /** Apply the mounted form if dirty; false = save failed, stay put. */
  const guard = async (): Promise<boolean> =>
    detailRef.current ? detailRef.current.applyIfDirty() : true

  const setSelectionSafe = async (sel: Selection) => {
    if (switching.current) return
    switching.current = true
    try {
      if (!(await guard())) return
      setSelection(sel)
    } finally {
      switching.current = false
    }
  }

  useImperativeHandle(ref, () => ({
    applyIfDirty: () => detailRef.current ? detailRef.current.applyIfDirty() : Promise.resolve(true),
  }))

  const isNew = selection?.kind === 'new'
  const selected = selection?.kind === 'id'
    ? items.find((item) => getId(item) === selection.id) ?? null
    : null

  return {
    isNew,
    /** The selected item, or null when nothing/new/vanished is selected. */
    selected,
    detailRef,
    /** Key for the keyed detail form; '::new' cannot collide with a real id. */
    detailKey: isNew ? '::new' : selected ? getId(selected) : '',
    /** Guarded selection changes (user clicks). */
    select: (id: string) => void setSelectionSafe({ kind: 'id', id }),
    selectNew: () => void setSelectionSafe({ kind: 'new' }),
    /** Unguarded setters for onSaved/onRemoved callbacks of the form itself. */
    reset: () => setSelection(null),
    setId: (id: string) => setSelection({ kind: 'id', id }),
    guard,
  }
}
