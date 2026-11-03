import type {
  MapCoordinate, MapDiagram, MapDiagramType, MapDiagramTypeRegion, MapGeometry,
} from '../../dto/mapDtos'

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
 * A diagram's own canvas: always its geometry, never where it stands in its parent — a diagram
 * packed into a region of a rack still opens on the paper it was drawn on. Rows written before
 * the two meanings were separated may carry no geometry at all, and they get a plain sheet
 * rather than a zero-sized one they could never be opened on.
 */
export const FALLBACK_PAPER: Rect = { left: 0, top: 0, width: 800, height: 600, rotation: 0 }

export function diagramPaper(diagram: MapDiagram | undefined): Rect {
  const rect = ownRect(diagram)
  if (rect.width > 0 && rect.height > 0) return { ...rect, left: 0, top: 0 }
  return { ...FALLBACK_PAPER }
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

/** A point in shares of a canvas, the space a diagram type stores its regions in. */
export type SharePoint = { x: number; y: number }

/** A region rectangle in shares of its canvas. */
export type ShareRect = { left: number; top: number; width: number; height: number; rotation: number }

/** The rectangle spanned by two corners, whichever way round they were dragged. */
export function normalizeRect(from: SharePoint, to: SharePoint): ShareRect {
  return {
    left: Math.min(from.x, to.x),
    top: Math.min(from.y, to.y),
    width: Math.abs(to.x - from.x),
    height: Math.abs(to.y - from.y),
    rotation: 0,
  }
}

/** Keeps a region on its canvas: a box dragged past an edge stops at it. */
export function clampRegion(rect: ShareRect): ShareRect {
  const width = Math.min(1, Math.max(0, rect.width))
  const height = Math.min(1, Math.max(0, rect.height))
  return {
    left: Math.min(1 - width, Math.max(0, rect.left)),
    top: Math.min(1 - height, Math.max(0, rect.top)),
    width,
    height,
    rotation: rect.rotation ?? 0,
  }
}

/**
 * The region a point of a canvas falls in. The last one drawn wins, so a region added on top of
 * another takes the click the way it takes the drawing.
 */
export function regionAtPoint(
  regions: MapDiagramTypeRegion[] | undefined, x: number, y: number,
): MapDiagramTypeRegion | undefined {
  for (let index = (regions?.length ?? 0) - 1; index >= 0; index -= 1) {
    const geometry = regions![index].geometry
    if (!geometry) continue

    const left = geometry.left ?? 0
    const top = geometry.top ?? 0
    if (x >= left && x <= left + (geometry.width ?? 0)
      && y >= top && y <= top + (geometry.height ?? 0)) {
      return regions![index]
    }
  }
  return undefined
}

/**
 * A figure on a diagram keeps its place as the canvas's own pixels, carried in the geometry's
 * lon and lat slots — the canvas is the frame, exactly as a made-up plan's coordinates are. So a
 * point is `{ lon: x, lat: y }` and nothing here converts to metres.
 */
export function figurePoint(coord: MapCoordinate | undefined): { x: number; y: number } {
  return { x: coord?.lon ?? 0, y: coord?.lat ?? 0 }
}

/** The geometry a point of the paper stands for. */
export function figureCoordinate(x: number, y: number): MapCoordinate {
  return { lat: y, lon: x }
}

/** The same shape, moved by a drag: every vertex travels, the type does not change. */
export function moveGeometry(geometry: MapGeometry | undefined, dx: number, dy: number): MapGeometry | undefined {
  if (!geometry?.coord) return geometry
  return {
    ...geometry,
    coord: geometry.coord.map((coord) => figureCoordinate((coord.lon ?? 0) + dx, (coord.lat ?? 0) + dy)),
  }
}
