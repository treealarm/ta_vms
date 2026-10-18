import type { EdgeImportJobDto, EdgeIntervalDto } from '../dto/dtos'

/** Sub-tabs of the camera-side archive workbench. */
export type EdgeArchiveSection = 'browse' | 'recording' | 'storage'

/** One importable span: same recording and wall-clock range, any number of ONVIF tracks. */
export type CollapsedEdgeInterval = EdgeIntervalDto & {
  trackTokens: string[]
}

/**
 * The device reports an interval per track, but an import pulls one Replay URI per *recording* —
 * the RTSP session carries every track at once. Left uncollapsed, importing a range with
 * video/audio/metadata tracks would fetch the same footage three times.
 */
export function collapseIntervalsByRecordingSpan(
  intervals: EdgeIntervalDto[]
): CollapsedEdgeInterval[] {
  const groups = new Map<string, EdgeIntervalDto[]>()
  for (const iv of intervals) {
    const key = `${iv.recordingToken}|${iv.startTime}|${iv.endTime}`
    const existing = groups.get(key)
    if (existing) existing.push(iv)
    else groups.set(key, [iv])
  }

  const out: CollapsedEdgeInterval[] = []
  for (const group of groups.values()) {
    // Prefer the video track's row as the representative: its token is the one worth showing.
    const primary = [...group].sort(
      (a, b) => trackPreferRank(a.trackToken) - trackPreferRank(b.trackToken)
    )[0]
    out.push({ ...primary, trackTokens: group.map((iv) => iv.trackToken).filter(Boolean) })
  }
  return out.sort((a, b) => a.startTime.localeCompare(b.startTime))
}

function trackPreferRank(trackToken: string | undefined): number {
  const token = (trackToken ?? '').toLowerCase()
  if (token.includes('video')) return 0
  if (token.includes('audio')) return 1
  return 2
}

/** Fraction of the requested window already pulled, 0..1. Returns 0 when it cannot be computed. */
export function importProgressFraction(job: EdgeImportJobDto): number {
  const start = toMs(job.startTime)
  const end = toMs(job.endTime)
  const position = toMs(job.position)
  if (start === null || end === null || position === null || end <= start) return 0
  return clamp01((position - start) / (end - start))
}

/** Whether a job is still doing work — drives polling and the cancel button. */
export function isImportActive(job: EdgeImportJobDto): boolean {
  return job.state === 'running'
}

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const exponent = Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)))
  const value = bytes / Math.pow(1024, exponent)
  return `${value.toFixed(exponent === 0 ? 0 : 1)} ${units[exponent]}`
}

function toMs(iso: string | null | undefined): number | null {
  if (!iso) return null
  const ms = Date.parse(iso)
  return Number.isNaN(ms) ? null : ms
}

function clamp01(value: number): number {
  return Math.max(0, Math.min(1, value))
}
