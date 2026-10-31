import { useEffect, useState } from 'react'
import { mapFileUrl } from '../api/mapApi'

/**
 * A file row (`files`) as an object URL. The `<img>` cannot carry the Bearer token, so the bytes
 * are fetched through the authenticated wrapper and handed to the tag as a blob URL. A missing file
 * leaves the URL null rather than raising: a journal row without its picture is still a row.
 */
export function useMapFileImage(fileId: string | null | undefined): string | null {
  const [url, setUrl] = useState<string | null>(null)

  useEffect(() => {
    if (!fileId) return

    let cancelled = false
    let objectUrl: string | undefined

    const load = async () => {
      const response = await fetch(mapFileUrl(fileId))
      if (!response.ok) return
      objectUrl = URL.createObjectURL(await response.blob())
      if (!cancelled) setUrl(objectUrl)
    }

    void load().catch(() => {})
    return () => {
      cancelled = true
      if (objectUrl) URL.revokeObjectURL(objectUrl)
    }
  }, [fileId])

  return url
}
