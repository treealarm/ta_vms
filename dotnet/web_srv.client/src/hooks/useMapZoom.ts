import { useState } from 'react'
import { useMap, useMapEvents } from 'react-leaflet'

/** The map's current zoom, kept in state so a layer can show and hide what belongs to its level. */
export function useMapZoom(): number {
  const map = useMap()
  const [zoom, setZoom] = useState(() => map.getZoom())
  useMapEvents({ zoomend: () => setZoom(map.getZoom()) })
  return zoom
}
