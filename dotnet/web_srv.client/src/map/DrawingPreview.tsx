import { CircleMarker, Polygon, Polyline, useMapEvents } from 'react-leaflet'
import type { GeoPointDto } from '../dto/dtos'

const PREVIEW = { color: '#ffca28', weight: 2, dashArray: '6 4', fillColor: '#ffca28', fillOpacity: 0.12 }

/** The shape being drawn, click by click, before it is saved. */
export default function DrawingPreview({ points, closed }: { points: GeoPointDto[]; closed: boolean }) {
  const positions = points.map((point) => [point.lat, point.lon] as [number, number])
  if (positions.length === 0) return null

  return (
    <>
      {positions.length >= 2 ? (closed ? (
        <Polygon positions={positions} pathOptions={{ ...PREVIEW, interactive: false }} />
      ) : (
        <Polyline positions={positions} pathOptions={{ ...PREVIEW, fillOpacity: 0, interactive: false }} />
      )) : null}
      {positions.map((position, index) => (
        <CircleMarker
          key={index}
          center={position}
          radius={4}
          pathOptions={{ color: '#ffca28', fillColor: '#ffca28', fillOpacity: 1, interactive: false }}
        />
      ))}
    </>
  )
}

/**
 * A double click ends a shape. Leaflet reports the two clicks of a double click before it reports
 * the double click itself, so the caller drops the point that landed twice.
 */
export function FinishOnDoubleClick({ onFinish }: { onFinish: () => void }) {
  useMapEvents({ dblclick: () => onFinish() })
  return null
}
