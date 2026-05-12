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
  position_ms?: number|null
}

export type PlayerCommand = 'play' | 'pause' | 'stop' | 'step' | 'step_back' | 'step_forward'

export type PlayerMode = 'unspecified' | 'playing' | 'paused' | 'stopped'

export type PlayerCommandDto = {
  camera_id: string
  client_id: string
  command: PlayerCommand
  step_frames?: number
  speed?: number
}

export type PlayerProgressStateDto = {
  camera_id: string
  client_id: string
  position_ts?: string
  file_path?: string
  start_ts?: string
  duration_ms: number
  position_ms: number
  mode: PlayerMode
}

export type CredentialsDto = {
  user: string
  pass: string
}

export type OnvifConnectionDto = {
  ip: string
  port: number
}

export type RecordingMode = 'off' | 'continuous' | 'prealarm'

export type RecordingProfileDto = {
  id: string
  name: string
  mode: RecordingMode
  preRecordSeconds: number
  postRecordSeconds: number
  segmentSeconds: number
}

export type CameraStreamDto = {
  streamName: string
  profileToken: string
  rtspUrl: string
  webRtc: boolean
  recordingProfile?: RecordingProfileDto
}

export type CameraSetupDto = {
  cameraId: string
  serverId?: string
  /** Derived from first stream's profile mode */
  recording?: boolean
  recordingMode: RecordingMode
  webRtc: boolean
  rtspUrl: string
  cred?: CredentialsDto
  onvifConn?: OnvifConnectionDto
  /** True while camera is inside a retention episode from FS (wall clock in [retain_from, retain_until]); new clips ingest as retained */
  activeRetentionWindow?: boolean
  streams?: CameraStreamDto[]
}

export type MediaServerNodeDto = {
  serverId: string
  status: string
}

/** Built client-side from servers + cameras for Settings tree */
export type ServerInstanceDto = {
  serverId: string
  address: string
  cameras: CameraSetupDto[]
}

export type RecordingDto = {
  filePath: string
  streamName?: string
  startTime: string
  endTime?: string
  sizeBytes: number
  clipUid: string
}

export type RecordingRangeDto = {
  recordings: RecordingDto[]
}

export type RecordingRangesResponseDto = {
  ranges: RecordingRangeDto[]
}

export type RecordingsQueryDto = {
  cameraId: string
  streamName?: string
  timestamp?: string
  fromUtc?: string
  toUtc?: string
  limit?: number
}

export type RecordingsResponseDto = {
  recordings: RecordingDto[]
  nearest?: RecordingDto
}

export type RetentionEventDto = {
  eventId?: string
  cameraId: string
  alarmStartTs: string
  alarmEndTs?: string
  preRecordSeconds?: number
  postRecordSeconds?: number
  timeoutSeconds?: number
}

export type RetentionEventRowDto = {
  eventId: string
  cameraId: string
  alarmStartTs: string
  alarmEndTs?: string
}

export type RetentionEventsListDto = {
  events: RetentionEventRowDto[]
  hasMore: boolean
  nextBeforeAlarmStart?: string
}

export type StorageDto = {
  storageId: string
  rootPath: string
  reservedBytes: number
}

export type CameraStorageAssignmentDto = {
  cameraId: string
  storageId: string
  priority: number
}

export type LogQueryDto = {
  from?: string
  to?: string
  limit?: number
  level?: string
  source?: string
}

export type LogEntryDto = {
  timestamp: string
  level: string
  file: string
  function: string
  message: string
}

export type LogResponseDto = {
  logs: LogEntryDto[]
  hasMore: boolean
}
