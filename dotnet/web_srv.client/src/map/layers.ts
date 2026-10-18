import L from 'leaflet'
import type { GeoPointDto, PlanCornersDto } from '../dto/dtos'
import { toEnu } from './geo'

/**
 * An image placed by three of its corners — position, scale and rotation in one affine transform.
 *
 * Leaflet's own ImageOverlay only takes north-up bounds, and the plugin that adds rotation patches a
 * global `L` that Leaflet's ES module build does not create, so this is the few lines it needs: the
 * image is laid out at its natural size and a CSS matrix carries its pixel axes onto the map's.
 */
export class RotatedImageLayer extends L.Layer {
  private img: HTMLImageElement | null = null
  private url: string
  private corners: PlanCornersDto
  private readonly widthPx: number
  private readonly heightPx: number
  private opacity: number

  constructor(url: string, corners: PlanCornersDto, widthPx: number, heightPx: number, opacity = 0.85) {
    super()
    this.url = url
    this.corners = corners
    this.widthPx = widthPx
    this.heightPx = heightPx
    this.opacity = opacity
  }

  onAdd(map: L.Map): this {
    const img = L.DomUtil.create('img', 'leaflet-image-layer leaflet-zoom-animated') as HTMLImageElement
    img.src = this.url
    img.style.position = 'absolute'
    img.style.left = '0'
    img.style.top = '0'
    img.style.width = `${this.widthPx}px`
    img.style.height = `${this.heightPx}px`
    img.style.transformOrigin = '0 0'
    img.style.opacity = String(this.opacity)
    img.style.maxWidth = 'none'
    img.draggable = false
    this.getPane()?.appendChild(img)
    this.img = img
    this.place((ll) => map.latLngToLayerPoint(ll))
    return this
  }

  onRemove(): this {
    this.img?.remove()
    this.img = null
    return this
  }

  getEvents(): Record<string, L.LeafletEventHandlerFn> {
    return {
      zoom: () => this.reset(),
      viewreset: () => this.reset(),
      zoomanim: (e) => this.animate(e as L.ZoomAnimEvent),
    }
  }

  setCorners(corners: PlanCornersDto): void {
    this.corners = corners
    this.reset()
  }

  setUrl(url: string): void {
    this.url = url
    if (this.img) this.img.src = url
  }

  setOpacity(opacity: number): void {
    this.opacity = opacity
    if (this.img) this.img.style.opacity = String(opacity)
  }

  private reset(): void {
    const map = this._map
    if (map) this.place((ll) => map.latLngToLayerPoint(ll))
  }

  private animate(e: L.ZoomAnimEvent): void {
    // The same private helper Leaflet's own ImageOverlay animates with.
    const map = this._map as L.Map & { _latLngToNewLayerPoint(ll: L.LatLng, zoom: number, center: L.LatLng): L.Point }
    if (map) this.place((ll) => map._latLngToNewLayerPoint(ll, e.zoom, e.center))
  }

  private place(project: (ll: L.LatLng) => L.Point): void {
    if (!this.img) return
    const p0 = project(L.latLng(this.corners.topLeft.lat, this.corners.topLeft.lon))
    const p1 = project(L.latLng(this.corners.topRight.lat, this.corners.topRight.lon))
    const p2 = project(L.latLng(this.corners.bottomLeft.lat, this.corners.bottomLeft.lon))
    const a = (p1.x - p0.x) / this.widthPx
    const b = (p1.y - p0.y) / this.widthPx
    const c = (p2.x - p0.x) / this.heightPx
    const d = (p2.y - p0.y) / this.heightPx
    this.img.style.transform = `matrix(${a}, ${b}, ${c}, ${d}, ${p0.x}, ${p0.y})`
  }
}

/**
 * Map tiles through the backend's caching proxy. A plain <img src> cannot carry the bearer token
 * the proxy requires, so each tile is fetched with the app's authenticated fetch and shown from a
 * blob URL — the same way plan images and crops are shown elsewhere.
 */
