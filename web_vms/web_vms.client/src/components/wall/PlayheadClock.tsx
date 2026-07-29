import { useEffect, useState } from 'react'
import { Tooltip, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'

import { wallClockBus } from '../../hooks/wallClockBus'
import { formatWallClock } from '../../utils/wallTime'

/**
 * The one component allowed to re-render at playhead rate. Everything else on the wall reads the
 * playhead through a ref so a moving clock does not repaint the video cells.
 *
 * Clicking copies the instant — an operator writing an incident report needs the exact timestamp,
 * and retyping it off the screen is how the wrong second ends up in the report.
 */
export default function PlayheadClock({ fallbackMs }: { fallbackMs: number }) {
  const { t } = useTranslation()
  const [playheadMs, setPlayheadMs] = useState<number | null>(() => wallClockBus.getPlayheadMs())
  const [copied, setCopied] = useState(false)

  useEffect(() => wallClockBus.subscribePlayhead(setPlayheadMs), [])

  useEffect(() => {
    if (!copied) return
    const timer = window.setTimeout(() => setCopied(false), 1200)
    return () => clearTimeout(timer)
  }, [copied])

  const text = formatWallClock(playheadMs ?? fallbackMs)

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text)
      setCopied(true)
    } catch {
      // Clipboard is unavailable outside a secure context — nothing useful to do but stay quiet.
    }
  }

  return (
    <Tooltip title={copied ? t('wall.copied') : t('wall.copyTime')}>
      <Typography
        onClick={() => void copy()}
        sx={{
          fontSize: '0.78rem',
          fontVariantNumeric: 'tabular-nums',
          whiteSpace: 'nowrap',
          cursor: 'pointer',
          px: 0.5,
          borderRadius: '3px',
          '&:hover': { bgcolor: 'action.hover' },
        }}
      >
        {text}
      </Typography>
    </Tooltip>
  )
}
