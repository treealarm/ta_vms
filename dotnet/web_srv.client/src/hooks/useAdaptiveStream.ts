import { useEffect, useRef, useState, type RefObject } from 'react'
import type { CameraStreamDto } from '../dto/dtos'
import { pickStream } from '../utils/streamSelection'

/**
 * How long the target has to stay put before the slot acts on it. This is the time guard; the size
 * guard is the hysteresis inside pickStream, and neither substitutes for the other — hysteresis
 * does not help when a window edge is dragged clean across the range, and a debounce does not help
 * against jitter that settles right on a boundary.
 */
const SWITCH_DEBOUNCE_MS = 400

/**
 * Which live stream this slot should show, chosen from the slot's own rendered size.
 *
 * The size is read from the element's CSS box, deliberately without devicePixelRatio: the question
 * is how large the picture is laid out, not how many device pixels back it.
 */
export function useAdaptiveStream(
  containerRef: RefObject<HTMLElement | null>,
  candidates: CameraStreamDto[],
  enabled: boolean,
): string | null {
  const [size, setSize] = useState<{ width: number; height: number } | null>(null)
  const [chosen, setChosen] = useState<string | null>(null)
  // Read inside the debounce timer, so the timer never needs re-arming when only the answer moved.
  const chosenRef = useRef<string | null>(null)
  chosenRef.current = chosen

  useEffect(() => {
    const element = containerRef.current
    if (!element || !enabled) return

    const observer = new ResizeObserver((entries) => {
      const box = entries[0]?.contentRect
      if (box) setSize({ width: box.width, height: box.height })
    })
    observer.observe(element)
    return () => observer.disconnect()
  }, [containerRef, enabled])

  // Candidate identity, not the array: the catalogue is re-fetched every few seconds and hands back
  // fresh objects each time, which would otherwise restart the debounce for ever and never switch.
  const candidateKey = candidates
    .map((s) => `${s.streamName}:${s.stats?.width ?? 0}x${s.stats?.height ?? 0}:${s.declaredWidth ?? 0}`)
    .join('|')

  useEffect(() => {
    if (!enabled || !size) return

    const target = pickStream(candidates, size.width, size.height, chosenRef.current)
    if (target === null || target === chosenRef.current) return

    // Nothing on screen yet to protect, so the first answer is taken at once; only a change of mind
    // waits out the debounce.
    if (chosenRef.current === null) {
      setChosen(target)
      return
    }

    const timer = setTimeout(() => setChosen(target), SWITCH_DEBOUNCE_MS)
    return () => clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [enabled, size?.width, size?.height, candidateKey])

  return enabled ? chosen : null
}
