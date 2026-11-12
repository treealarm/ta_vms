import { useMemo, useState } from 'react'
import type { GeoPointDto } from '../../dto/dtos'
import { EditorStateContext, FRESH, type EditorState, type Selection, type Snapshot } from './editorState'

/**
 * The editing state of the space on screen, shared by the lists beside the map and the map itself.
 * They are different subtrees of the page -- the lists sit in the page's left column, the map and
 * its property tabs in the right -- so what is selected, which tool is active and which camera is
 * waiting to be placed cannot live in either one alone.
 *
 * It belongs to one space: opening another starts afresh, the way a keyed component would, but
 * without remounting the lists around it.
 */
export function EditorStateProvider({ spaceId, children }: { spaceId: string | null; children: React.ReactNode }) {
  const [snapshot, setSnapshot] = useState<Snapshot>(FRESH)
  const [forSpace, setForSpace] = useState(spaceId)
  // Reset while rendering rather than in an effect: a child's effect (selecting the camera a link
  // asked for) runs before a parent's, and a reset arriving after it would throw that away.
  if (forSpace !== spaceId) {
    setForSpace(spaceId)
    setSnapshot(FRESH)
  }

  const value = useMemo<EditorState>(() => {
    const patch = (p: Partial<Snapshot>) => setSnapshot((s) => ({ ...s, ...p }))
    const setSelection = (selection: Selection) => patch(selection ? { selection, sideTab: 'selection' } : { selection })
    const focusOn = (at: GeoPointDto) => setSnapshot((s) => ({ ...s, focus: { lat: at.lat, lon: at.lon, seq: (s.focus?.seq ?? 0) + 1 } }))
    return {
      ...snapshot,
      setMode: (mode) => patch({ mode }),
      setSelection,
      // Three placements wait for the same click; asking for one drops the others.
      setPendingCameraId: (pendingCameraId) => patch({ pendingCameraId, pendingDiagramId: null, pendingPart: null }),
      setPendingDiagramId: (pendingDiagramId) => patch({ pendingDiagramId, pendingCameraId: null, pendingPart: null }),
      setPendingPart: (pendingPart) => patch({ pendingPart, pendingCameraId: null, pendingDiagramId: null }),
      focusOn,
      select: (kind, id, at) => {
        setSnapshot((s) => ({
          ...s, mode: 'select', pendingCameraId: null, pendingDiagramId: null, pendingPart: null,
          selection: { kind, id }, sideTab: 'selection',
          // A list acts on the space's own map: a diagram open over it hides the object picked.
          openDiagramId: null,
          focus: { lat: at.lat, lon: at.lon, seq: (s.focus?.seq ?? 0) + 1 },
        }))
      },
      setSideTab: (sideTab) => patch({ sideTab }),
      openDiagram: (openDiagramId) => patch({ openDiagramId }),
      // Repeating the ask for the same node is a new ask: the counter is what the tree watches.
      showInTree: (id) => setSnapshot((s) => ({
        ...s, revealed: { id, seq: (s.revealed?.seq ?? 0) + 1 },
      })),
    }
  }, [snapshot])

  return <EditorStateContext.Provider value={value}>{children}</EditorStateContext.Provider>
}
