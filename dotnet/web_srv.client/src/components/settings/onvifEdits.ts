import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react'

/**
 * The unsaved edits of the ONVIF camera cards.
 *
 * Each card on the ONVIF Cfg page keeps its configuration in local state until its own Save
 * button is pressed, and both the inner tab strip and the camera list on the left unmount it — so
 * leaving without saving would drop the edit silently. Every card registers here; the panel asks
 * the registry to save them in order before it lets the operator leave, and stops at the first
 * refusal.
 *
 * Only saved configuration registers. A field that feeds an immediate command — a new PTZ
 * preset's name, the "set time" box — is not an unsaved edit and is deliberately left out.
 */

/**
 * What an apply reported. The message travels with the result rather than being read back from
 * the card: the card's own error state only lands on the next render, and the panel would show
 * the section without its reason.
 */
export type OnvifEditOutcome = { ok: boolean; error: string | null }

export type OnvifEditHandle = {
  /** Whether the card holds changes its own Save button would send. */
  isDirty: () => boolean
  /** Saves when dirty; a refused result keeps the operator on the card. */
  apply: () => Promise<OnvifEditOutcome>
}

export type OnvifEditsApi = {
  /** Registers a card; returns the cleanup that removes it again. */
  register: (key: string, section: string, handle: OnvifEditHandle) => () => void
  /** Re-reads every card's dirty flag — call it whenever a card's own state changes. */
  refreshDirty: () => void
}

/** What failed, and in which section, when a save-on-leave was refused. */
export type OnvifEditFailure = { section: string; error: string | null }

export const OnvifEditsContext = createContext<OnvifEditsApi | null>(null)

/**
 * The registry and the dirty flag of the ONVIF Cfg panel. The panel owns them (it is the one the
 * route's imperative handle belongs to) and hands the API to the cards through the context.
 */
export function useOnvifEdits(): {
  api: OnvifEditsApi
  dirty: boolean
  /** Saves every dirty card in the order they sit on the page; the first refusal stops it. */
  applyAll: () => Promise<OnvifEditFailure | null>
} {
  const entries = useRef(new Map<string, { section: string; handle: OnvifEditHandle }>())
  const [dirty, setDirty] = useState(false)

  const refreshDirty = useCallback(() => {
    const next = [...entries.current.values()].some((entry) => entry.handle.isDirty())
    setDirty((current) => (current === next ? current : next))
  }, [])

  const register = useCallback(
    (key: string, section: string, handle: OnvifEditHandle) => {
      entries.current.set(key, { section, handle })
      refreshDirty()
      return () => {
        // A re-registration for the same key replaces the handle: only the latest may remove it.
        if (entries.current.get(key)?.handle === handle) entries.current.delete(key)
        refreshDirty()
      }
    },
    [refreshDirty],
  )

  const applyAll = useCallback(async (): Promise<OnvifEditFailure | null> => {
    // Insertion order is the order the cards sit on the page, so the operator is shown the first
    // refusal where it happened rather than somewhere further down.
    for (const { section, handle } of entries.current.values()) {
      if (!handle.isDirty()) continue
      const outcome = await handle.apply()
      if (outcome.ok) continue
      return { section, error: outcome.error }
    }
    return null
  }, [])

  const api = useMemo(() => ({ register, refreshDirty }), [register, refreshDirty])

  return { api, dirty, applyAll }
}

/**
 * Registers one card's save-on-leave contract. Outside the panel (the same card drawn elsewhere)
 * there is no registry, and nothing is guarded here — the card's own Save button still works.
 */
export function useOnvifEdit(entry: {
  /** Stable across re-renders: camera plus section plus anything that tells two cards apart. */
  key: string
  /** The tab the card belongs to, translated — the toolbar names it when the save fails. */
  section: string
  dirty: boolean
  apply: () => Promise<OnvifEditOutcome>
}): void {
  const api = useContext(OnvifEditsContext)
  // The handle is registered once per key and reads the current render's closures through a ref,
  // so a re-render does not churn the registry.
  const latest = useRef(entry)
  useEffect(() => {
    latest.current = entry
  })

  useEffect(() => {
    if (!api) return
    return api.register(entry.key, entry.section, {
      isDirty: () => latest.current.dirty,
      apply: () => latest.current.apply(),
    })
  }, [api, entry.key, entry.section])

  useEffect(() => {
    api?.refreshDirty()
  }, [api, entry.dirty])
}
