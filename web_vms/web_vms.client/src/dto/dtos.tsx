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

export type ScheduleWindowKind = 'weekly' | 'date'

/** Seconds are counted from local midnight in the schedule's timezone (0..86400); endSecond is
 *  exclusive and endSecond <= startSecond means the window wraps past midnight. */
export type ScheduleWindowDto = {
  kind: ScheduleWindowKind
  /** 0=Sunday..6=Saturday; used for 'weekly' windows */
  dayOfWeek: number
  /** 'YYYY-MM-DD'; used for 'date' windows */
  date: string
  startSecond: number
  endSecond: number
}

export type RecordingScheduleDto = {
  id?: string
  name: string
  /** IANA timezone id, e.g. 'Europe/Moscow' */
  timezone: string
  /** Seconds before a window start at which recording is switched on */
  leadInSeconds: number
  windows: ScheduleWindowDto[]
}

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
  /** An active retention window with no specific stream retains every stream. */
  allStreamsRetained?: boolean
  /** Specific stream names currently retained (an alarm scoped to those streams). */
  retainedStreams?: string[]
  streams?: CameraStreamDto[]
  mediaMtxServerId?: string
  /** Read-only mirror of the assigned media router's STUN URL, used to build the WHEP ICE config.
   *  undefined/null = no router assigned; empty string = no STUN (LAN only). */
  effectiveWebrtcStunUrl?: string | null
  onvifEventsEnabled?: boolean
  hasOnvifFilters?: boolean
  onvifManaged?: boolean
  armed?: boolean
  disabled?: boolean
  /** True when the camera has an enabled analytics watch; gates the live bbox overlay toggle. */
  hasAnalytics?: boolean
  /** Recording schedules assigned to this camera (uuids). */
  scheduleIds?: string[]
  /** True while one of the assigned schedule windows is open. Recomputed server-side from the wall
   *  clock; while true the camera records continuously, overriding profile and overrides. */
  scheduleActive?: boolean
}

/** What a user may do with a camera. All false = no access. */
export type CameraPermissionFlagsDto = {
  viewLive: boolean
  viewArchive: boolean
  ptz: boolean
  /** Arming, recording overrides, manual retention, relay outputs. */
  control: boolean
  /** Downloading a clip as a file. */
  export: boolean
}

/** Effective permission on a camera is the OR of allCameras and the matching cameras entry. */
export type UserCameraPermissionsDto = {
  username: string
  /** null/undefined = no blanket grant; a blanket grant also covers cameras added later. */
  allCameras?: CameraPermissionFlagsDto | null
  cameras: Record<string, CameraPermissionFlagsDto>
}

