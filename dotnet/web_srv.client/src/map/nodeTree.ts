import type { MapMarker } from '../dto/mapDtos'
import { sortMarkers } from './nodeKind'

/** One line of a node tree: the node and how deep under the roots it sits. */
export type TreeNodeRow = { marker: MapMarker; depth: number }

/** What the store knows about one parent's children; `loaded` says the page was read. */
export type TreePage = { ids: string[]; loaded: boolean }

/**
 * The visible rows of a node tree: the roots, and under each open row its page's children, down
 * to the depth the reader has opened. A page that has not been read yet is not walked -- opening
 * a row is what asks for it -- so the tree never invents children it does not know.
 */
export function treeRows(
  roots: MapMarker[],
  pages: Record<string, TreePage | undefined>,
  markers: Record<string, MapMarker>,
  expanded: ReadonlySet<string>,
): TreeNodeRow[] {
  const rows: TreeNodeRow[] = []
  // The server refuses to close a cycle, but a tree that recursed forever on bad data would take
  // the page with it; a node is drawn once.
  const seen = new Set<string>()

  const walk = (marker: MapMarker, depth: number) => {
    if (seen.has(marker.id)) return
    seen.add(marker.id)
    rows.push({ marker, depth })

    if (!expanded.has(marker.id)) return
    const page = pages[marker.id]
    if (!page?.loaded) return

    const children = page.ids
      .map((id) => markers[id])
      .filter((child): child is MapMarker => !!child)

    // The server returns a page in id order, which is a hash order to the reader.
    for (const child of sortMarkers(children)) walk(child, depth + 1)
  }

  for (const root of sortMarkers(roots)) walk(root, 0)
  return rows
}
