import { useState } from 'react'
import { Box, Button, Popover, Stack, Tooltip, Typography } from '@mui/material'
import { useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'

import type { WallEvent } from '../../store/wallEventsSlice'
import type { Segment } from '../../utils/archiveRanges'
import { msToX } from '../../utils/timelineTicks'
import { formatWallClock } from '../../utils/wallTime'
import type { WallRange } from '../../utils/wallTime'

const MIN_WIDTH_PX = 3
const STEM_H = 9
const DOT_PX = 6
const END_W = 2
// Below this the start dot and the end stroke would overlap into a blob — the event is a point at
// this zoom, so only the dot is drawn.
const MARKS_FIT_PX = DOT_PX + 3

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
 * Event markers: a filled dot where the event began, a vertical stroke where it ended, and a thin
 * rule joining them along the top.
 *
 * A filled bar would be easier to draw but hides the archive underneath and, more importantly,
 * loses the two facts that matter — where the event began and where it ended. The connecting line
 * is deliberately hairline: it has to make the span readable without competing with the coverage
 * bar below it.
 *
 * The two ends are different SHAPES rather than different colours. At this size (a 9px stem over a
 * 16px lane) shape is far more legible than hue, and the timeline already spends colour on three
 * other meanings — warning for recording-boundary ticks, primary for coverage and the playhead,
 * muted text for the ruler. A fourth hue would compete with all of them.
 *
 * Every mark is drawn only where it is actually true: a start that lies left of the window gets no
 * dot, an end past the right edge gets no stroke, and an end that is unknown — the episode is still
 * open, or its footage was cut short — gets none either, with the rule fading out instead of
 * stopping. An absent mark therefore always means "not here", never "off screen".
 */
export default function EventBrackets({ events, segments, win, pxWidth, onPick }: Props) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const [active, setActive] = useState<{ ev: WallEvent; anchor: HTMLElement } | null>(null)

  if (pxWidth <= 0) return null

  // The events tab pages backwards from a cursor, so landing on an old episode means asking for
  // the page that starts just after it — filtered to the camera, and told which row to select.
  const openInEvents = (ev: WallEvent) => {
    setActive(null)
    const qs = new URLSearchParams({
      cameraId: ev.cameraId,
      before: new Date(ev.startMs + 1).toISOString(),
      eventUuid: ev.eventUuid,
    })
    navigate(`/operator/events?${qs.toString()}`)
  }

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

        // "Where it began/ended is on screen", not "the bracket has an edge here" — the bracket is
        // clipped to the window, its marks are not.
        const startVisible = startX >= 0
        const endVisible = endX <= pxWidth
        const showDot = startVisible
        const showEnd = endVisible && !openEnded && !clipped.truncated && width >= MARKS_FIT_PX
        // No definite end to mark: let the rule run out instead of stopping dead, so the span does
        // not read as though it finished exactly where the drawing does.
        const fadeOut = openEnded || clipped.truncated || !endVisible

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
                setActive({ ev, anchor: e.currentTarget })
              }}
              sx={{
                position: 'absolute',
                top: 0,
                left: `${left}px`,
                width: `${width}px`,
                height: STEM_H,
                cursor: 'pointer',
                color: 'warning.main',
                '&:hover': { color: 'warning.light' },
              }}
            >
              {/* The joining rule, in currentColor so the whole marker lights up together on hover */}
              <Box
                sx={{
                  position: 'absolute',
                  top: 0,
                  left: 0,
                  right: 0,
                  height: '1px',
                  background: fadeOut
                    ? 'linear-gradient(to right, currentColor 55%, transparent)'
                    : 'currentColor',
                }}
              />

              {showDot && (
                <Box
                  sx={{
                    position: 'absolute',
                    top: 0,
                    left: 0,
                    width: DOT_PX,
                    height: DOT_PX,
                    borderRadius: '50%',
                    bgcolor: 'currentColor',
                  }}
                />
              )}

              {showEnd && (
                <Box
                  sx={{
                    position: 'absolute',
                    top: 0,
                    right: 0,
                    width: END_W,
                    height: STEM_H,
                    bgcolor: 'currentColor',
                  }}
                />
              )}
            </Box>
          </Tooltip>
        )
      })}

      {/* Clicking a bracket seeks there; this says what was actually clicked. Everything shown is
          already in the loaded event, so opening it costs no request. */}
      <Popover
        open={Boolean(active)}
        anchorEl={active?.anchor ?? null}
        onClose={() => setActive(null)}
        anchorOrigin={{ vertical: 'top', horizontal: 'center' }}
        transformOrigin={{ vertical: 'bottom', horizontal: 'center' }}
      >
        {active && (
          <Stack spacing={0.5} sx={{ p: 1.25, minWidth: 220 }}>
            <Typography variant="body2" fontWeight={600}>{active.ev.eventId}</Typography>
            <Typography variant="caption" color="text.secondary">
              {t('wall.eventStart')}: {formatWallClock(active.ev.startMs, { withDate: true })}
            </Typography>
            <Typography variant="caption" color="text.secondary">
              {t('wall.eventEnd')}:{' '}
              {active.ev.endMs === null
                ? t('wall.eventOngoing')
                : formatWallClock(active.ev.endMs, { withDate: true })}
            </Typography>
            {active.ev.objectId && (
              <Typography variant="caption" color="text.secondary">
                {t('wall.eventObject')}: {active.ev.objectId}
              </Typography>
            )}
            <Button size="small" sx={{ alignSelf: 'flex-start' }} onClick={() => openInEvents(active.ev)}>
              {t('wall.openInEvents')}
            </Button>
          </Stack>
        )}
      </Popover>
    </>
  )
}
