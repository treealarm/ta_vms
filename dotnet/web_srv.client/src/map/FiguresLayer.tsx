import { Fragment, useMemo, useState } from 'react'
import L from 'leaflet'
import { Circle, CircleMarker, Marker, Polygon, Polyline, Tooltip, useMap, useMapEvents } from 'react-leaflet'
import type { GeoPointDto } from '../dto/dtos'
import type {
  MapAlarmState, MapFigure, MapGeometry, MapLevel, MapObjectState, MapStateDescription,
} from '../dto/mapDtos'
import { nodeStyle } from './nodeStyle'
import VertexEditor from './vertexEditor'
import { VERTEX_ICON } from './vertexHandles'

type Props = {
  figures: MapFigure[]
  states: Record<string, MapObjectState>
  alarms: Record<string, MapAlarmState>
  descriptions: Record<string, MapStateDescription>
  levels: MapLevel[]
  selectedId: string | null
  onSelect: (id: string) => void
  /** Figures drawn by something else — a camera by its placement, a space by its footprint. */
  skipIds?: Set<string>
  /** The administrator's canvas: the selected shape grows handles and can be reshaped. */
  editable?: boolean
  /** commit is false while a shape is being dragged and true when it is let go. */
  onGeometryChange?: (figure: MapFigure, geometry: MapGeometry, commit: boolean) => void
}

/** A small icon from `visual`, turned by its own angle; the picture is base64 in the row. */
function figureIcon(figure: MapFigure): L.DivIcon {
  const width = figure.visual?.imageWidth || 28
  const height = figure.visual?.imageHeight || 28
  const rotation = figure.visual?.rotationDeg ?? 0
  const raw = figure.visual?.image ?? ''
  const src = raw.startsWith('data:') ? raw : `data:image/png;base64,${raw}`
  const caption = figure.visual?.text ? escapeHtml(figure.visual.text) : ''

  const html = `
    <div style="width:${width}px;height:${height}px;transform:rotate(${rotation}deg);transform-origin:center;display:flex;flex-direction:column;align-items:center;">
      ${raw ? `<img src="${src}" alt="" style="width:${width}px;height:${height}px;object-fit:contain;pointer-events:none;" />` : ''}
      ${caption
        ? `<span style="font-size:10px;color:#eee;white-space:nowrap;text-shadow:0 1px 2px #000;">${caption}</span>`
        : ''}
    </div>`

  return L.divIcon({
    className: 'map-figure-icon',
    html,
    iconSize: [width, height],
    iconAnchor: [width / 2, height / 2],
  })
}

function escapeHtml(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;')
}

/**
 * The arbitrary shapes of one canvas: points (a circle of metres, a dot or an icon), lines and
 * polygons, each painted by the §4.8 rule — alarm red, else its first state's colour, else its own
 * visual colour — and filtered by the scale layer the node belongs to.
 */
export default function FiguresLayer({
  figures, states, alarms, descriptions, levels, selectedId, onSelect, skipIds,
  editable, onGeometryChange,
}: Props) {
  const map = useMap()
  const [zoom, setZoom] = useState(() => map.getZoom())
  useMapEvents({ zoomend: () => setZoom(map.getZoom()) })

  const ranges = useMemo(() => {
    const byName = new Map<string, { min: number; max: number }>()
    for (const level of levels) {
      if (level.zoomLevel) {
        byName.set(level.zoomLevel, { min: level.zoomMin ?? 0, max: level.zoomMax ?? 99 })
      }
    }
    return byName
  }, [levels])

  const shown = figures.filter((figure) => {
    if (skipIds?.has(figure.id) || figure.nodeKind === 'space') return false
    if (!figure.zoomLevel) return true

    const range = ranges.get(figure.zoomLevel)
    return !range || (zoom >= range.min && zoom <= range.max)
  })

  return (
    <>
      {shown.map((figure) => renderFigure(
        figure, states[figure.id], alarms[figure.id], descriptions, selectedId, onSelect,
        editable && figure.id === selectedId ? onGeometryChange : undefined,
      ))}
    </>
  )
}

