import { useState } from 'react'
import { Polygon, Popup, Tooltip } from 'react-leaflet'
import { List, ListItemButton, ListItemText } from '@mui/material'
import L from 'leaflet'
import type { GeoPointDto, SpaceDto } from '../dto/dtos'
import { ALARM_COLOR } from './cameraStatus'
import VertexEditor from './vertexEditor'

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
  /** Spaces with an alarm running in them or anywhere inside them; their outlines burn. */
  alarmed?: ReadonlySet<string>
  /** Clicked a footprint with nothing else under it, or picked one from the list of overlapping ones. */
  onPick: (space: SpaceDto) => void
}

/**
 * Where nested spaces lie, as outlines with their names. Floors of one building usually share an
 * outline, so a click that lands in several asks which one is meant instead of guessing.
 */
export function Footprints({ nested, selectedId, alarmed, onPick }: Props) {
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
        const alarm = group.some((s) => !!s.id && !!alarmed?.has(s.id))
        return (
          <Polygon
            // Keyed by alarm too: Leaflet sets a path's class once, when it is created.
            key={`${first.id}:${alarm}`}
            positions={first.footprint.map((p) => [p.lat, p.lon] as [number, number])}
            pathOptions={{
              color: alarm ? ALARM_COLOR : selected ? '#ffca28' : '#90caf9',
              weight: selected || alarm ? 3 : 2,
              dashArray: alarm ? undefined : '6 4',
              fillColor: alarm ? ALARM_COLOR : '#90caf9',
              fillOpacity: alarm ? 0.18 : 0.08,
              className: alarm ? 'vms-alarm-pulse' : undefined,
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

type EditorProps = {
  footprint: GeoPointDto[]
  /** commit is false while a corner is being dragged and true when it is let go. */
  onChange: (footprint: GeoPointDto[], commit: boolean) => void
}

/**
 * Corners of a footprint, for reshaping it; the drag itself is `VertexEditor`'s, which the drawn
 * shapes on a canvas use too.
 */
export function FootprintEditor({ footprint, onChange }: EditorProps) {
  return <VertexEditor points={footprint} onChange={onChange} />
}
