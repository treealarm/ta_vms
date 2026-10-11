import { useMemo, useRef } from 'react'
import { Marker, Polygon, Polyline, Tooltip } from 'react-leaflet'
import L from 'leaflet'
import type { CameraPlacementDto, LandmarkDto } from '../dto/dtos'
import { bearing, destination, distance, fovPolygon } from './geo'

const cameraIcon = (color: string, headingDeg: number, selected: boolean) => L.divIcon({
  className: 'vms-map-camera',
  html: `<div style="width:26px;height:26px;transform:rotate(${headingDeg}deg)">`
    + `<svg width="26" height="26" viewBox="0 0 26 26">`
    + `<path d="M13 1 L18 9 L8 9 Z" fill="${color}" stroke="#000" stroke-width="0.8"/>`
    + `<circle cx="13" cy="14" r="8" fill="${color}" stroke="${selected ? '#fff' : '#000'}" stroke-width="${selected ? 3 : 1}"/>`
    + `</svg></div>`,
  iconSize: [26, 26],
  iconAnchor: [13, 14],
})

const headingHandleIcon = L.divIcon({
  className: 'vms-map-handle',
  html: '<div style="width:14px;height:14px;border-radius:50%;background:#fff;border:2px solid #1976d2;cursor:grab"></div>',
  iconSize: [14, 14],
  iconAnchor: [7, 7],
})

const landmarkIcon = (selected: boolean) => L.divIcon({
  className: 'vms-map-landmark',
  html: `<div style="width:14px;height:14px;transform:rotate(45deg);background:#ffca28;`
    + `border:${selected ? '3px solid #fff' : '1px solid #000'}"></div>`,
  iconSize: [14, 14],
  iconAnchor: [7, 7],
})

type CameraProps = {
  placement: CameraPlacementDto
  label: string
  color: string
  selected?: boolean
  /** Lets the marker be dragged, and, when selected, its cone be turned and stretched. */
  editable?: boolean
  dimmed?: boolean
  onSelect?: () => void
  /** commit is false while dragging and true when the handle is let go. */
  onChange?: (placement: CameraPlacementDto, commit: boolean) => void
}

/** A camera and its view cone. heading 0 points the cone north, clockwise from there. */
export function CameraMarker({ placement, label, color, selected, editable, dimmed, onSelect, onChange }: CameraProps) {
  const p = placement
  const tip = destination({ lat: p.lat, lon: p.lon }, p.headingDeg, p.rangeM)
  const startRef = useRef(p)
  // One icon object per look: a marker handed a new one rebuilds its element, which ends a drag.
  const icon = useMemo(() => cameraIcon(color, p.headingDeg, !!selected), [color, p.headingDeg, selected])

  const moveCamera = (e: L.LeafletEvent, commit: boolean) => {
    const at = (e.target as L.Marker).getLatLng()
    onChange?.({ ...startRef.current, lat: at.lat, lon: at.lng }, commit)
  }

  const aimCone = (e: L.LeafletEvent, commit: boolean) => {
    const at = (e.target as L.Marker).getLatLng()
    const apex = { lat: p.lat, lon: p.lon }
    const to = { lat: at.lat, lon: at.lng }
    onChange?.({
      ...p,
      headingDeg: Math.round(bearing(apex, to)),
      rangeM: Math.max(1, Math.round(distance(apex, to))),
    }, commit)
  }

  return (
    <>
      <Polygon
        positions={fovPolygon(p)}
        pathOptions={{ color, weight: 1, fillColor: color, fillOpacity: dimmed ? 0.05 : selected ? 0.25 : 0.12, opacity: dimmed ? 0.3 : 0.8 }}
        eventHandlers={onSelect ? { click: onSelect } : undefined}
      />
      <Marker
        position={[p.lat, p.lon]}
        icon={icon}
        opacity={dimmed ? 0.45 : 1}
        draggable={!!editable}
        eventHandlers={{
          click: () => onSelect?.(),
          dragstart: () => { startRef.current = p },
          drag: (e) => moveCamera(e, false),
          dragend: (e) => moveCamera(e, true),
        }}
      >
        <Tooltip direction="top" offset={[0, -12]}>{label}</Tooltip>
      </Marker>
      {editable && selected && (
        <>
          <Polyline positions={[[p.lat, p.lon], [tip.lat, tip.lon]]} pathOptions={{ color: '#fff', weight: 1, dashArray: '4 4' }} interactive={false} />
          <Marker
            position={[tip.lat, tip.lon]}
            icon={headingHandleIcon}
            draggable
            eventHandlers={{ drag: (e) => aimCone(e, false), dragend: (e) => aimCone(e, true) }}
          />
        </>
      )}
    </>
  )
}

type LandmarkProps = {
  landmark: LandmarkDto
  selected?: boolean
  editable?: boolean
  onSelect?: () => void
  onMove?: (lat: number, lon: number) => void
}

export function LandmarkMarker({ landmark, selected, editable, onSelect, onMove }: LandmarkProps) {
  const icon = useMemo(() => landmarkIcon(!!selected), [selected])
  return (
    <Marker
      position={[landmark.lat, landmark.lon]}
      icon={icon}
      draggable={!!editable}
      eventHandlers={{
        click: () => onSelect?.(),
        dragend: (e) => {
          const at = (e.target as L.Marker).getLatLng()
          onMove?.(at.lat, at.lng)
        },
      }}
    >
      <Tooltip direction="top" offset={[0, -8]}>
        {landmark.name}{landmark.heightM ? ` · ${landmark.heightM} m` : ''}
      </Tooltip>
    </Marker>
  )
}
