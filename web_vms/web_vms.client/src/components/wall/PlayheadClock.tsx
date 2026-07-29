import { useEffect, useState } from 'react'
import { Typography } from '@mui/material'

import { wallClockBus } from '../../hooks/wallClockBus'
import { formatWallClock } from '../../utils/wallTime'

/**
 * The one component allowed to re-render at playhead rate. Everything else on the wall reads the
 * playhead through a ref so a moving clock does not repaint the video cells.
 */
export default function PlayheadClock({ fallbackMs }: { fallbackMs: number }) {
  const [playheadMs, setPlayheadMs] = useState<number | null>(() => wallClockBus.getPlayheadMs())

  useEffect(() => wallClockBus.subscribePlayhead(setPlayheadMs), [])

  return (
    <Typography sx={{ fontSize: '0.78rem', fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' }}>
      {formatWallClock(playheadMs ?? fallbackMs)}
    </Typography>
  )
}
