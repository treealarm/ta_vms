import L from 'leaflet'

const handleIcon = (color: string, size: number) => L.divIcon({
  className: 'vms-map-handle',
  html: `<div style="width:${size}px;height:${size}px;border-radius:50%;background:${color};border:2px solid #fff;`
    + `box-shadow:0 0 3px rgba(0,0,0,.7);cursor:move"></div>`,
  iconSize: [size, size],
  iconAnchor: [size / 2, size / 2],
})

// Built once: a marker handed a new icon object rebuilds its element, which ends a drag.
export const VERTEX_ICON = handleIcon('#ffca28', 14)
export const MIDPOINT_ICON = handleIcon('rgba(255,202,40,0.5)', 10)
