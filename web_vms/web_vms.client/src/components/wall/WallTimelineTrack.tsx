import { alpha, Box, Typography } from '@mui/material'

import type { Segment } from '../../utils/archiveRanges'
import { msToX } from '../../utils/timelineTicks'
import type { WallRange } from '../../utils/wallTime'

type Props = {
  label: string
  segments: readonly Segment[]
  win: WallRange
  pxWidth: number
  height: number
  emphasis?: boolean
  /** Lane has no camera behind it — drawn muted so it reads as inactive, not as "no archive". */
  disabled?: boolean
  children?: React.ReactNode
}

/** One coverage lane: a label and the recorded stretches inside the visible window. */
export default function WallTimelineTrack({
  label,
  segments,
  win,
  pxWidth,
  height,
  emphasis = false,
  disabled = false,
  children,
}: Props) {
  return (
    <Box sx={{ position: 'relative', height, mt: 0.25, opacity: disabled ? 0.4 : 1 }}>
      <Box
        sx={{
          position: 'absolute',
          inset: 0,
          bgcolor: (th) => alpha(th.palette.text.primary, 0.05),
          borderRadius: '2px',
          overflow: 'hidden',
        }}
      >
        {segments.map((seg, i) => {
          const left = msToX(Math.max(seg.startMs, win.startMs), win, pxWidth)
          const right = msToX(Math.min(seg.endMs, win.endMs), win, pxWidth)
          if (right <= 0 || left >= pxWidth) return null
          // Footage recovered from the camera's own storage is drawn hatched rather than in another
          // colour: the lane's colours already mean "this camera" and "the selected one", and a
          // third shade would read as a third camera instead of a different provenance.
          const recovered = seg.origin === 'edge'
          return (
            <Box
              key={`${seg.startMs}-${i}`}
              title={recovered ? 'Recovered from the camera' : undefined}
              sx={{
                position: 'absolute',
                top: 0,
                bottom: 0,
                left: `${left}px`,
                // Always at least a hairline, so a short recording never vanishes entirely.
                width: `${Math.max(1, right - left)}px`,
                bgcolor: emphasis ? 'primary.main' : 'primary.light',
                opacity: emphasis ? 0.85 : 0.5,
                backgroundImage: recovered
                  ? 'repeating-linear-gradient(45deg, rgba(255,255,255,.55) 0 3px, transparent 3px 7px)'
                  : undefined,
              }}
            />
          )
        })}
        {children}
      </Box>

      <Typography
        sx={{
          position: 'absolute',
          left: 4,
          top: '50%',
          transform: 'translateY(-50%)',
          fontSize: '0.58rem',
          lineHeight: 1,
          color: 'text.secondary',
          pointerEvents: 'none',
          textShadow: '0 0 3px var(--app-video-bg, rgba(0,0,0,.6))',
          whiteSpace: 'nowrap',
          maxWidth: '60%',
          overflow: 'hidden',
          textOverflow: 'ellipsis',
        }}
      >
        {label}
      </Typography>
    </Box>
  )
}
