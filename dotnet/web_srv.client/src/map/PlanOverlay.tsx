import { useEffect, useMemo, useRef, useState } from 'react'
import { Marker, Polygon, useMap } from 'react-leaflet'
import L from 'leaflet'
import type { GeoPointDto, PlanCornersDto, SpaceDto } from '../dto/dtos'
import { useSpacePlanImage } from '../hooks/useSpacePlanImage'
import { bottomRight, fromEnu, planAffine, toEnu } from './geo'
import { RotatedImageLayer } from './layers'

type Props = {
  space: SpaceDto
  /** A draft placement being edited; the stored one otherwise. */
  corners?: PlanCornersDto
  editable?: boolean
  /** commit is false while a handle is being dragged and true when it is let go. */
  onCornersChange?: (corners: PlanCornersDto, commit: boolean) => void
  opacity?: number
}

const handleIcon = (glyph: string, title: string, cursor: string) => L.divIcon({
  className: 'vms-map-handle',
  html: `<div title="${title}" style="width:22px;height:22px;border-radius:50%;background:#1976d2;color:#fff;`
    + `display:flex;align-items:center;justify-content:center;font-size:14px;border:2px solid #fff;`
    + `box-shadow:0 0 4px rgba(0,0,0,.7);cursor:${cursor}">${glyph}</div>`,
  iconSize: [22, 22],
  iconAnchor: [11, 11],
})

// Built once. A marker given a new icon object rebuilds its element, and an element rebuilt under
// the pointer ends the drag — the handle would let go of the mouse on the first step.
const MOVE_ICON = handleIcon('✥', 'move', 'move')
const ROTATE_ICON = handleIcon('⟳', 'rotate', 'grab')
const SCALE_ICON = handleIcon('⤡', 'resize', 'nwse-resize')

/** How far above the plan's top edge the rotate handle stands, as a share of its height. */
const ROTATE_HANDLE_OFFSET = 0.12

type Handle = 'move' | 'rotate' | 'scale'

/**
 * A plan space's picture, in that space. Editable, it grows three handles: the middle one moves the plan,
 * the one above its top edge turns it about its centre, the corner one resizes it about its centre.
 * A floor plan keeps its proportions, so these are all it needs — free corners would only let it be
 * skewed by accident — and each does one thing, so turning a plan never changes its size.
 */
