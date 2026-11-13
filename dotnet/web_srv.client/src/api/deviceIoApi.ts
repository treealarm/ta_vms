import { apiFetch } from './apiFetch'
import type { DeviceArmMode } from '../dto/dtos'

/**
 * The arming of one configured digital input: an explicit on or off, which outranks the camera
 * and the zone it stands in, or `inherit`, which hands it back to them. Only the mode moves here —
 * the name, the trigger and the alarm flag are the administrator's settings, saved through
 * `PUT .../settings`.
 */
export async function setDigitalInputArmMode(
  cameraId: string,
  token: string,
  armMode: DeviceArmMode,
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
