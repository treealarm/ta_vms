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
  xaddr?: string
}

export type DiscoveredDeviceDto = {
  ip: string
  port: number
  endpointUuid: string
  xaddrs: string[]
  hardware?: string
  name?: string
}

export type RecordingMode = 'off' | 'continuous' | 'prealarm'

export type RecordingProfileDto = {
  id: string
  name: string
  mode: RecordingMode
  preRecordSeconds: number
  postRecordSeconds: number
  segmentSeconds: number
  isOverridePreset: boolean
  isBuiltin: boolean
}

// Well-known built-in recording-profile preset IDs (see VmsCfg migration 0001_init.sql)
export const RECORDING_PROFILE_CONTINUOUS_ID = '00000000-0000-0000-0000-000000000001'
export const RECORDING_PROFILE_PREALARM_ID = '00000000-0000-0000-0000-000000000002'
export const RECORDING_PROFILE_OFF_ID = '00000000-0000-0000-0000-000000000003'

export type CameraStreamDto = {
  streamName: string
  profileToken: string
  path: string
  webRtc: boolean
  recordingProfile?: RecordingProfileDto
  overrideProfile?: RecordingProfileDto
  disabled?: boolean
  /** Relative URL to our WHEP gateway (proxies to MediaMTX); set by backend when webRtc=true */
  whepUrl?: string
}

export type CameraSetupDto = {
  cameraId: string
  name: string
  serverId?: string
  /** Derived from first stream's profile mode */
  recording?: boolean
  recordingMode: RecordingMode
  webRtc: boolean
  path: string
  cred?: CredentialsDto
  onvifConn?: OnvifConnectionDto
  /** True while camera is inside a retention episode from FS (wall clock in [retain_from, retain_until]); new clips ingest as retained */
  activeRetentionWindow?: boolean
  streams?: CameraStreamDto[]
  mediaMtxServerId?: string
  onvifEventsEnabled?: boolean
  hasOnvifFilters?: boolean
  onvifManaged?: boolean
  armed?: boolean
  disabled?: boolean
}

export type MediaServerNodeDto = {
  serverId: string
  status: string
}