export default function PlanOverlay({ space, corners, editable, onCornersChange, opacity = 0.85 }: Props) {
  const map = useMap()
  const plan = space.plan
  const placed = corners ?? plan?.corners
  const url = useSpacePlanImage(space.id, plan?.etag)
  const layerRef = useRef<RotatedImageLayer | null>(null)
  // The placement a drag started from: each step is computed from it rather than accumulated, and
  // the handles stay where they were while it lasts — the one being dragged is under the pointer
  // anyway, and moving the others mid-drag would only make them jump.
  const [dragFrom, setDragFrom] = useState<{ corners: PlanCornersDto; at: L.LatLng } | null>(null)

  useEffect(() => {
    if (!url || !plan || !placed) return
    const layer = new RotatedImageLayer(url, placed, plan.widthPx, plan.heightPx, opacity)
    layer.addTo(map)
    layerRef.current = layer
    return () => {
      layer.remove()
      layerRef.current = null
    }
    // Re-created only when the image or its size changes; corners and opacity are updated in place.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [map, url, plan?.widthPx, plan?.heightPx])

  useEffect(() => { if (placed) layerRef.current?.setCorners(placed) }, [placed])
  useEffect(() => { layerRef.current?.setOpacity(opacity) }, [opacity])

  // Memoised, not just computed: react-leaflet moves a marker whenever its position prop is a new
  // array, and doing that mid-drag snaps the handle back from under the pointer.
  const anchorCorners = dragFrom?.corners ?? placed
  const handlesAt = useMemo(() => {
    if (!plan || !anchorCorners) return null
    const w = plan.widthPx
    const h = plan.heightPx
    const anchors = planAffine(anchorCorners, w, h)
    const at = (x: number, y: number): [number, number] => {
      const p = anchors.pixelToGeo(x, y)
      return [p.lat, p.lon]
    }
    return { move: at(w / 2, h / 2), rotate: at(w / 2, -h * ROTATE_HANDLE_OFFSET), scale: at(w, h) }
  }, [plan, anchorCorners])

  if (!plan || !placed || !editable || !handlesAt) return null

  const w = plan.widthPx
  const h = plan.heightPx
  const outline: [number, number][] = [placed.topLeft, placed.topRight, bottomRight(placed), placed.bottomLeft]
    .map((p) => [p.lat, p.lon])

  const begin = (e: L.LeafletEvent) => setDragFrom({ corners: placed, at: (e.target as L.Marker).getLatLng() })

  const step = (handle: Handle, e: L.LeafletEvent, commit: boolean) => {
    if (!dragFrom) return
    const c = dragFrom.corners
    const to = (e.target as L.Marker).getLatLng()
    const next = handle === 'move'
      ? shifted(c, dragFrom.at, to)
      : about(c, w, h, dragFrom.at, to, handle)
    if (next) onCornersChange?.(next, commit)
    if (commit) setDragFrom(null)
  }

  const handlers = (handle: Handle) => ({
    dragstart: begin,
    drag: (e: L.LeafletEvent) => step(handle, e, false),
    dragend: (e: L.LeafletEvent) => step(handle, e, true),
  })

  return (
    <>
      <Polygon positions={outline} pathOptions={{ color: '#90caf9', weight: 1, dashArray: '6 4', fill: false }} interactive={false} />
      <Marker position={handlesAt.move} draggable icon={MOVE_ICON} eventHandlers={handlers('move')} />
      <Marker position={handlesAt.rotate} draggable icon={ROTATE_ICON} eventHandlers={handlers('rotate')} />
      <Marker position={handlesAt.scale} draggable icon={SCALE_ICON} eventHandlers={handlers('scale')} />
    </>
  )
}

const geo = (p: L.LatLng): GeoPointDto => ({ lat: p.lat, lon: p.lng })

/** The plan moved by however far the pointer has gone. */
function shifted(c: PlanCornersDto, from: L.LatLng, to: L.LatLng): PlanCornersDto {
  const d = toEnu(geo(from), geo(to))
  const shift = (p: GeoPointDto) => fromEnu(p, d.east, d.north)
  return { topLeft: shift(c.topLeft), topRight: shift(c.topRight), bottomLeft: shift(c.bottomLeft) }
}

/**
 * The plan turned or resized about its centre. Turning follows the pointer's angle around the
 * centre; resizing follows how far along the handle's own diagonal the pointer has gone, so the
 * corner slides along that line under the pointer instead of the plan also turning.
 */
function about(
  c: PlanCornersDto, w: number, h: number, from: L.LatLng, to: L.LatLng, handle: 'rotate' | 'scale',
): PlanCornersDto | null {
  const pivot = planAffine(c, w, h).pixelToGeo(w / 2, h / 2)
  const a = toEnu(pivot, geo(from))
  const b = toEnu(pivot, geo(to))
  let k = 1
  let theta = 0
  if (handle === 'rotate') {
    theta = Math.atan2(b.north, b.east) - Math.atan2(a.north, a.east)
  } else {
    k = (a.east * b.east + a.north * b.north) / (a.east * a.east + a.north * a.north)
    // Through the centre and out the other side would mirror the plan; stop just short of it.
    if (!Number.isFinite(k)) return null
    k = Math.max(k, 0.02)
  }
  const cos = Math.cos(theta)
  const sin = Math.sin(theta)
  const apply = (p: GeoPointDto) => {
    const v = toEnu(pivot, p)
    return fromEnu(pivot, k * (v.east * cos - v.north * sin), k * (v.east * sin + v.north * cos))
  }
  return { topLeft: apply(c.topLeft), topRight: apply(c.topRight), bottomLeft: apply(c.bottomLeft) }
}
