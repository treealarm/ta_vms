import { useEffect, useRef, useState } from 'react'
import type { RelayMode } from '../dto/dtos'

// ONVIF has no operation to read a relay's current logical (active/inactive) state — only its
// configured Mode/DelayTime/IdleState (GetRelayOutputs never carries a live state field, see
// RelayOutputSettings in onvif.xsd). So the displayed on/off state can't be reconciled against the
// camera; it's tracked purely client-side: set optimistically the moment the command succeeds,
// and for monostable relays auto-reverted after delayMs via a local timer that mirrors the
// camera's own auto-revert, never re-fetched from the server.
export function useRelayToggle(mode: RelayMode, delayMs: number) {
  const [active, setActive] = useState(false)
  const timerRef = useRef<ReturnType<typeof setTimeout> | null>(null)

  useEffect(() => () => {
    if (timerRef.current) clearTimeout(timerRef.current)
  }, [])

  const applyCommandedState = (next: boolean) => {
    if (timerRef.current) { clearTimeout(timerRef.current); timerRef.current = null }
    setActive(next)
    if (next && mode === 'monostable' && delayMs > 0) {
      timerRef.current = setTimeout(() => setActive(false), delayMs)
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
