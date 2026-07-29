// The wall's shared playhead.
//
// Exactly one cell — the clock OWNER — drives it; every other cell follows. The playhead moves
// several times a second, so it deliberately lives outside Redux: dispatching it would re-render
// every cell on the wall at that rate. Subscribers get a coalesced callback and are expected to
// keep the value in a ref, not in state.
//
// Ownership rules (a focused camera with no archive must never freeze the wall):
//  - a cell registers as a CANDIDATE only once it actually has media to play;
//  - the focused cell owns the clock if it is a candidate;
//  - otherwise the longest-registered candidate owns it.

type PlayheadListener = (wallMs: number) => void
type OwnerListener = (ownerKey: string | null) => void

const PUBLISH_COALESCE_MS = 250

class WallClockBus {
  /** slotKey → refcount. Refcounted because StrictMode mounts effects twice. */
  private candidates = new Map<string, number>()
  /** slotKey → registration sequence, for a stable "first candidate" order. */
  private sequence = new Map<string, number>()
  private nextSeq = 0

  private focusedKey: string | null = null
  private ownerKey: string | null = null

  private playheadMs: number | null = null
  private lastEmitAt = 0
  private pendingEmit?: number

  private playheadListeners = new Set<PlayheadListener>()
  private ownerListeners = new Set<OwnerListener>()

  registerCandidate(slotKey: string): () => void {
    const n = (this.candidates.get(slotKey) ?? 0) + 1
    this.candidates.set(slotKey, n)
    if (n === 1) {
      this.sequence.set(slotKey, this.nextSeq++)
      this.recomputeOwner()
    }

    let released = false
    return () => {
      if (released) return
      released = true
      const cur = this.candidates.get(slotKey)
      if (cur === undefined) return
      if (cur <= 1) {
        this.candidates.delete(slotKey)
        this.sequence.delete(slotKey)
        this.recomputeOwner()
      } else {
        this.candidates.set(slotKey, cur - 1)
      }
    }
  }

  setFocused(slotKey: string | null): void {
    if (this.focusedKey === slotKey) return
    this.focusedKey = slotKey
    this.recomputeOwner()
  }

  getOwner(): string | null {
    return this.ownerKey
  }

  isOwner(slotKey: string): boolean {
    return this.ownerKey === slotKey
  }

  subscribeOwner(cb: OwnerListener): () => void {
    this.ownerListeners.add(cb)
    cb(this.ownerKey)
    return () => {
      this.ownerListeners.delete(cb)
    }
  }

  /** Ignored unless `slotKey` currently owns the clock — slaves cannot move the wall. */
  publish(slotKey: string, wallMs: number): void {
    if (this.ownerKey !== slotKey || !Number.isFinite(wallMs)) return
    this.playheadMs = wallMs
    this.scheduleEmit()
  }

  getPlayheadMs(): number | null {
    return this.playheadMs
  }

  subscribePlayhead(cb: PlayheadListener): () => void {
    this.playheadListeners.add(cb)
    if (this.playheadMs !== null) cb(this.playheadMs)
    return () => {
      this.playheadListeners.delete(cb)
    }
  }

  /** Hard reset on a committed seek: the playhead jumps immediately instead of easing over. */
  reset(seekMs: number): void {
    if (!Number.isFinite(seekMs)) return
    this.playheadMs = seekMs
    this.emitNow()
  }

  private recomputeOwner(): void {
    let next: string | null = null

    if (this.focusedKey && this.candidates.has(this.focusedKey)) {
      next = this.focusedKey
    } else {
      let bestSeq = Infinity
      for (const key of this.candidates.keys()) {
        const seq = this.sequence.get(key) ?? Infinity
        if (seq < bestSeq) {
          bestSeq = seq
          next = key
        }
      }
    }

    if (next === this.ownerKey) return
    this.ownerKey = next
    for (const cb of this.ownerListeners) cb(next)
  }

  private scheduleEmit(): void {
    const now = Date.now()
    if (now - this.lastEmitAt >= PUBLISH_COALESCE_MS) {
      this.emitNow()
      return
    }
    if (this.pendingEmit !== undefined) return
    this.pendingEmit = window.setTimeout(
      () => this.emitNow(),
      PUBLISH_COALESCE_MS - (now - this.lastEmitAt)
    )
  }

  private emitNow(): void {
    if (this.pendingEmit !== undefined) {
      clearTimeout(this.pendingEmit)
      this.pendingEmit = undefined
    }
    this.lastEmitAt = Date.now()
    const value = this.playheadMs
    if (value === null) return
    for (const cb of this.playheadListeners) cb(value)
  }
}

export const wallClockBus = new WallClockBus()