export type MediaMtxServerDto = {
  id: string
  name: string
  host: string
  webrtcHost: string
  rtspPort: number
  webrtcPort: number
  apiPort: number
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

export type RecordingCoverageGranularity = 'Year' | 'Month' | 'Day'

export type RecordingCoverageQueryDto = {
  cameraId: string
  granularity: RecordingCoverageGranularity
  fromUtc?: string
  toUtc?: string
}

export type RecordingCoverageResponseDto = {
  buckets: string[]
}

export type RetentionEventDto = {
  eventId?: string
  cameraId: string
  streamName?: string
  alarmStartTs: string
  alarmEndTs?: string
  timeoutSeconds?: number
}

export type RetentionEventRowDto = {
  eventId: string
  cameraId: string
  streamName?: string
  alarmStartTs: string
  alarmEndTs?: string
}

export type RetentionEventsListDto = {
  events: RetentionEventRowDto[]
  hasMore: boolean
  nextBeforeAlarmStart?: string
}

export type EventClipsResponseDto = {
  clips: RecordingDto[]
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
  appId?: string
}

export type LogEntryDto = {
  timestamp: string
  level: string
  appId: string
  file: string
  function: string
  message: string
}

export type LogResponseDto = {
  logs: LogEntryDto[]
  hasMore: boolean
}

export type EntityStateDto = {
  service: string
  entityType: string
  entityId: string
  stateJson: string
  lastSeen: string
  stale: boolean
}

export type MonitorLayout = '1x1' | '1x2' | '2x2' | '3x3'

export const MONITOR_LAYOUTS: MonitorLayout[] = ['1x1', '1x2', '2x2', '3x3']

export function layoutSlotCount(layout: MonitorLayout): number {
  switch (layout) {
    case '1x1': return 1
    case '1x2': return 2
    case '2x2': return 4
    case '3x3': return 9
  }
}

export function layoutColumns(layout: MonitorLayout): number {
  switch (layout) {
    case '1x1': return 1
    case '1x2': return 2
    case '2x2': return 2
    case '3x3': return 3
  }
}

export function layoutRows(layout: MonitorLayout): number {
  switch (layout) {
    case '1x1': return 1
    case '1x2': return 1
    case '2x2': return 2
    case '3x3': return 3
  }
}

export type MonitorSlotDto = {
  slotIndex: number
  cameraId: string
}

export type MonitorDto = {
  monitorId: string
  name: string
  layout: MonitorLayout
  cameraIds: string[]
  slots: MonitorSlotDto[]
}

export type OnvifCameraEntryDto = {
  cameraId: string
  subscribed: boolean
  pollTimeoutSeconds: number
  messageLimit: number
}

export type CameraArmedDto = {
  cameraId: string
  armed: boolean
}

export type AnalyticsDetectionClass = 'person' | 'face' | 'vehicle' | 'license_plate'

export const ANALYTICS_DETECTION_CLASSES: AnalyticsDetectionClass[] = ['person', 'face', 'vehicle', 'license_plate']

export type AnalyticsWatchDto = {
  cameraId: string
  streamName: string
  enabled: boolean
  classes: AnalyticsDetectionClass[]
  minConfidence: number
  sampleFps: number
  /** Read-only; populated from camera armed state */
  armed: boolean
  /** Debug: video_a attaches a JPEG of each detection to the event stream */
  attachDebugCrops: boolean
}

export type AnalyticsDetectionDto = {
  id: number
  eventId: string
  cameraId: string
  trackId: number
  kind: AnalyticsDetectionClass | 'unknown'
  confidence: number
  detectedAt: string
  plateText?: string
  hasCrop: boolean
}

export type AnalyticsActivityEntryDto = {
  cameraId: string
  trackId: number
  kind: AnalyticsDetectionClass | 'unknown'
  confidence: number
  bboxX: number
  bboxY: number
  bboxWidth: number
  bboxHeight: number
  detectedAt: string
  plateText?: string
  /** Base64 JPEG; absent when the watch has no debug crops enabled */
  cropJpegBase64?: string
  /** false = seen by the detector but dropped by debounce/armed check */
  persisted: boolean
}

export type OnvifAlarmFilterDto = {
  id?: string
  cameraId: string
  filterText: string
  textFilter?: string
  timeoutSeconds: number
}

export type OnvifEventRecordDto = {
  receivedAt: string
  topic: string
  payloadXml: string
  cameraId?: string
}

export type ObservedTopicDto = {
  topic: string
  firstSeen: string
  lastSeen: string
}

export type VideoEncoderConfigDto = {
  token: string
  name: string
  encoding: string
  width: number
  height: number
  frameRateLimit: number
  bitrateLimit: number
  govLength: number
  h264Profile: string
  quality: number
}

export type VideoEncoderConfigOptionsDto = {
  encoding: string
  resolutions: { width: number; height: number }[]
  minFrameRate: number
  maxFrameRate: number
  minBitrate: number
  maxBitrate: number
  minGovLength: number
  maxGovLength: number
  h264Profiles: string[]
}

export type AudioEncoderConfigDto = {
  token: string
  name: string
  encoding: string
  bitrate: number
  sampleRate: number
}

export type AudioEncoderConfigOptionsDto = {
  encoding: string
  bitrates: number[]
  sampleRates: number[]
}

export type PtzPresetDto = {
  token: string
  name: string
}

export type DeviceInfoDto = {
  manufacturer: string
  model: string
  firmwareVersion: string
  serialNumber: string
  hardwareId: string
}

export type DeviceCapabilitiesDto = {
  hasPtz: boolean
  hasImaging: boolean
  hasEvents: boolean
  hasAudio: boolean
}

export type FloatRangeDto = { min: number; max: number }

export type ImagingSettingsDto = {
  brightness?: number | null
  contrast?: number | null
  colorSaturation?: number | null
  sharpness?: number | null
}

export type ImagingOptionsDto = {
  brightness?: FloatRangeDto | null
  contrast?: FloatRangeDto | null
  colorSaturation?: FloatRangeDto | null
  sharpness?: FloatRangeDto | null
}
