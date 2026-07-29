import { Box, Tooltip } from '@mui/material'

import type { WallEvent } from '../../store/wallEventsSlice'
import type { Segment } from '../../utils/archiveRanges'
import { msToX } from '../../utils/timelineTicks'
import { formatWallClock } from '../../utils/wallTime'
import type { WallRange } from '../../utils/wallTime'

const MIN_WIDTH_PX = 3
const STEM_H = 7

type Props = {
  events: readonly WallEvent[]
  /** Archive coverage of the same camera; brackets are clipped to it. */
  segments: readonly Segment[]
  win: WallRange
  pxWidth: number
  onPick: (ms: number) => void
}

/**
 * Trims an event to the recording that actually exists for it.
 *
 * `alarm_end_ts` is the PLANNED end of retention (alarm start + the watch's timeout), not the last
 * recorded frame. When recording stopped early — camera dropped, service restarted — the stored
 * event outlives its own footage, and drawing it verbatim puts a bracket over empty timeline.
 * Returns null when the event has no footage at all.
 */
function clipToCoverage(
  ev: WallEvent,
  segments: readonly Segment[],
  fallbackEndMs: number
): { startMs: number; endMs: number; truncated: boolean } | null {
  const rawEnd = ev.endMs ?? fallbackEndMs
  let startMs = Number.POSITIVE_INFINITY
  let endMs = Number.NEGATIVE_INFINITY

  for (const seg of segments) {
    if (seg.endMs <= ev.startMs || seg.startMs >= rawEnd) continue
    startMs = Math.min(startMs, Math.max(seg.startMs, ev.startMs))
    endMs = Math.max(endMs, Math.min(seg.endMs, rawEnd))
  }

  if (!Number.isFinite(startMs) || !Number.isFinite(endMs) || endMs <= startMs) return null
  return { startMs, endMs, truncated: endMs < rawEnd - 1000 }
}

/**
 * Event markers drawn as brackets: a vertical tick at the start, one at the end, and a thin rule
 * joining them along the top.
 *
 * A filled bar would be easier to draw but hides the archive underneath and, more importantly,
 * loses the two facts that matter — where the event began and where it ended. The connecting line
 * is deliberately hairline: it has to make the span readable without competing with the coverage
 * bar below it.
 */
export default function EventBrackets({ events, segments, win, pxWidth, onPick }: Props) {
  if (pxWidth <= 0) return null

  return (
    <>
      {events.map((ev) => {
        const clipped = clipToCoverage(ev, segments, win.endMs)
        if (!clipped) return null

        const startX = msToX(clipped.startMs, win, pxWidth)
        const endX = msToX(clipped.endMs, win, pxWidth)
        if (endX < 0 || startX > pxWidth) return null

        const left = Math.max(0, startX)
        const width = Math.max(MIN_WIDTH_PX, Math.min(pxWidth, endX) - left)
        const openEnded = ev.endMs === null

        const shown = `${formatWallClock(clipped.startMs, { withDate: false })} → ${
          openEnded ? '…' : formatWallClock(clipped.endMs, { withDate: false })
        }`
        // Say so when the bracket is shorter than the stored event: the operator should not have to
        // wonder why a 41-second alarm shows 20 seconds of video.
        const title = clipped.truncated && !openEnded
          ? `${shown} (${formatWallClock(ev.endMs!, { withDate: false })})`
          : shown

        return (
          <Tooltip key={ev.eventUuid} title={title} placement="top">
            <Box
              // Handled on pointerup, not click: the timeline seeks on its own pointerup, which
              // fires before any click event — stopping propagation there is too late, and the wall
              // would take two seeks and rebuild every cell's pipeline twice.
              onPointerUp={(e) => {
                e.stopPropagation()
                onPick(clipped.startMs)
              }}
              sx={{
                position: 'absolute',
                top: 0,
                left: `${left}px`,
                width: `${width}px`,
                height: STEM_H,
                cursor: 'pointer',
                // The joining rule.
                borderTop: '1px solid',
                borderColor: 'warning.main',
                // Start tick; the end tick is omitted while the episode is still open, or where the
                // footage was cut short — there is no real end to mark in either case.
                borderLeft: '2px solid',
                borderRight: openEnded || clipped.truncated ? 'none' : '2px solid',
                boxSizing: 'border-box',
                '&:hover': { borderColor: 'warning.light' },
              }}
            />
          </Tooltip>
        )
      })}
    </>
  )
}
