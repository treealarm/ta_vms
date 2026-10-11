import type { CameraSetupDto } from '../dto/dtos'

/**
 * What a camera's marker says about it. Read from the camera list the rest of the app already
 * polls, so the map costs no request per camera.
 *
 * 'quiet' is deliberately not called offline: stream measurements exist only while media_server is
 * running a pipeline for the stream, and a camera nobody records or watches has none — it is not
 * known to be down, only not known to be up.
 */
export type CameraMapStatus = 'disabled' | 'quiet' | 'live' | 'recording'

export function cameraMapStatus(camera: CameraSetupDto | undefined): CameraMapStatus {
  if (!camera) return 'quiet'
  if (camera.disabled) return 'disabled'
  if ((camera.recordingMode ?? 'off') !== 'off') return 'recording'
  const measured = (camera.streams ?? []).some((s) => !s.disabled && (s.stats?.observedAtMs ?? 0) > 0)
  return measured ? 'live' : 'quiet'
}

/** Recording is red everywhere in the app (the card's frame); the rest follow the theme's meaning. */
export const STATUS_COLOR: Record<CameraMapStatus, string> = {
  disabled: '#616161',
  quiet: '#90a4ae',
  live: '#66bb6a',
  recording: '#ef5350',
}
