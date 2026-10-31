import type { PathOptions } from 'leaflet'
import { Circle, CircleMarker, Polygon, Polyline, Tooltip } from 'react-leaflet'
import type { MapGeoObject, MapMarker, MapTrackPoint } from '../dto/mapDtos'

/** Tracks answer a different question from states — where something was — so they have a colour
 *  of their own and never take the colour of the node they belong to. */
export const TRACK_COLOR = '#ab47bc'

type Props = {
  tracks: MapTrackPoint[]
  /** Names of the nodes the track points belong to, for the tooltip, where they are known. */
  markers?: Record<string, MapMarker>
}

/**
 * The track points recorded in the open space over the period the operator asked for, drawn by
 * the shape each one carries (`figure.location`): a polyline for a path, a polygon for an area, a
 * dot or a circle of metres for a position. `figure.id` ties a point back to the node it belongs
 * to, which is how its name reaches the tooltip.
 */
export default function TracksLayer({ tracks, markers }: Props) {
  return (
    <>
      {tracks.map((track, index) => renderTrack(track, index, markers))}
    </>
  )
}

function renderTrack(
  track: MapTrackPoint,
  index: number,
  markers: Record<string, MapMarker> | undefined,
) {
  const figure = track.figure
  const coords = (figure?.location?.coord ?? [])
    .filter((point) => Number.isFinite(point.lat) && Number.isFinite(point.lon))
    .map((point) => [point.lat as number, point.lon as number] as [number, number])
  if (coords.length === 0) return null

  // A track point's `id` is the object it belongs to, not a row id, so it repeats across the
  // points of one object; the position in the list is what makes the key unique.
  const key = `${index}:${figure?.id ?? ''}:${track.timestamp ?? ''}`
  const label = [
    markers?.[figure?.id ?? '']?.name,
    track.timestamp ? new Date(track.timestamp).toLocaleString() : undefined,
  ].filter(Boolean).join(' · ')

  const pathOptions: PathOptions = {
    color: TRACK_COLOR,
    weight: 2,
    opacity: 0.8,
    fillColor: TRACK_COLOR,
    fillOpacity: 0.2,
    dashArray: '4 4',
  }

  switch (figure?.location?.type) {
    case 'LineString':
      return (
        <Polyline key={key} positions={coords} pathOptions={pathOptions}>
          <Tooltip>{label}</Tooltip>
        </Polyline>
      )
    case 'Polygon':
      return (
        <Polygon key={key} positions={coords} pathOptions={pathOptions}>
          <Tooltip>{label}</Tooltip>
        </Polygon>
      )
    default:
      return circleOf(key, figure, coords[0], pathOptions, label)
  }
}

function circleOf(
  key: string,
  figure: MapGeoObject | undefined,
  centre: [number, number],
  pathOptions: PathOptions,
  label: string,
) {
  if ((figure?.radius ?? 0) > 0) {
    return (
      <Circle key={key} center={centre} radius={figure?.radius as number} pathOptions={pathOptions}>
        <Tooltip>{label}</Tooltip>
      </Circle>
    )
  }

  return (
    <CircleMarker key={key} center={centre} radius={3} pathOptions={{ ...pathOptions, fillOpacity: 0.9 }}>
      <Tooltip>{label}</Tooltip>
    </CircleMarker>
  )
}
