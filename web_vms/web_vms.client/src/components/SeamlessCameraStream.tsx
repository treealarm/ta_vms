import { Box } from '@mui/material'
import React, { useEffect, useRef, useState } from 'react'
import CameraStream from './CameraStream'
import type { DrawableBox } from './bbox/bboxDraw'

/**
 * How long a stream may warm up out of sight before it is shown regardless. If it never delivers,
 * showing it anyway is no worse than the plain behaviour — and CameraStream's own retry loop then
 * works on it. Holding the old picture instead would leave the tile on a stream nothing intends to
 * show again, with nobody left to retry.
 */
const WARMUP_TIMEOUT_MS = 8000

/**
 * At most this many tiles may hold two sessions at once. Leaving the grid's solo view re-evaluates
 * every tile at the same instant, and on a sixteen-camera wall an unbounded swap would briefly
 * double every stream on the page — the exact burst that makes the picture come apart (see the
 * capacity table in docs/handoff.md). Tiles that have to wait keep showing what they have, so
 * queueing costs nothing but time.
 */
const MAX_CONCURRENT_WARMUPS = 3

let activeWarmups = 0
const warmupQueue: (() => void)[] = []

function acquireWarmupSlot(): Promise<void> {
  if (activeWarmups < MAX_CONCURRENT_WARMUPS) {
    activeWarmups += 1
    return Promise.resolve()
  }
  return new Promise<void>((resolve) => warmupQueue.push(resolve))
}

function releaseWarmupSlot(): void {
  const next = warmupQueue.shift()
  if (next) {
    next()
    return
  }
  activeWarmups = Math.max(0, activeWarmups - 1)
}

interface Props {
  whepUrl: string
  cameraId?: string
  bboxEnabled?: boolean
  stunUrl?: string | null
  /**
   * Which stream is on screen right now, which during a swap is not yet the one that was asked
   * for. Anything describing the picture — a resolution readout, an overlay — has to follow this
   * and not the target, or it describes a stream the viewer cannot see yet.
   */
  onShownUrlChange?: (url: string) => void
  onFindSimilar?: (box: DrawableBox) => void
  onTrackObject?: (box: DrawableBox) => void
  trackingHighlight?: { source: string; trackId: number; kind: string } | null
}

/**
 * Shows a live stream, and changes which one without going black in between.
 *
 * The adaptive quality choice re-picks the stream whenever a tile is resized or expanded, and a
 * plain swap tears the session down before its replacement has a picture. Here the replacement is
 * negotiated behind the current one and only takes its place once it is actually delivering frames.
 *
 * Deliberately two whole CameraStream instances rather than two <video> elements inside one: an
 * instance already owns exactly one WHEP session and closes it on unmount, which is the part that
 * took real effort to get right and is not worth re-deriving here.
 */
const SeamlessCameraStream: React.FC<Props> = ({
  whepUrl,
  cameraId,
  bboxEnabled = false,
  stunUrl,
  onShownUrlChange,
  onFindSimilar,
  onTrackObject,
  trackingHighlight,
}) => {
  const [visibleUrl, setVisibleUrl] = useState(whepUrl)
  const [warmingUrl, setWarmingUrl] = useState<string | null>(null)
  const holdsSlotRef = useRef(false)

  const releaseSlot = () => {
    if (!holdsSlotRef.current) return
    holdsSlotRef.current = false
    releaseWarmupSlot()
  }

  // Derived rather than stored: a warm-up matters only while it is still the target and still
  // differs from what is on screen. Deriving it means no effect has to reset it, and a target that
  // flips back to the visible stream mid-warm-up simply stops counting.
  const pendingUrl =
    warmingUrl !== null && warmingUrl === whepUrl && warmingUrl !== visibleUrl ? warmingUrl : null

  useEffect(() => {
    if (whepUrl === visibleUrl) return

    let cancelled = false
    void acquireWarmupSlot().then(() => {
      if (cancelled) {
        releaseWarmupSlot()
        return
      }
      holdsSlotRef.current = true
      setWarmingUrl(whepUrl)
    })

    return () => {
      cancelled = true
      releaseSlot()
    }
  }, [whepUrl, visibleUrl])

  const promote = (url: string) => {
    setVisibleUrl(url)
    setWarmingUrl(null)
    releaseSlot()
  }

  useEffect(() => {
    if (!pendingUrl) return
    const timer = window.setTimeout(() => promote(pendingUrl), WARMUP_TIMEOUT_MS)
    return () => clearTimeout(timer)
  }, [pendingUrl])

  useEffect(() => releaseSlot, [])

  useEffect(() => {
    onShownUrlChange?.(visibleUrl)
    // Only when the shown stream actually changes — not on every render of the parent.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [visibleUrl])

  // One keyed list, not "a visible one plus a hidden one": promoting makes the same element change
  // style and position within the list, and React moves the existing DOM node rather than recreating
  // it. Rendering the two in separate slots of the tree would remount the warmed-up player on
  // promotion and throw away the very session this component exists to prepare.
  const urls = pendingUrl ? [visibleUrl, pendingUrl] : [visibleUrl]

  return (
    <Box sx={{ position: 'relative', width: '100%', height: '100%', display: 'flex' }}>
      {urls.map((url) => {
        const shown = url === visibleUrl
        return (
          <Box
            key={url}
            aria-hidden={!shown}
            sx={{
              position: 'absolute', inset: 0, display: 'flex',
              // Laid out at full size even while hidden: a display:none element is not guaranteed
              // to decode, and having real frames ready before it is shown is the whole point.
              opacity: shown ? 1 : 0,
              pointerEvents: shown ? 'auto' : 'none',
            }}
          >
            <CameraStream
              whepUrl={url}
              stunUrl={stunUrl}
              cameraId={shown ? cameraId : undefined}
              bboxEnabled={shown && bboxEnabled}
              onFirstFrames={shown ? undefined : () => promote(url)}
              onFindSimilar={shown ? onFindSimilar : undefined}
              onTrackObject={shown ? onTrackObject : undefined}
              trackingHighlight={shown ? trackingHighlight : undefined}
            />
          </Box>
        )
      })}
    </Box>
  )
}

export default SeamlessCameraStream
