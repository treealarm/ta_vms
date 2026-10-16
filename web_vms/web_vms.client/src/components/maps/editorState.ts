import { createContext, useContext } from 'react'
import type { GeoPointDto } from '../../dto/dtos'
import type { MapFocus } from '../../map/SpaceCanvas'

/** What the next click on the map does. */
export type Mode = 'select' | 'landmark' | 'plan'

export type Selection = { kind: 'camera' | 'landmark' | 'footprint'; id: string } | null

/** Which of the two property tabs beside the map is open. */
export type SideTab = 'space' | 'selection'

export type EditorState = {
  mode: Mode
  setMode: (mode: Mode) => void
  selection: Selection
  /** Selecting something opens the Selection tab; clearing leaves the tabs where they are. */
  setSelection: (selection: Selection) => void
  /** A camera picked from the list; the next click on the map places it. */
  pendingCameraId: string | null
  setPendingCameraId: (cameraId: string | null) => void
  focus: MapFocus | null
  /** Pans the map to the point; the same point again pans again. */
  focusOn: (at: GeoPointDto) => void
  /** Picks an object from a list: selects it, drops any tool, and brings it into view. */
  select: (kind: NonNullable<Selection>['kind'], id: string, at: GeoPointDto) => void
  sideTab: SideTab
  setSideTab: (tab: SideTab) => void
}

export type Snapshot = {
  mode: Mode
  selection: Selection
  pendingCameraId: string | null
  focus: MapFocus | null
  sideTab: SideTab
}

export const FRESH: Snapshot = { mode: 'select', selection: null, pendingCameraId: null, focus: null, sideTab: 'space' }

export const EditorStateContext = createContext<EditorState | null>(null)

export function useEditorState(): EditorState {
  const state = useContext(EditorStateContext)
  if (!state) throw new Error('useEditorState outside EditorStateProvider')
  return state
}
