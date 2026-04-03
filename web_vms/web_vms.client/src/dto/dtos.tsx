export const toIsoUtc = (value: string) => {
  const date = new Date(value)
  return isNaN(date.getTime()) ? null : date.toISOString()
}

export function toLocalDatetimeInputValue(date: Date) {
  const pad = (n: number) => n.toString().padStart(2, '0')

  return (
    date.getFullYear() +
    '-' +
    pad(date.getMonth() + 1) +
    '-' +
    pad(date.getDate()) +
    'T' +
    pad(date.getHours()) +
    ':' +
    pad(date.getMinutes()) +
    ':' +
    pad(date.getSeconds())
  )
}

// sessionId.ts
export function getTabId(): string {
  const key = 'tab_id'

  let tabId = sessionStorage.getItem(key)
  if (!tabId) {
    tabId = crypto.randomUUID()
    sessionStorage.setItem(key, tabId)
  }

  return tabId
}

export type ApiResponseDto = {
  success: boolean
  message: string
}

export type PlayerStateDto = {
  camera_id: string
  client_id: string
  position_ts?: string
  position_ms?: number
}

export type PlayerCommand = 'play' | 'pause' | 'stop' | 'step' | 'step_back' | 'step_forward'

export type PlayerMode = 'unspecified' | 'playing' | 'paused' | 'stopped'

export type PlayerCommandDto = {
  client_id: string
  command: PlayerCommand
  step_frames?: number
  speed?: number
}

export type PlayerProgressStateDto = {
  camera_id: string
  client_id: string
  position_ts: string
  file_path: string
  start_ts: string
  duration_ms: number
  position_ms:number
  mode: PlayerMode
}

export type CameraStateDto = {
  cameraId: string
  recording: boolean
  webRtc: boolean
  rtspUrl: string
}

export type RecordingDto = {
  filePath: string
  startTime: string
}

export type RecordingsQueryDto = {
  cameraId: string
  timestamp: string
}

export type RecordingsResponseDto = {
  recordings: RecordingDto[]
}

export type AddCameraRequestDto = {
  cameraId: string
  rtspUrl: string
  state: Pick<CameraStateDto, 'cameraId' | 'recording' | 'webRtc'>
}


export type UpdateCameraStateRequestDto = Pick<CameraStateDto, 'cameraId' | 'recording' | 'webRtc'>
