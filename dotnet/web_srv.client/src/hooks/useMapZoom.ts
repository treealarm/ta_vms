import { useEffect, useState } from 'react'
import { useMap } from 'react-leaflet'

/**
 * The map's current zoom, kept in state so a layer can show and hide what belongs to its level.
 *
 * A subscription of its own rather than `useMapEvents`: that one re-subscribes on every render, and
 * a programmatic `setView` called from another effect of the same commit -- entering a container,
 * say -- lands in the gap between the old handler being removed and the new one being attached. The
 * map moves and every layer stays on the zoom it last heard about.
 */
export function useMapZoom(): number {
  const map = useMap()
  const [zoom, setZoom] = useState(() => map.getZoom())

  useEffect(() => {
    const update = () => setZoom(map.getZoom())
    map.on('zoomend', update)
    update()
    return () => {
      map.off('zoomend', update)
    }
  }, [map])

  return zoom
}
