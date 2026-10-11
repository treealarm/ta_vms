import { useEffect, useState } from 'react'

/**
 * A plan space's picture as an object URL. Fetched with the app's authenticated fetch, which an
 * <img src> cannot do, and keyed by the plan's ETag so a re-uploaded plan is never served from the
 * browser's cache under the old address.
 */
export function useSpacePlanImage(spaceId: string | null | undefined, etag: string | null | undefined): string | null {
  const key = spaceId && etag ? `${spaceId}?v=${encodeURIComponent(etag)}` : null
  const [loaded, setLoaded] = useState<{ key: string; url: string } | null>(null)

  useEffect(() => {
    if (!key) return
    let objectUrl: string | null = null
    let cancelled = false
    fetch(`/api/spatial/spaces/${key.replace('?v=', '/plan?v=')}`)
      .then((r) => (r.ok ? r.blob() : Promise.reject(new Error(String(r.status)))))
      .then((blob) => {
        if (cancelled) return
        objectUrl = URL.createObjectURL(blob)
        setLoaded({ key, url: objectUrl })
      })
      .catch(() => undefined)
    return () => {
      cancelled = true
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [key])

  // Whatever was loaded for another space or an older image is not this one.
  return loaded && loaded.key === key ? loaded.url : null
}
