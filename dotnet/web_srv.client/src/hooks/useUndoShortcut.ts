import { useEffect, useRef } from 'react'

/**
 * Ctrl+Z (⌘Z) runs `undo` while the component is mounted — except in a text field, where the
 * shortcut belongs to the text being typed.
 */
export function useUndoShortcut(undo: () => void) {
  const ref = useRef(undo)
  useEffect(() => { ref.current = undo }, [undo])
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey) || e.shiftKey || e.key.toLowerCase() !== 'z') return
      const el = e.target as HTMLElement | null
      if (el && (el.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(el.tagName))) return
      e.preventDefault()
      ref.current()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [])
}
