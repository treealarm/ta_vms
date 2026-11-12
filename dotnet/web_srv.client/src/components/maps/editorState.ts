import { createContext, useContext } from 'react'
import type { GeoPointDto } from '../../dto/dtos'
import type { MapFocus } from '../../map/SpaceCanvas'

/** What the next click on the map does. */
export type Mode = 'select' | 'landmark' | 'plan' | 'point' | 'line' | 'polygon'

/**
 * What is selected beside the map: a camera's placement, a landmark or a nested space's footprint
 * from the spatial half, or any map node — a drawn shape, a diagram — from the map's own tree.
 */
export type Selection =
  | { kind: 'camera' | 'landmark' | 'footprint' | 'node'; id: string }
  | null

/** The modes that draw a shape click by click instead of acting on what is already there. */
export const DRAW_MODES: Mode[] = ['point', 'line', 'polygon']

/** A camera part picked from the palette: the next click on the map makes a replica of it. */
export type PendingPart = {
  /** The part's canonical node; the replica's owner. */
  id: string
  name: string
  kind: 'digital_input' | 'relay'
}

/**
 * Which property tab beside the map is open: the space's own, what the selection is in this canvas
 * (a camera's placement, a landmark, a footprint, a diagram element), the selected node itself —
 * its name, look and properties, the same form whichever panel selected it — and its rights.
 */
export type SideTab = 'space' | 'selection' | 'object' | 'rights'

export type EditorState = {
  mode: Mode
  setMode: (mode: Mode) => void
  selection: Selection
  /** Selecting something opens the Selection tab; clearing leaves the tabs where they are. */
  setSelection: (selection: Selection) => void
  /** A camera picked from the list; the next click on the map places it. */
  pendingCameraId: string | null
  setPendingCameraId: (cameraId: string | null) => void
  /**
   * A camera whose diagram was asked for on the canvas: the next click puts the diagram there,
   * the way a click puts the camera itself. One at a time with `pendingCameraId`.
   */
  pendingDiagramId: string | null
  setPendingDiagramId: (cameraId: string | null) => void
  /**
   * A part of a device picked from the palette — a sensor or a relay. The next click on the map
   * makes a replica of it there, the way a click places a camera; a part has no placement row, so
   * the replica is an ordinary picture of the part's node.
   */
  pendingPart: PendingPart | null
  setPendingPart: (part: PendingPart | null) => void
  focus: MapFocus | null
  /** Pans the map to the point; the same point again pans again. */
  focusOn: (at: GeoPointDto) => void
  /** Picks an object from a list: selects it, drops any tool, and brings it into view. */
  select: (kind: NonNullable<Selection>['kind'], id: string, at: GeoPointDto) => void
  sideTab: SideTab
  setSideTab: (tab: SideTab) => void
  /**
   * A node picked beside the map, asked to be shown in the tree: the sidebar opens its Nodes tab,
   * unfolds the path down to it and scrolls there. `seq` repeats the ask for the same node.
   */
  revealed: { id: string; seq: number } | null
  showInTree: (id: string) => void
  /**
   * The mnemoscheme opened in place of the map, if one is. Kept apart from the selection: an
   * element inside it is selected while the diagram stays open, and only its breadcrumbs or a
   * space in the tree lead out.
   */
  openDiagramId: string | null
  openDiagram: (id: string | null) => void
}

export type Snapshot = {
  mode: Mode
  selection: Selection
  pendingCameraId: string | null
  pendingDiagramId: string | null
  pendingPart: PendingPart | null
  focus: MapFocus | null
  sideTab: SideTab
  openDiagramId: string | null
  revealed: { id: string; seq: number } | null
}

export const FRESH: Snapshot = {
  mode: 'select', selection: null, pendingCameraId: null, pendingDiagramId: null,
  pendingPart: null, focus: null,
  sideTab: 'space', openDiagramId: null, revealed: null,
}

export const EditorStateContext = createContext<EditorState | null>(null)

export function useEditorState(): EditorState {
  const state = useContext(EditorStateContext)
  if (!state) throw new Error('useEditorState outside EditorStateProvider')
  return state
}
