import { useEffect, useRef } from 'react'
import { Box, Typography } from '@mui/material'
import { AttributionControl, MapContainer, ScaleControl, useMap, useMapEvents } from 'react-leaflet'
import type { LatLngBoundsExpression } from 'leaflet'
import { useTranslation } from 'react-i18next'
import type { GeoPointDto, SpaceDto } from '../dto/dtos'
import { useAppSelector } from '../store'
import { AuthTileLayer, MetricGridLayer } from './layers'
import { boundsCentre } from './geo'
import './map.css'

type Props = {
  space: SpaceDto
  /**
   * Map tiles under a plan in real coordinates, so it can be lined up with the world while it is
   * being aligned. Never for operators: a plan is a world of its own, not a layer over the map.
   */
  alignOnMap?: boolean
  /** Fitted once when the map opens, in place of the space's own default view. */
  initialBounds?: LatLngBoundsExpression | null
  onMapClick?: (p: GeoPointDto) => void
  /** Crosshair cursor while the next click places something. */
  picking?: boolean
  /**
   * Controls drawn over the map, outside Leaflet's container. Inside it, a click on them would
   * reach the map's own listener before React could stop it, and place something underneath.
   */
  overlay?: React.ReactNode
  /** Brought into view whenever it changes — an object picked from a list beside the map. */
  focus?: MapFocus | null
  children?: React.ReactNode
}

/** `seq` makes picking the same object again pan to it again. */
export type MapFocus = { lat: number; lon: number; seq: number }

// Close enough to tell one camera from the next; a map already zoomed in further stays as it is.
const FOCUS_MIN_ZOOM = 19

const LEAFLET_PREFIX = '<a href="https://leafletjs.com" target="_blank" rel="noreferrer">Leaflet</a>'

/**
 * What every maps page draws on: a map space gets tiles (when a provider is configured), a plan a
 * metric grid under its picture. Keyed by space by its callers, so switching spaces recentres.
 */
export default function SpaceCanvas({ space, alignOnMap, initialBounds, onMapClick, picking, overlay, focus, children }: Props) {
  const { t } = useTranslation()
  const tilesEnabled = useAppSelector((s) => s.frontendConfig.mapTilesEnabled)
  const wantsTiles = space.kind === 'map' || (!!alignOnMap && space.realCoordinates)
  const showNoTiles = wantsTiles && !tilesEnabled
  // A plan in made-up coordinates is measured from (0, 0); one in real coordinates has no zero of
  // its own, so its grid simply starts where it opens.
  const gridOrigin = space.realCoordinates ? boundsCentre(space.view) : { lat: 0, lon: 0 }

  return (
    <Box sx={{ position: 'relative', flex: 1, minHeight: 0, '& .leaflet-container': { cursor: picking ? 'crosshair' : undefined } }}>
      <MapContainer
        bounds={[[space.view.southWest.lat, space.view.southWest.lon], [space.view.northEast.lat, space.view.northEast.lon]]}
        maxZoom={22}
        style={{ position: 'absolute', inset: 0, background: '#2b2b2b' }}
        attributionControl={false}
      >
        {/* Leaflet's default prefix carries a national flag; a product map stays neutral. */}
        <AttributionControl prefix={LEAFLET_PREFIX} />
        <Background tiles={wantsTiles && tilesEnabled} gridOrigin={wantsTiles ? null : gridOrigin} />
        <ScaleControl metric imperial={false} position="bottomleft" />
        <FitOnce bounds={initialBounds} />
        <AutoResize />
        {onMapClick && <ClickCapture onClick={onMapClick} />}
        <FocusOn focus={focus} />
        {children}
      </MapContainer>
      {overlay}
      {showNoTiles && (
        <Typography
          variant="caption"
          sx={{ position: 'absolute', top: 8, left: '50%', transform: 'translateX(-50%)', zIndex: 1000, px: 1, py: 0.5, borderRadius: 1, bgcolor: 'rgba(0,0,0,0.6)', pointerEvents: 'none' }}
        >
          {t('maps.noTilesHint')}
        </Typography>
      )}
    </Box>
  )
}

function Background({ tiles, gridOrigin }: { tiles: boolean; gridOrigin: GeoPointDto | null }) {
  const map = useMap()
  const attribution = useAppSelector((s) => s.frontendConfig.mapTileAttribution)
  const originLat = gridOrigin?.lat
  const originLon = gridOrigin?.lon
  useEffect(() => {
    const layer = tiles
      ? new AuthTileLayer(attribution)
      : originLat !== undefined && originLon !== undefined ? new MetricGridLayer({ lat: originLat, lon: originLon }) : null
    layer?.addTo(map)
    return () => { layer?.remove() }
  }, [map, tiles, originLat, originLon, attribution])
  return null
}

function FitOnce({ bounds }: { bounds?: LatLngBoundsExpression | null }) {
  const map = useMap()
  const done = useRef(false)
  useEffect(() => {
    if (done.current || !bounds) return
    done.current = true
    map.fitBounds(bounds, { padding: [40, 40], maxZoom: 20 })
  }, [map, bounds])
  return null
}

/** The map sits in flex layouts that change size without a window resize; Leaflet must be told. */
function AutoResize() {
  const map = useMap()
  useEffect(() => {
    const observer = new ResizeObserver(() => map.invalidateSize())
    observer.observe(map.getContainer())
    return () => observer.disconnect()
  }, [map])
  return null
}

function FocusOn({ focus }: { focus?: MapFocus | null }) {
  const map = useMap()
  useEffect(() => {
    if (focus) map.setView([focus.lat, focus.lon], Math.max(map.getZoom(), FOCUS_MIN_ZOOM))
  }, [map, focus])
  return null
}

function ClickCapture({ onClick }: { onClick: (p: GeoPointDto) => void }) {
  useMapEvents({ click: (e) => onClick({ lat: e.latlng.lat, lon: e.latlng.lng }) })
  return null
}
