import type { CameraStreamDto } from '../dto/dtos'
import { streamSize } from '../dto/dtos'

/**
 * One line describing what a stream is actually sending: resolution, codec, frame rate, bitrate.
 *
 * Only measured values are shown as such. A declared resolution is marked with a trailing "?" —
 * it comes from the camera's ONVIF profile and can be out of date, and presenting it as a
 * measurement would be a quiet lie. An em dash means nothing is known at all yet.
 *
 * A trailing "→ h264 (vaapi)" appears only while the stream is actually being re-compressed for
 * WebRTC. Its absence is not "we do not know": a camera that already sends H.264 is passed
 * through untouched, and that is the normal, cheaper case.
 */
export function formatStreamParams(stream: CameraStreamDto): string {
  const size = streamSize(stream)
  const stats = stream.stats

  const parts: string[] = []
  if (size) parts.push(`${size.width}×${size.height}${size.observed ? '' : ' ?'}`)
  if (stats?.codec) parts.push(stats.codec)
  if (stats && stats.fps > 0) parts.push(`${stats.fps.toFixed(0)} fps`)
  if (stats && stats.bitrateBps > 0) parts.push(formatBitrate(stats.bitrateBps))
  if (stats?.transcodeEncoder) parts.push(`→ h264 (${stats.transcodeEncoder})`)

  return parts.length > 0 ? parts.join(' · ') : '—'
}

function formatBitrate(bps: number): string {
  if (bps >= 1_000_000) return `${(bps / 1_000_000).toFixed(2)} Mbps`
  return `${Math.round(bps / 1000)} kbps`
}
