import { apiFetch } from './apiFetch'
import type { DeviceArmMode } from '../dto/dtos'

/**
 * Arm or disarm one configured digital input of a camera. Only the mode moves here — the name,
 * the trigger and the alarm flag are the administrator's settings, saved through
 * `PUT .../settings`. `inherit` is not an operator's command, so the type excludes it.
 */
export async function setDigitalInputArmMode(
  cameraId: string,
  token: string,
  armMode: Extract<DeviceArmMode, 'armed' | 'disarmed'>,
): Promise<void> {
  await apiFetch(
    `/api/DeviceIo/cameras/${encodeURIComponent(cameraId)}/digital-inputs/${encodeURIComponent(token)}/arm-mode`,
    {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ armMode }),
    },
  )
}
