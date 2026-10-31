import { useMemo, useRef, useState } from 'react'
import { Marker } from 'react-leaflet'
import L from 'leaflet'
import type { GeoPointDto } from '../dto/dtos'
import { MIDPOINT_ICON, VERTEX_ICON } from './vertexHandles'

type Props = {
  points: GeoPointDto[]
  /** A polygon closes on itself and midpoints wrap around; a line's ends are the last points. */
  closed?: boolean
  /** commit is false while a point is being dragged and true when it is let go. */
  onChange: (points: GeoPointDto[], commit: boolean) => void
}

/**
 * The points of a shape, for reshaping it: drag a point to move it, drag a midpoint to make a new
 * point there, Alt+click a point to remove it. Shared by the footprints of nested plans and by the
 * shapes drawn on a canvas — the same drag, on the same kind of data.
 */
export default function VertexEditor({ points, closed = true, onChange }: Props) {
  // The shape the drag started from. The handles stay put while it lasts — react-leaflet moves a
  // marker whenever its position prop is a new array, and that would pull the one being dragged
  // out from under the pointer.
  const [dragFrom, setDragFrom] = useState<GeoPointDto[] | null>(null)
  // The same points for the handlers, which may run before the state above has been rendered.
  const fromRef = useRef<GeoPointDto[] | null>(null)
  const anchor = dragFrom ?? points
  const begin = () => { fromRef.current = points; setDragFrom(points) }
  const end = () => { fromRef.current = null; setDragFrom(null) }
  const base = () => fromRef.current ?? points

  const minimum = closed ? 3 : 2
  const segments = closed ? anchor.length : Math.max(0, anchor.length - 1)
  const handles = useMemo(() => anchor.map((point, index) => {
    const next = anchor[(index + 1) % anchor.length]
    return {
      point: [point.lat, point.lon] as [number, number],
      // A line has no midpoint after its last point; a ring has one between every pair, the last
      // pair included.
      middle: index < segments ? [(point.lat + next.lat) / 2, (point.lon + next.lon) / 2] as [number, number] : null,
    }
  }), [anchor, segments])

  const at = (event: L.LeafletEvent): GeoPointDto => {
    const latLng = (event.target as L.Marker).getLatLng()
    return { lat: latLng.lat, lon: latLng.lng }
  }

  return (
    <>
      {handles.map((handle, index) => (
        <Marker
          key={`c${index}`}
          position={handle.point}
          icon={VERTEX_ICON}
          draggable
          eventHandlers={{
            click: (event) => {
              if (!(event.originalEvent as MouseEvent).altKey || points.length <= minimum) return
              onChange(points.filter((_, i) => i !== index), true)
            },
            dragstart: begin,
            drag: (event) => onChange(base().map((p, i) => (i === index ? at(event) : p)), false),
            dragend: (event) => {
              onChange(base().map((p, i) => (i === index ? at(event) : p)), true)
              end()
            },
          }}
        />
      ))}
      {handles.map((handle, index) => (handle.middle ? (
        <Marker
          key={`m${index}`}
          position={handle.middle}
          icon={MIDPOINT_ICON}
          draggable
          eventHandlers={{
            dragstart: begin,
            drag: (event) => onChange([...base().slice(0, index + 1), at(event), ...base().slice(index + 1)], false),
            dragend: (event) => {
              onChange([...base().slice(0, index + 1), at(event), ...base().slice(index + 1)], true)
              end()
            },
          }}
        />
      ) : null))}
    </>
  )
}
