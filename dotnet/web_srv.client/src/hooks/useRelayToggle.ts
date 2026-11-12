// ONVIF has no operation to read a relay's current logical state — only its configured
// Mode/DelayTime/IdleState. The state itself arrives from the camera through device events.
//
// The switch has no guess of its own: VmsOnvif is the one place that knows both the operator's
// wish and the camera's report, and it answers with the wish until the camera confirms it. A local
// guess is what used to flip a relay back on: it outlived the command, or a late event overwrote
// the cache it was reading.
export function useRelayToggle(reported: boolean | null = null) {
  return { active: reported ?? false }
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