export class AuthTileLayer extends L.TileLayer {
  // In-flight downloads by tile element. Zooming through a few levels leaves dozens of tiles nobody
  // will see; unless they are cancelled, they hold the browser's six connections to this host and
  // the proxy's two download slots, and the tiles actually on screen — cached ones included — queue
  // behind them.
  private readonly inFlight = new WeakMap<HTMLElement, AbortController>()

  constructor(attribution: string | null) {
    super('/api/map/tiles/{z}/{x}/{y}', { maxZoom: 22, maxNativeZoom: 19, attribution: attribution ?? undefined })
    const cancel = (e: L.TileEvent) => this.inFlight.get(e.tile)?.abort()
    this.on('tileunload', cancel)
    this.on('tileabort', cancel)
  }

  createTile(coords: L.Coords, done: L.DoneCallback): HTMLElement {
    const img = document.createElement('img')
    img.alt = ''
    img.setAttribute('role', 'presentation')
    const controller = new AbortController()
    this.inFlight.set(img, controller)
    fetch(this.getTileUrl(coords), { signal: controller.signal })
      .then((r) => (r.ok ? r.blob() : Promise.reject(new Error(`tile ${r.status}`))))
      .then((blob) => {
        const url = URL.createObjectURL(blob)
        img.onload = () => {
          URL.revokeObjectURL(url)
          done(undefined, img)
        }
        img.onerror = () => {
          URL.revokeObjectURL(url)
          done(new Error('tile decode failed'), img)
        }
        img.src = url
      })
      .catch((err: Error) => {
        // A cancelled tile is gone from the map already; reporting it would only log an error.
        if (!controller.signal.aborted) done(err, img)
      })
      .finally(() => this.inFlight.delete(img))
    return img
  }
}

const NICE_STEPS_M = [0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000]

/**
 * A metric grid around a site's origin, for sites with no map underneath: something to read
 * distances off, and a visible origin so "where is zero" is never a question.
 */
export class MetricGridLayer extends L.GridLayer {
  private readonly origin: GeoPointDto

  constructor(origin: GeoPointDto) {
    super({ maxZoom: 24, tileSize: 256 })
    this.origin = origin
  }

  createTile(coords: L.Coords): HTMLElement {
    const canvas = document.createElement('canvas')
    const size = this.getTileSize()
    canvas.width = size.x
    canvas.height = size.y
    const map = this._map
    const ctx = canvas.getContext('2d')
    if (!map || !ctx) return canvas

    const nw = map.unproject(L.point(coords.x * size.x, coords.y * size.y), coords.z)
    const se = map.unproject(L.point((coords.x + 1) * size.x, (coords.y + 1) * size.y), coords.z)
    const a = toEnu(this.origin, { lat: nw.lat, lon: nw.lng })
    const b = toEnu(this.origin, { lat: se.lat, lon: se.lng })
    const metresPerPx = (b.east - a.east) / size.x
    const step = NICE_STEPS_M.find((s) => s / metresPerPx >= 60) ?? NICE_STEPS_M[NICE_STEPS_M.length - 1]

    const line = (x0: number, y0: number, x1: number, y1: number, axis: boolean) => {
      ctx.strokeStyle = axis ? 'rgba(255,255,255,0.45)' : 'rgba(255,255,255,0.10)'
      ctx.lineWidth = axis ? 1.5 : 1
      ctx.beginPath()
      ctx.moveTo(x0, y0)
      ctx.lineTo(x1, y1)
      ctx.stroke()
    }

    for (let e = Math.ceil(a.east / step) * step; e <= b.east; e += step) {
      const x = Math.round((e - a.east) / metresPerPx) + 0.5
      line(x, 0, x, size.y, Math.abs(e) < step / 2)
    }
    const metresPerPxY = (a.north - b.north) / size.y
    for (let n = Math.ceil(b.north / step) * step; n <= a.north; n += step) {
      const y = Math.round((a.north - n) / metresPerPxY) + 0.5
      line(0, y, size.x, y, Math.abs(n) < step / 2)
    }
    return canvas
  }
}
