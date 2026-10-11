import { useMemo, useRef, useState } from 'react'
import { Marker, Polygon, Popup, Tooltip } from 'react-leaflet'
import { List, ListItemButton, ListItemText } from '@mui/material'
import L from 'leaflet'
import type { GeoPointDto, SpaceDto } from '../dto/dtos'

/** Ray casting in degrees: exact enough for a building's outline. */
function contains(polygon: GeoPointDto[], p: GeoPointDto): boolean {
  let inside = false
  for (let i = 0, j = polygon.length - 1; i < polygon.length; j = i++) {
    const a = polygon[i]
    const b = polygon[j]
    if ((a.lat > p.lat) !== (b.lat > p.lat) && p.lon < ((b.lon - a.lon) * (p.lat - a.lat)) / (b.lat - a.lat) + a.lon) inside = !inside
  }
  return inside
}

type Props = {
  /** The spaces nested in the one shown; those with a footprint are drawn. */
  nested: SpaceDto[]
  selectedId?: string | null
  /** Clicked a footprint with nothing else under it, or picked one from the list of overlapping ones. */
  onPick: (space: SpaceDto) => void
}

/**
 * Where nested spaces lie, as outlines with their names. Floors of one building usually share an
 * outline, so a click that lands in several asks which one is meant instead of guessing.
 */
export function Footprints({ nested, selectedId, onPick }: Props) {
  const [choice, setChoice] = useState<{ at: L.LatLng; spaces: SpaceDto[] } | null>(null)
  const drawn = nested.filter((s) => s.footprint.length >= 3)
  // Floors drawn with one shared outline are one shape with all their names, not a pile of labels
  // on top of each other.
  const groups = new Map<string, SpaceDto[]>()
  for (const s of drawn) {
    const key = JSON.stringify(s.footprint)
    groups.set(key, [...(groups.get(key) ?? []), s])
  }
  const shapes = [...groups.values()]

  return (
    <>
      {shapes.map((group) => {
        const first = group[0]
        const selected = group.some((s) => s.id === selectedId)
        return (
          <Polygon
            key={first.id}
            positions={first.footprint.map((p) => [p.lat, p.lon] as [number, number])}
            pathOptions={{
              color: selected ? '#ffca28' : '#90caf9',
              weight: selected ? 3 : 2,
              dashArray: '6 4',
              fillColor: '#90caf9',
              fillOpacity: 0.08,
            }}
            eventHandlers={{
              click: (e) => {
                L.DomEvent.stopPropagation(e)
                const at = { lat: e.latlng.lat, lon: e.latlng.lng }
                const hits = drawn.filter((x) => contains(x.footprint, at))
                if (hits.length > 1) setChoice({ at: e.latlng, spaces: hits })
                else onPick(hits[0] ?? first)
              },
            }}
          >
            <Tooltip direction="center" permanent>{group.map((s) => s.name).join(' · ')}</Tooltip>
          </Polygon>
        )
      })}
      {choice && (
        <Popup position={choice.at} eventHandlers={{ remove: () => setChoice(null) }}>
          <List dense disablePadding sx={{ minWidth: 140 }}>
            {choice.spaces.map((s) => (
              <ListItemButton key={s.id} onClick={() => { setChoice(null); onPick(s) }}>
                <ListItemText primary={s.name} />
              </ListItemButton>
            ))}
          </List>
        </Popup>
      )}
    </>
  )
}

const vertexIcon = (color: string, size: number) => L.divIcon({
  className: 'vms-map-handle',
  html: `<div style="width:${size}px;height:${size}px;border-radius:50%;background:${color};border:2px solid #fff;`
    + `box-shadow:0 0 3px rgba(0,0,0,.7);cursor:move"></div>`,
  iconSize: [size, size],
  iconAnchor: [size / 2, size / 2],
})

// Built once: a marker handed a new icon object rebuilds its element, which ends a drag.
const VERTEX_ICON = vertexIcon('#ffca28', 14)
const MIDPOINT_ICON = vertexIcon('rgba(255,202,40,0.5)', 10)

type EditorProps = {
  footprint: GeoPointDto[]
  /** commit is false while a corner is being dragged and true when it is let go. */
  onChange: (footprint: GeoPointDto[], commit: boolean) => void
}

/**
 * Corners of a footprint, for reshaping it: drag a corner to move it, drag a midpoint to make a new
 * corner there, Alt+click a corner to remove it (a footprint keeps at least three).
 */
export function FootprintEditor({ footprint, onChange }: EditorProps) {
  // The outline the drag started from. The handles stay put while it lasts — react-leaflet moves a
  // marker whenever its position prop is a new array, and that would pull the one being dragged
  // out from under the pointer.
  const [dragFrom, setDragFrom] = useState<GeoPointDto[] | null>(null)
  // The same outline for the handlers, which may run before the state above has been rendered.
  const fromRef = useRef<GeoPointDto[] | null>(null)
  const anchor = dragFrom ?? footprint
  const begin = () => { fromRef.current = footprint; setDragFrom(footprint) }
  const end = () => { fromRef.current = null; setDragFrom(null) }
  const base = () => fromRef.current ?? footprint
  const handles = useMemo(() => anchor.map((p, i) => {
    const next = anchor[(i + 1) % anchor.length]
    return {
      corner: [p.lat, p.lon] as [number, number],
      middle: [(p.lat + next.lat) / 2, (p.lon + next.lon) / 2] as [number, number],
    }
  }), [anchor])

  const at = (e: L.LeafletEvent): GeoPointDto => {
    const ll = (e.target as L.Marker).getLatLng()
    return { lat: ll.lat, lon: ll.lng }
  }

  return (
    <>
      {handles.map((h, i) => (
        <Marker
          key={`c${i}`}
          position={h.corner}
          icon={VERTEX_ICON}
          draggable
          eventHandlers={{
            click: (e) => {
              if (!(e.originalEvent as MouseEvent).altKey || footprint.length <= 3) return
              onChange(footprint.filter((_, j) => j !== i), true)
            },
            dragstart: begin,
            drag: (e) => onChange(base().map((p, j) => (j === i ? at(e) : p)), false),
            dragend: (e) => {
              onChange(base().map((p, j) => (j === i ? at(e) : p)), true)
              end()
            },
          }}
        />
      ))}
      {handles.map((h, i) => (
        <Marker
          key={`m${i}`}
          position={h.middle}
          icon={MIDPOINT_ICON}
          draggable
          eventHandlers={{
            dragstart: begin,
            drag: (e) => onChange([...base().slice(0, i + 1), at(e), ...base().slice(i + 1)], false),
            dragend: (e) => {
              onChange([...base().slice(0, i + 1), at(e), ...base().slice(i + 1)], true)
              end()
            },
          }}
        />
      ))}
    </>
  )
}