function renderFigure(
  figure: MapFigure,
  state: MapObjectState | undefined,
  alarm: MapAlarmState | undefined,
  descriptions: Record<string, MapStateDescription>,
  selectedId: string | null,
  onSelect: (id: string) => void,
  /** Set only for the selected shape on the administrator's canvas. */
  reshape?: (figure: MapFigure, geometry: MapGeometry, commit: boolean) => void,
) {
  const coords = (figure.geometry?.coord ?? [])
    .filter((point) => Number.isFinite(point.lat) && Number.isFinite(point.lon))
    .map((point) => [point.lat as number, point.lon as number] as [number, number])
  if (coords.length === 0) return null

  const style = nodeStyle(figure, state, alarm, descriptions)
  const selected = figure.id === selectedId
  const handlers = { click: () => onSelect(figure.id) }
  const pathOptions = {
    color: style.color,
    weight: selected ? 4 : 2,
    dashArray: selected ? '6 4' : undefined,
    fillColor: style.color,
    fillOpacity: 0.2,
  }

  // A polygon arrives closed (the first point repeated at the end); its handles are the corners
  // alone, and whatever is dragged is closed again before it is saved.
  const corners = (): GeoPointDto[] => {
    const points = coords.map(([lat, lon]) => ({ lat, lon }))
    const last = points[points.length - 1]
    const first = points[0]
    const closedRing = figure.geometry?.type === 'Polygon'
      && points.length > 3 && last.lat === first.lat && last.lon === first.lon
    return closedRing ? points.slice(0, -1) : points
  }
  const withCorners = (points: GeoPointDto[]): MapGeometry => ({
    ...figure.geometry,
    type: figure.geometry?.type,
    coord: figure.geometry?.type === 'Polygon' ? [...points, points[0]] : points,
  })

  const handles = reshape ? (
    figure.geometry?.type === 'Point' ? (
      <Marker
        position={coords[0]}
        icon={VERTEX_ICON}
        draggable
        eventHandlers={{
          drag: (event) => {
            const latLng = (event.target as L.Marker).getLatLng()
            reshape(figure, withCorners([{ lat: latLng.lat, lon: latLng.lng }]), false)
          },
          dragend: (event) => {
            const latLng = (event.target as L.Marker).getLatLng()
            reshape(figure, withCorners([{ lat: latLng.lat, lon: latLng.lng }]), true)
          },
        }}
      />
    ) : (
      <VertexEditor
        points={corners()}
        closed={figure.geometry?.type === 'Polygon'}
        onChange={(points, commit) => reshape(figure, withCorners(points), commit)}
      />
    )
  ) : null

  switch (figure.geometry?.type) {
    case 'LineString':
      return (
        <Fragment key={figure.id}>
          <Polyline positions={coords} pathOptions={{ ...pathOptions, fillOpacity: 0 }}
            eventHandlers={handlers}>
            <Tooltip>{figure.name}</Tooltip>
          </Polyline>
          {handles}
        </Fragment>
      )
    case 'Polygon':
      return (
        <Fragment key={figure.id}>
          <Polygon positions={coords} pathOptions={pathOptions} eventHandlers={handlers}>
            <Tooltip>{figure.name}</Tooltip>
          </Polygon>
          {handles}
        </Fragment>
      )
    default: {
      const [lat, lon] = coords[0]
      const caption = style.text || figure.name || ''

      if (figure.visual?.image) {
        return (
          <Fragment key={figure.id}>
            <Marker position={[lat, lon]} icon={figureIcon(figure)} eventHandlers={handlers}>
              <Tooltip>{figure.name}</Tooltip>
            </Marker>
            {handles}
          </Fragment>
        )
      }

      if ((figure.radius ?? 0) > 0) {
        return (
          <Fragment key={figure.id}>
            <Circle center={[lat, lon]} radius={figure.radius as number}
              pathOptions={pathOptions} eventHandlers={handlers}>
              <Tooltip permanent={!!style.text} direction="top">{caption}</Tooltip>
            </Circle>
            {handles}
          </Fragment>
        )
      }

      return (
        <Fragment key={figure.id}>
          <CircleMarker center={[lat, lon]} radius={selected ? 9 : 7}
            pathOptions={pathOptions} eventHandlers={handlers}>
            <Tooltip permanent={!!style.text} direction="top">{caption}</Tooltip>
          </CircleMarker>
          {handles}
        </Fragment>
      )
    }
  }
}
