import { alpha } from '@mui/material'
import type { Theme } from '@mui/material'

/** How one stretch of coverage is drawn, whatever lane or legend sample draws it. */
export type SegmentMark = {
  emphasis?: boolean
  recovered?: boolean
  recompressed?: boolean
}

/**
 * Fill, hatch and band for a recorded stretch. The lanes and the legend both call this, so an
 * operator reading the legend is reading exactly what the lanes draw.
 */
export function segmentSx({ emphasis = false, recovered = false, recompressed = false }: SegmentMark = {}) {
  return {
    bgcolor: emphasis ? 'primary.main' : 'primary.light',
    opacity: emphasis ? 0.85 : 0.5,
    // Recovered footage is hatched rather than recoloured: the fill already carries camera
    // identity, and a third shade would read as a third camera instead of a different provenance.
    backgroundImage: recovered
      ? 'repeating-linear-gradient(45deg, rgba(255,255,255,.55) 0 3px, transparent 3px 7px)'
      : undefined,
    // A band along the bottom, because a stretch can be both recovered and re-encoded.
    borderBottom: recompressed ? '2px solid' : undefined,
    borderBottomColor: recompressed ? 'warning.main' : undefined,
  }
}

/** The coverage request failed: the lane carries no data, and the warning hatch says why. */
export const unavailableSx = (theme: Theme) => ({
  backgroundImage: `repeating-linear-gradient(-45deg, ${alpha(theme.palette.warning.main, 0.45)} 0 4px, transparent 4px 10px)`,
})

/**
 * No track for this camera in an otherwise successful response. Vertical bars, unlike both
 * diagonal hatches, so "we did not hear back" cannot be read as recovered footage.
 */
export const noAnswerSx = (theme: Theme) => ({
  backgroundImage: `repeating-linear-gradient(90deg, ${alpha(theme.palette.text.primary, 0.3)} 0 2px, transparent 2px 8px)`,
})
