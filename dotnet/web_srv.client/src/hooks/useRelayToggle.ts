import { useEffect, useRef, useState } from 'react'
import type { RelayMode } from '../dto/dtos'

// How long the switch keeps showing what was commanded when the camera has not confirmed it. The
// device's own report normally lands in a fraction of a second; the window is there so a command
// the camera never applies does not leave the switch lying indefinitely.
const CONFIRM_WINDOW_MS = 5000

// ONVIF has no operation to read a relay's current logical state — only its configured
// Mode/DelayTime/IdleState. The state itself arrives from the camera through device events, which
// is what `reported` is. The switch therefore follows the camera, and only for a moment follows
// the command: the old version kept a local boolean that started at "off", so a relay the camera
// said was on showed as off, and clicking "on" commanded a state it already had — nothing happened.
export function useRelayToggle(mode: RelayMode, delayMs: number, reported: boolean | null = null) {
  const [commanded, setCommanded] = useState<boolean | null>(null)
  const revertRef = useRef<ReturnType<typeof setTimeout> | null>(null)
  const settleRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  useEffect(() => () => {
    if (revertRef.current) clearTimeout(revertRef.current)
    if (settleRef.current) clearTimeout(settleRef.current)
  }, [])

  const active = commanded ?? reported ?? false

  const applyCommandedState = (next: boolean) => {
    if (revertRef.current) { clearTimeout(revertRef.current); revertRef.current = null }
    if (settleRef.current) { clearTimeout(settleRef.current); settleRef.current = null }
    setCommanded(next)

    if (next && mode === 'monostable' && delayMs > 0) {
      // The camera drops a monostable relay on its own clock; the guess goes when it would.
      revertRef.current = setTimeout(() => setCommanded(null), delayMs)
    } else {
      settleRef.current = setTimeout(() => setCommanded(null), CONFIRM_WINDOW_MS)
    }
  }

  return { active, applyCommandedState }
}

export async function setRelayOutputState(cameraId: string, token: string, active: boolean): Promise<void> {
  const r = await fetch(
    `/api/DeviceIo/cameras/${encodeURIComponent(cameraId)}/relay-outputs/${encodeURIComponent(token)}/state`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ active }),
    },
  )
  if (!r.ok) throw new Error(await r.text())
}
