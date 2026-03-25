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

export type ApiResponseDto = {
  success: boolean
  message: string
}

export type PlayerStateDto = {
  cameraId: string
  clientId: string
  position: string
  filePath: string
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
  endTime?: string
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

export type AddPlayerRequestDto = {
  cameraId: string
  clientId: string
  position: string
}

export type UpdateCameraStateRequestDto = Pick<CameraStateDto, 'cameraId' | 'recording' | 'webRtc'>

export type SeekPlayerRequestDto = {
  cameraId: string
  clientId: string
  position: string
  filePath?: string
}
