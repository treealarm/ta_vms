import { useEffect, useRef, useState } from 'react'
import { Alert, Box, CircularProgress, Stack, Typography } from '@mui/material'
import Hls from 'hls.js'
import { useTranslation } from 'react-i18next'

import { authXhrSetup } from '../auth/authFetch'
import type { EdgeExportLinkDto } from '../dto/dtos'

/** What the preview is asked to show. */
export type EdgePreviewTarget =
  /** A range already written into this VMS archive, played through the normal archive HLS path. */
  | { kind: 'archive'; cameraId: string; from: string; to: string; label: string }
  /** An exported mp4 sitting under VMS_EXPORT_BASE_PATH. */
  | { kind: 'file'; relPath: string; label: string }

/**
 * Plays what an import produced without leaving the page — an archive range through the same HLS
 * endpoint the wall player uses, an exported file directly.
 */
export default function EdgeMediaPreview({ target }: { target: EdgePreviewTarget }) {
  const { t } = useTranslation()
  const videoRef = useRef<HTMLVideoElement>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(false)

  const key = target.kind === 'archive'
    ? `archive|${target.cameraId}|${target.from}|${target.to}`
    : `file|${target.relPath}`

  useEffect(() => {
    const video = videoRef.current
    if (!video) return

    setError(null)
    const tryPlay = () => void video.play().catch(() => { /* autoplay may be blocked; controls remain */ })

    if (target.kind === 'archive') {
      const url = `/api/arch/hls/preview.m3u8?cameraId=${encodeURIComponent(target.cameraId)}`
        + `&from=${encodeURIComponent(target.from)}&to=${encodeURIComponent(target.to)}`

      if (Hls.isSupported()) {
        // xhrSetup carries the bearer token: hls.js fetches segments over XHR, which the global
        // fetch wrapper does not touch.
        const hls = new Hls({ enableWorker: true, lowLatencyMode: false, xhrSetup: authXhrSetup })
        hls.on(Hls.Events.MEDIA_ATTACHED, () => hls.loadSource(url))
        hls.on(Hls.Events.MANIFEST_PARSED, tryPlay)
        hls.on(Hls.Events.ERROR, (_, data) => {
          if (data.fatal) setError(`${data.type}: ${data.details ?? ''}`)
        })
        hls.attachMedia(video)
        return () => { hls.destroy() }
      }
      if (video.canPlayType('application/vnd.apple.mpegurl')) {
        video.src = url
        video.onerror = () => setError(t('edge.playbackFailed'))
        return () => { video.onerror = null }
      }
      setError(t('edge.hlsUnsupported'))
      return
    }

    // A plain <video src> sends no Authorization header, so the grant travels in a short-lived
    // signed URL instead. That keeps playback progressive and seekable over Range — fetching the
    // whole file into a blob would download all of it before the first frame.
    let cancelled = false
    setLoading(true)
    void (async () => {
      try {
        const response = await fetch(
          `/api/edge-archive/export-link?relPath=${encodeURIComponent(target.relPath)}`,
          { method: 'POST' }
        )
        if (!response.ok) throw new Error(`HTTP ${response.status}`)
        const link = (await response.json()) as EdgeExportLinkDto
        if (cancelled) return
        video.src = link.url
        tryPlay()
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : t('edge.playbackFailed'))
      } finally {
        if (!cancelled) setLoading(false)
      }
    })()

    return () => { cancelled = true }
    // `key` collapses the target into a primitive so a re-render with an equal object does not
    // restart playback.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, t])

  return (
    <Box>
      <Stack direction="row" spacing={1} alignItems="center" sx={{ mb: 0.5 }}>
        <Typography variant="caption" color="text.secondary">{target.label}</Typography>
        {loading && <CircularProgress size={14} />}
      </Stack>
      {error && <Alert severity="error" sx={{ mb: 1 }}>{error}</Alert>}
      <video
        ref={videoRef}
        controls
        playsInline
        style={{ width: '100%', maxHeight: 360, background: '#000' }}
      />
    </Box>
  )
}