/** Answer to "what may I do", used to hide affordances. The server still checks every request. */
export type MyPermissionsDto = {
  username: string
  isAdmin: boolean
  aclEnforced: boolean
  allCameras?: CameraPermissionFlagsDto | null
  cameras: Record<string, CameraPermissionFlagsDto>
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
  /** STUN server the browser uses for WHEP; empty = no STUN, LAN host candidates only */
  stunUrl: string
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

/**
 * Timeline coverage: continuous stretches of archive, several cameras per call.
 * Unlike recording-ranges this carries no clips — the timeline only ever needs boundaries.
 */
export type RecordingTimelineQueryDto = {
  cameraIds: string[]
  streamName?: string
  fromUtc: string
  toUtc: string
  /** Gaps at or below this are drawn as continuous — normally the wall time of one screen pixel. */
  minGapMs: number
}

export type RecordingIntervalDto = {
  startTime: string
  endTime: string
}

export type RecordingTimelineTrackDto = {
  cameraId: string
  intervals: RecordingIntervalDto[]
}

export type RecordingTimelineResponseDto = {
  tracks: RecordingTimelineTrackDto[]
}

/**
 * One interval a camera's source was unavailable — why the archive has a hole there.
 *
 * Only streams that were supposed to be recording produce one: with recording off nothing was going
 * to be written, so there is nothing to restore from the camera and no gap to report.
 */
export type SourceOutageDto = {
  outageUid: string
  cameraId: string
  streamName?: string
  startedTs: string
  /** Absent = the source is down right now. */
  endedTs?: string
  /** `link_lost` (this camera) or `server_down` (the whole node went quiet). */
  cause: string
  /** `open|filling|filled|exhausted|partial|held|ignored`. */
  state: string
  /** Covered by clips, for display only — decisions are made from the archive itself. */
  filledMs: number
  attempts: number
  message: string
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

// One episode = one retention open->close cycle. Selected by the user; archive/details load by eventUuid.
export type EpisodeDto = {
  eventUuid: string
  eventId: string
  objectId?: string
  cameraId: string
  streamName?: string
  alarmStartTs: string
  alarmEndTs?: string
}

// A group = all episodes sharing one eventId (a time-clustered session of one object, possibly
// across cameras). Rendered as a tree row with a "+N" badge; episodes ordered oldest-first.
export type EventGroupDto = {
  eventId: string
  objectId?: string
  representativeCameraId: string
  earliestAlarmStart: string
  latestAlarmStart: string
  episodes: EpisodeDto[]
}

export type RetentionEventGroupsListDto = {
  groups: EventGroupDto[]
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

/**
 * Uniform grids, plus the classic "feature" layouts: one large cell with smaller ones filling the
 * remaining column and row. '1+5' is a 3x3 with a 2x2 feature, '1+7' a 4x4 with a 3x3 feature —
 * the counts are the usual VMS naming (one big, N small).
 */
export type MonitorLayout = '1x1' | '1x2' | '2x2' | '1+5' | '1+7' | '3x3'

export const MONITOR_LAYOUTS: MonitorLayout[] = ['1x1', '1x2', '2x2', '1+5', '1+7', '3x3']

export function layoutSlotCount(layout: MonitorLayout): number {
  switch (layout) {
    case '1x1': return 1
    case '1x2': return 2
    case '2x2': return 4
    case '1+5': return 6
    case '1+7': return 8
    case '3x3': return 9
  }
}

export function layoutColumns(layout: MonitorLayout): number {
  switch (layout) {
    case '1x1': return 1
    case '1x2': return 2
    case '2x2': return 2
    case '1+5': return 3
    case '1+7': return 4
    case '3x3': return 3
  }
}

export function layoutRows(layout: MonitorLayout): number {
  switch (layout) {
    case '1x1': return 1
    case '1x2': return 1
    case '2x2': return 2
    case '1+5': return 3
    case '1+7': return 4
    case '3x3': return 3
  }
}

/**
 * How many rows and columns the first slot spans, or 0 for a uniform grid.
 *
 * Only the first slot is placed explicitly; the rest are left to CSS grid auto-placement, which
 * fills the leftover column and bottom row in slot order — exactly the classic arrangement, with
 * no per-slot coordinate table to keep in step with the layout list.
 */
export function layoutFeatureSpan(layout: MonitorLayout): number {
  switch (layout) {
    case '1+5': return 2
    case '1+7': return 3
    default: return 0
  }
}

export type MonitorSlotDto = {
  slotIndex: number
  cameraId: string
}

/**
 * 'wall' — the shared, admin-defined video wall. 'investigation' — a private board an operator
 * creates while working through an incident: same grid and timeline, but its own camera set,
 * visible only to its owner, and always in playback.
 */
export type MonitorKind = 'wall' | 'investigation'

export type MonitorDto = {
  monitorId: string
  name: string
  layout: MonitorLayout
  cameraIds: string[]
  slots: MonitorSlotDto[]
  kind: MonitorKind
  /** Server-assigned; empty when authentication is disabled. Ignored on writes. */
  owner: string
}

/**
 * The next larger layout, or null when there is nowhere left to grow — boards grow as cameras are
 * added to them. A feature layout grows into the next feature layout rather than flattening into a
 * uniform grid: growing a board should add room, not silently rearrange what the operator chose.
 */
export function nextLayoutUp(layout: MonitorLayout): MonitorLayout | null {
  switch (layout) {
    case '1x1': return '1x2'
    case '1x2': return '2x2'
    case '2x2': return '3x3'
    case '1+5': return '1+7'
    case '1+7': return '3x3'
    case '3x3': return null
  }
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
  /** Seconds of recording kept after the last detection before the event closes itself. */
  timeoutSeconds: number
}

export type AnalyticsDetectionDto = {
  id: number
  eventId: string
  eventUuid: string
  objectId?: string
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
  /** Re-identification label of the physical object, when resolved for this track */
  objectId?: string
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
  hasRelayOutputs: boolean
  hasDigitalInputs: boolean
}

export type RelayMode = 'monostable' | 'bistable'
export type IdleState = 'open' | 'closed'

// ONVIF has no operation to read a relay's current logical (active/inactive) state — only its
// configured Mode/DelayTime/IdleState. Live state is tracked client-side after a command, never
// fetched from the server.
export type RelayOutputDto = {
  token: string
  mode: RelayMode
  idleState: IdleState
  delayMs: number
}

export type RelayOutputOptionsDto = {
  token: string
  supportedModes: RelayMode[]
  discrete: boolean
  minDelayMs: number
  maxDelayMs: number
  discreteDelaysMs?: number[]
}

// idleState is null when the camera didn't report it at all.
export type DigitalInputDto = {
  token: string
  idleState: IdleState | null
}

export type DigitalInputOptionsDto = {
  token: string
  idleStateConfigurable: boolean
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

// ── Camera-side (SD card) archive, ONVIF Profile G ────────────────────────────
// Timestamps stay ISO 8601 strings: they come from the device and are only displayed or echoed
// back, so parsing them into Date would invent precision the camera never reported.

export type EdgeCapabilitiesDto = {
  recording: boolean
  search: boolean
  replay: boolean
  /** Both search and replay — the minimum for browsing and importing. */
  supported: boolean
  dynamicRecordings: boolean
  dynamicTracks: boolean
  storageConfiguration: boolean
  auxiliaryCommands: boolean
  maxRecordings: number
  supportedAuxiliaryCommands: string[]
  /** Why Profile G is unavailable, when it is. Shown instead of empty panels. */
  message: string
}

export type EdgeSummaryDto = {
  dataFrom: string
  dataUntil: string
  numberRecordings: number
  supported: boolean
  message: string
  /** Camera clock minus ours, in ms, measured while answering. Null = the camera does not report
   *  its time, and then these timestamps are the camera's own uncorrected scale. */
  clockOffsetMs?: number | null
}

export type EdgeTrackDto = {
  trackToken: string
  trackType: string
  description: string
  dataFrom: string
  dataTo: string
}

export type EdgeRecordingDto = {
  recordingToken: string
  sourceName: string
  sourceDescription: string
  earliestRecording: string
  latestRecording: string
  content: string
  recordingStatus: string
  tracks: EdgeTrackDto[]
}

export type EdgeRecordingsDto = {
  recordings: EdgeRecordingDto[]
  message: string
  /** Camera clock minus ours, in ms, measured while answering. Null = the camera does not report
   *  its time, and then these timestamps are the camera's own uncorrected scale. */
  clockOffsetMs?: number | null
}

export type EdgeIntervalDto = {
  recordingToken: string
  trackToken: string
  startTime: string
  endTime: string
}

export type EdgeIntervalsDto = {
  intervals: EdgeIntervalDto[]
  searched: boolean
  message: string
  /** Camera clock minus ours, in ms, measured while answering. Null = the camera does not report
   *  its time, and then these timestamps are the camera's own uncorrected scale. */
  clockOffsetMs?: number | null
}

export type EdgeRecordingJobDto = {
  jobToken: string
  recordingToken: string
  mode: string
  sourceToken: string
}

export type EdgeRecordingConfigurationDto = {
  recordingToken: string
  sourceId: string
  sourceName: string
  sourceLocation: string
  sourceDescription: string
  sourceAddress: string
  content: string
  /** ISO 8601 duration; PT0S means unlimited retention on the device. */
  maximumRetentionTime: string
}

export type EdgeRecordingJobsDto = {
  jobs: EdgeRecordingJobDto[]
  configurations: EdgeRecordingConfigurationDto[]
  message: string
}

export type EdgeStorageConfigurationDto = {
  token: string
  type: string
  localPath: string
  storageUri: string
  user: string
}

export type EdgeStorageDto = {
  configurations: EdgeStorageConfigurationDto[]
  supported: boolean
  formatSupported: boolean
  /** The exact vendor auxiliary command a format would send — shown before confirming. */
  formatCommand: string
  message: string
}

export type EdgeImportRequestDto = {
  recordingToken: string
  streamName?: string
  startTime: string
  endTime: string
  /** Start of the recording the token addresses; the RTSP seek is an offset from it. */
  recordingStartTime: string
  exportToFile?: boolean
  segmentSeconds?: number
}

export type EdgeImportJobState = 'running' | 'completed' | 'failed' | 'cancelled' | 'unknown'

export type EdgeImportJobDto = {
  jobId: string
  cameraId: string
  streamName: string
  state: EdgeImportJobState
  startTime: string | null
  endTime: string | null
  /** Wall clock reached so far; progress is its position within [startTime, endTime]. */
  position: string | null
  clipsWritten: number
  bytesWritten: number
  message: string
  exportPath: string
}

export type EdgeExportDto = {
  relPath: string
  cameraId: string
  sizeBytes: number
  createdAt: string | null
}

export type EdgeExportLinkDto = {
  /** Relative URL carrying a sealed grant; usable directly as a media element source. */
  url: string
  expiresAt: string
}
