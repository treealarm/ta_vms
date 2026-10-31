import type { MapDiagram, MapDiagramType, MapDiagramTypeRegion } from '../../dto/mapDtos'

/** A rectangle in the pixels of a diagram's own canvas. */
export type Rect = { left: number; top: number; width: number; height: number; rotation: number }

export const EMPTY_RECT: Rect = { left: 0, top: 0, width: 0, height: 0, rotation: 0 }

/** A diagram row's own rectangle in its parent's canvas; a node without one has no size. */
export function ownRect(diagram: MapDiagram | undefined): Rect {
  const geometry = diagram?.geometry
  return {
    left: geometry?.left ?? 0,
    top: geometry?.top ?? 0,
    width: geometry?.width ?? 0,
    height: geometry?.height ?? 0,
    rotation: geometry?.rotation ?? 0,
  }
}

/**
 * Where a region of a diagram type lies inside a diagram of that type: a region is stored in
 * shares of its diagram's rectangle, so the same type fits racks of any size.
 */
export function regionRect(region: MapDiagramTypeRegion | undefined, parent: Rect): Rect | null {
  const geometry = region?.geometry
  if (!geometry) return null

  return {
    left: parent.width * (geometry.left ?? 0),
    top: parent.height * (geometry.top ?? 0),
    width: parent.width * (geometry.width ?? 0),
    height: parent.height * (geometry.height ?? 0),
    rotation: geometry.rotation ?? 0,
  }
}

/**
 * Where a child is drawn in its parent's canvas: in the region of the parent's type when it
 * names one, otherwise at its own rectangle. Null when neither says anything — the node is in the
 * tree but has nowhere to stand.
 */
export function elementRect(
  diagram: MapDiagram | undefined,
  parentType: MapDiagramType | undefined,
  parent: Rect,
): Rect | null {
  if (!diagram) return null

  if (diagram.regionId) {
    const region = parentType?.regions?.find((candidate) => candidate.regionKey === diagram.regionId)
    return regionRect(region, parent)
  }

  return ownRect(diagram)
}

/** How much the canvas must shrink to show the diagram whole, never growing it past its size. */
export function fitScale(container: { width: number; height: number }, paper: Rect): number {
  if (paper.width <= 0 || paper.height <= 0) return 1
  if (container.width <= 0 || container.height <= 0) return 1

  const margin = 32
  return Math.min(
    1,
    (container.width - margin) / paper.width,
    (container.height - margin) / paper.height,
  )
}
