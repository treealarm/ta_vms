import type { CameraStreamDto } from '../dto/dtos'
import { streamSize } from '../dto/dtos'

/**
 * Fraction of the slot width the size must move past a boundary before the choice changes, and the
 * floor for that in pixels. Together they stop a slow resize from flipping the stream back and
 * forth across a boundary such as 640/720, each flip costing a WebRTC renegotiation.
 */
const HYSTERESIS_FRACTION = 0.12
const HYSTERESIS_MIN_PX = 80

/**
 * Which stream a slot of this size should show.
 *
 * `slotWidth`/`slotHeight` are CSS pixels and must NOT be multiplied by devicePixelRatio: on a
 * HiDPI screen that would ask for a stream twice the size the layout actually needs, which is the
 * opposite of the point.
 *
 * The rule, in order:
 *  - the smallest stream that covers the slot in both dimensions;
 *  - failing that, the widest one — a little upscaling beats a sub-stream blown up;
 *  - with fewer than two streams of known size, the previous behaviour: the first candidate.
 *    A single measurement is nothing to compare against, so there is no choice to make.
 *
 * Returns null when there are no candidates at all.
 */
export function pickStream(
  candidates: CameraStreamDto[],
  slotWidth: number,
  slotHeight: number,
  current: string | null,
): string | null {
  if (candidates.length === 0) return null

  const sized = candidates.filter((s) => streamSize(s) !== null)
  if (sized.length < 2 || slotWidth <= 0 || slotHeight <= 0)
    return candidates[0].streamName

  // A pinned or previously chosen stream that no longer exists must not hold the slot hostage.
  const currentIsCandidate = current !== null && candidates.some((s) => s.streamName === current)
  if (!currentIsCandidate) return bestFor(sized, slotWidth, slotHeight)

  // Only move when the answer is the same across the whole hysteresis band. Sitting on a boundary
  // the two ends disagree, and the slot keeps what it has.
  const margin = Math.max(HYSTERESIS_MIN_PX, slotWidth * HYSTERESIS_FRACTION)
  const scale = (slotWidth + margin) / slotWidth
  const low = bestFor(sized, slotWidth - margin, slotHeight / scale)
  const high = bestFor(sized, slotWidth + margin, slotHeight * scale)

  return low === high ? low : current
}

function bestFor(
  sized: CameraStreamDto[],
  slotWidth: number,
  slotHeight: number,
): string {
  const withSize = sized.map((s) => ({ name: s.streamName, size: streamSize(s)! }))

  const covering = withSize.filter((c) => c.size.width >= slotWidth && c.size.height >= slotHeight)
  if (covering.length > 0) {
    // Smallest by area: the cheapest picture that still does not need upscaling.
    return covering.reduce((a, b) =>
      a.size.width * a.size.height <= b.size.width * b.size.height ? a : b).name
  }

  return withSize.reduce((a, b) => (a.size.width >= b.size.width ? a : b)).name
}
