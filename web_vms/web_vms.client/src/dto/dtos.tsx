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

/** What media_server last measured on a running stream. Absent means nothing was measured
 *  recently: the snapshot carries a TTL, so "gone" and "stale" are the same case. */
export type StreamStatsDto = {
  width: number
  height: number
  codec: string
  fps: number
  bitrateBps: number
  /** Unix milliseconds; 0 when the publisher could not say. */
  observedAtMs: number
  /** Which encoder the WebRTC path re-compresses this stream with ("vaapi", "x264"). Empty means
   *  nothing is re-encoded: nobody is watching, or the camera already sends H.264. */
  transcodeEncoder?: string
}

export type CameraStreamDto = {
  streamName: string
  profileToken: string
  path: string
  webRtc: boolean
  recordingProfile?: RecordingProfileDto
  overrideProfile?: RecordingProfileDto
  disabled?: boolean
  /** Whether this stream may be recorded at all. Orthogonal to `webRtc`: watching a stream and
   *  archiving it are separate decisions. Absent means true — an older payload must not be able to
   *  take a stream out of the archive by omission. Use `isRecordable` rather than reading it raw. */
  recordable?: boolean
  /** Resolution the camera declares for this stream's ONVIF profile; 0/absent when not declared.
   *  A seed for adaptive stream choice, never the truth — observed parameters win. */
  declaredWidth?: number
  declaredHeight?: number
  /** Observed parameters, when a fresh measurement exists. These win over `declaredWidth`/
   *  `declaredHeight`, which can go stale without anyone editing the configuration. */
  stats?: StreamStatsDto
  /** Relative URL to our WHEP gateway (proxies to MediaMTX); set by backend when webRtc=true */
  whepUrl?: string
}

/** A stream counts as recordable unless it says otherwise; see `CameraStreamDto.recordable`. */
export const isRecordable = (s: CameraStreamDto): boolean => s.recordable !== false

/** Enabled streams of this camera that are allowed into the archive — the target of every
 *  camera-level recording action, and what the monitor's record button acts on. */
export const recordableStreams = (camera: { streams?: CameraStreamDto[] }): CameraStreamDto[] =>
  (camera.streams ?? []).filter((s) => !s.disabled && isRecordable(s))

/** A stream's frame size and where the number came from. */
export type StreamSize = { width: number; height: number; observed: boolean }

/**
 * The best available frame size for a stream: what media_server measured, or failing that what the
 * camera declares for its ONVIF profile. Null when neither is known — a stream nobody has watched
 * on a camera that was configured with a plain RTSP path.
 *
 * The order is the point. A declared resolution can go stale without anyone editing the
 * configuration (a different ONVIF profile, a different file), so it is only ever a seed.
 */
export const streamSize = (s: CameraStreamDto): StreamSize | null => {
  if (s.stats && s.stats.width > 0 && s.stats.height > 0)
    return { width: s.stats.width, height: s.stats.height, observed: true }
  if ((s.declaredWidth ?? 0) > 0 && (s.declaredHeight ?? 0) > 0)
    return { width: s.declaredWidth as number, height: s.declaredHeight as number, observed: false }
  return null
}

/**
 * What this stream actually records — the client's statement of the precedence the server applies
 * (`RecordingRules.Effective` in web_vms.Types, `recording_policy_for_stream` in media_server):
 * a stream that may not be recorded never records, then an open schedule window forces continuous
 * recording, then an override profile, then the base profile.
 *
 * Getting this wrong is visible: the card outlines itself red to say "recording", so a mode that
 * claims more than the server does shows a recording that is not happening.
 */
export const effectiveStreamMode = (
  stream: CameraStreamDto,
  scheduleActive: boolean,
): RecordingMode => {
  if (!isRecordable(stream)) return 'off'
  if (scheduleActive) return 'continuous'
  return (stream.overrideProfile ?? stream.recordingProfile)?.mode ?? 'off'
}

export type CameraSetupDto = {
  cameraId: string
  name: string
  serverId?: string
  /** The camera's recording state: the strongest effective mode across its enabled streams that
   *  are available for recording. */
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
  /** True when the camera has an enabled CV watch; gates the live bbox overlay toggle. Not the
   *  device's own ONVIF Analytics service — that is `hasAnalyticsService` on the capabilities. */
  cvWatchEnabled?: boolean
  /** Recording schedules assigned to this camera (uuids). */
  scheduleIds?: string[]
  /** True while one of the assigned schedule windows is open. Recomputed server-side from the wall
   *  clock; while true the camera records continuously, overriding profile and overrides. */
  scheduleActive?: boolean
  /** Which stream to recover from the camera's own storage when the archive has a gap; empty = off. */
  edgeBackfillStream?: string
  /** Drop the object boxes this camera puts in its own RTSP metadata track, before they are
   *  parsed: nothing reaches the archive, the live overlay or the ROI pass. Separate from
   *  switching analytics off on the device, which lives in the camera's ONVIF settings. */
  onvifMetadataDisabled?: boolean
  /** This camera really can move, so it may be driven — manually or by object tracking. An
   *  administrator's word, not the device's: firmware advertises the ONVIF PTZ service whether or
   *  not there is a motor behind it. */
  hasTelemetry?: boolean
  /** Re-encode finished clips with ROI, keeping detector boxes sharp. */
  roiTranscode?: boolean
  /** Hours before a closed clip may be re-encoded; 0 means as soon as possible. */
  roiDelayHours?: number
  roiMinCoverage?: number
  roiMaxCoverage?: number
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
  /** Media server node this MediaMTX serves; empty = shared, usable by any node */
  serverId: string
  publishUser: string
  publishPass: string
  readUser: string
  readPass: string
  /** Re-encode HEVC cameras to H.264 before publishing here. False passes H.265 through, which
   *  MediaMTX carries over WebRTC to browsers that can decode it. */
  transcodeHevc: boolean
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
  /** `live`, or `edge` for a range recovered from the camera's own storage. */
  origin?: string
  /** Re-encoded with ROI: sharp where the detector saw something, compressed harder elsewhere. */
  /** What the ROI pass did: re-encoded it, looked and left it alone because the result
   *  was no smaller, or nothing (never examined, skipped, given up on). */
  roiState?: 'original' | 'recompressed' | 'no_gain'
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
  /** `live`, or `edge` for footage recovered from the camera's own storage. */
  origin?: string
  /** Re-encoded with ROI: kept sharp where the detector saw something, compressed harder elsewhere. */
  /** What the ROI pass did: re-encoded it, looked and left it alone because the result
   *  was no smaller, or nothing (never examined, skipped, given up on). */
  roiState?: 'original' | 'recompressed' | 'no_gain'
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
  /**
   * What set the alarm off, in the producer's own words — the ONVIF topic
   * ('tns1:RuleEngine/CellMotionDetector/Motion') for an ONVIF alarm. Absent for producers that
   * name no cause.
   */
  trigger?: string
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
  /** What set the alarm off — the ONVIF topic for an ONVIF alarm. Absent when none was named. */
  trigger?: string
}

/** An alarm episode as the operator's map sees it. */
export type AlarmDto = EpisodeDto & {
  /** Still running: its projected end is after the server's clock. */
  active: boolean
  /** The caller may watch the camera's archive, and so the recording of this alarm. */
  archive: boolean
}

/** Alarms running now and those that ended within the window, newest first, with the server's clock. */
export type AlarmsDto = {
  serverTime: string
  alarms: AlarmDto[]
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
  /** Out of service: kept, with its camera assignments, but taking no new recordings. */
  disabled?: boolean
  /** Cameras recording here. They lose their assignment if it is deleted — the FK cascades. */
  cameraCount?: number
}

/** One writable directory on a recording node's disk. `path` is that node's own path style. */
export type DirectoryEntryDto = {
  path: string
  name: string
  freeBytes: number
  totalBytes: number
  /** Only a writable directory can be picked. The rest are listed so their contents stay reachable
   *  — a volume root is routinely off limits while the directory to record into sits inside it. */
  writable: boolean
  /** A file rather than a directory; only present when the caller asked for files. Then totalBytes
   *  is the file's own size and freeBytes is 0. */
  isFile?: boolean
}

export type DirectoryListingDto = {
  path: string
  /** Empty means "up goes back to the volume roots" — also the case for a volume root itself. */
  parentPath: string
  entries: DirectoryEntryDto[]
  /** Non-fatal note from the node: truncated listing, unreadable path, nothing writable found. */
  message: string
  /** Whether `path` itself can be picked as a storage root. */
  writable: boolean
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
export type MonitorLayout = '1x1' | '1x2' | '2x2' | '1+5' | '1+7' | '3x3' | '4x3' | '4x4'

export const MONITOR_LAYOUTS: MonitorLayout[] = ['1x1', '1x2', '2x2', '1+5', '1+7', '3x3', '4x3', '4x4']

export function layoutSlotCount(layout: MonitorLayout): number {
  switch (layout) {
    case '1x1': return 1
    case '1x2': return 2
    case '2x2': return 4
    case '1+5': return 6
    case '1+7': return 8
    case '3x3': return 9
    case '4x3': return 12
    case '4x4': return 16
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
    case '4x3': return 4
    case '4x4': return 4
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
    case '4x3': return 3
    case '4x4': return 4
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
  /** Stream this slot is pinned to for the current user; empty means the tile picks one from its
   *  own size. Per operator, unlike `cameraId` — pinning on a shared wall is a private choice. */
  streamName?: string
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
    case '3x3': return '4x3'
    case '4x3': return '4x4'
    case '4x4': return null
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

/** Classes the CV detector can be configured to look for — the choices offered on a watch. */
export type AnalyticsDetectionClass = 'person' | 'face' | 'vehicle' | 'license_plate'

/**
 * Every class a detection can actually arrive with. A superset of the watch classes: a camera's own
 * analytics reports kinds the CV detector has no model for ('animal' is part of ONVIF's own
 * vocabulary), and anything it cannot place at all still gets drawn as 'unknown'.
 */
export type AnalyticsDetectionKind = AnalyticsDetectionClass | 'animal' | 'unknown'

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
  /** Persist this camera's detection embeddings for forensic similarity search. */
  storeEmbeddings: boolean
}

export type PtzCalibrationDto = {
  cameraId: string
  panDx: number
  panDy: number
  tiltDx: number
  tiltDy: number
  confidence: number
  calibratedAt?: string
  method: string
}

export type AnalyticsDetectionDto = {
  id: number
  eventId: string
  eventUuid: string
  objectId?: string
  cameraId: string
  trackId: number
  kind: AnalyticsDetectionKind
  confidence: number
  detectedAt: string
  plateText?: string
  hasCrop: boolean
}

export type DetectionIdProbeDto = {
  detectionId: number
}

export type ArchiveTrackProbeDto = {
  cameraId: string
  trackId: number
  ts: string
  kind?: AnalyticsDetectionKind
}

export type SearchSimilarDetectionsRequestDto = {
  detection?: DetectionIdProbeDto
  archive?: ArchiveTrackProbeDto
  image?: ImageProbeDto
  fromTs: string
  toTs: string
  minScore?: number
  limit?: number
  cameraIds?: string[]
}

export type BoundingBoxDto = {
  x: number
  y: number
  width: number
  height: number
}

export type ImageProbeDto = {
  jpegBase64: string
  kind: AnalyticsDetectionKind
  bbox?: BoundingBoxDto
  detectFirst?: boolean
}

export type SimilarDetectionHitDto = {
  detection: AnalyticsDetectionDto
  score: number
}

export type AnalyticsActivityEntryDto = {
  cameraId: string
  trackId: number
  kind: AnalyticsDetectionKind
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
  /**
   * Where the box came from: 'cv' (the video_a detector) or 'onvif' (the camera's own Scene
   * Description). The two number their tracks independently, so a camera running both needs this
   * in the overlay's key or the boxes overwrite each other.
   */
  source: AnalyticsDetectionSource
  /**
   * The producer's own word for the class when it says more than `kind` does — 'Truck' under
   * vehicle, 'Bag' under unknown. ONVIF lets cameras report free-form classes alongside its
   * five-value enum, so this is where the detail that does not fit `kind` survives.
   */
  label?: string
}

export type AnalyticsDetectionSource = 'cv' | 'onvif'

/** One object as its producer saw it, normalized 0..1 from the top-left corner. */
export type ArchiveMetadataBoxDto = {
  trackId: number
  kind: AnalyticsDetectionKind
  confidence: number
  bboxX: number
  bboxY: number
  bboxWidth: number
  bboxHeight: number
  label?: string
  plateText?: string
}

/** Everything one producer saw at one instant, on the same wall clock the playlist's PDT carries. */
export type ArchiveMetadataFrameDto = {
  ts: string
  boxes: ArchiveMetadataBoxDto[]
}

/**
 * Frames from one producer, ascending by time. The two sources stay apart all the way to the
 * renderer: they number their tracks independently, and their timestamps come off different clocks.
 */
export type ArchiveMetadataSourceDto = {
  source: AnalyticsDetectionSource
  frames: ArchiveMetadataFrameDto[]
}

export type ArchiveMetadataResponseDto = {
  sources: ArchiveMetadataSourceDto[]
}

export type ArchiveMetadataCoverageDto = {
  hasCv: boolean
  hasOnvif: boolean
}

/**
 * An ONVIF event shown over live video. Carries no geometry: cameras whose analytics is cell motion
 * or tamper have nothing to outline, and the event is the whole statement they can make.
 */
export type LiveCameraEventDto = {
  cameraId: string
  /** Full ONVIF topic, the same string the alarm filters are written against. */
  topic: string
  /** Short name from the last topic segment ('Motion', 'Tamper'). */
  label: string
  /** True when the condition started, false when it cleared. */
  active: boolean
  /** True when the event matched one of the camera's alarm filters. Subscribing is what puts an
   *  event on this stream; matching is only what makes it worth keeping a clip for. */
  matched: boolean
  receivedAt: string
}

/** The server-side CV detector (video_a) watching this camera. */
export type CvSourceDto = {
  enabled: boolean
  streamName: string
  classes: string[]
  minConfidence: number
  sampleFps: number
  timeoutSeconds: number
  attachDebugCrops: boolean
  storeEmbeddings: boolean
}

/** The camera's own object boxes. Two halves that must not be collapsed into one. */
export type OnvifMetadataSourceDto = {
  /** Whether we consume the boxes the camera sends. */
  accepted: boolean
  /** Whether the device is configured to stream boxes at all. Null = not probed yet, which is
   *  deliberately distinct from a confident "no". */
  deviceEmits: boolean | null
}

export type OnvifEventsSourceDto = {
  /** False when the camera has no subscription row at all — not the same as managed-but-off. */
  managed: boolean
  subscribed: boolean
  pollTimeoutSeconds: number
  messageLimit: number
  filterCount: number
}

/** Every source that can raise a detection for one camera, as the Detections tab renders it. */
export type CameraAnalyticsSourcesDto = {
  cameraId: string
  /** Null when the camera has no analytics watch at all — different from having one switched off. */
  cv: CvSourceDto | null
  onvifMetadata: OnvifMetadataSourceDto
  onvifEvents: OnvifEventsSourceDto
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
  /** Camera runs its own analytics engine (ONVIF ver20 Analytics) — gates the Metadata tab. */
  hasAnalyticsService: boolean
}

// ── ONVIF Profile M: metadata / analytics ───────────────────────────────────

export type MetadataConfigDto = {
  token: string
  name: string
  /** The flag that decides whether the metadata track carries object boxes at all. */
  analytics: boolean
  ptzStatus: boolean
  events: boolean
  geoLocation: boolean
  shapePolygon: boolean
  sessionTimeout?: string
  compressionType?: string
  /** Profiles this configuration is attached to; empty means it changes no stream. */
  attachedProfileTokens: string[]
}

export type MetadataConfigsResponseDto = {
  configs: MetadataConfigDto[]
  /** The camera's own wording when it refused; the list is empty in that case. */
  error?: string
}

export type MetadataConfigOptionsDto = {
  supportsPtzStatus: boolean
  compressionTypes: string[]
  error?: string
}

export type OnvifAnalyticsConfigDto = {
  token: string
  name: string
  attachedProfileTokens: string[]
}

export type OnvifAnalyticsConfigsResponseDto = {
  configs: OnvifAnalyticsConfigDto[]
  error?: string
}

export type AnalyticsSimpleItemDto = {
  name: string
  value: string
}

/** A configured analytics module or rule on the camera. */
export type OnvifAnalyticsModuleDto = {
  name: string
  type: string
  simpleItems: AnalyticsSimpleItemDto[]
  /**
   * Structured parameters (polygons, line segments, schedules). Not modelled beyond name + XML:
   * they have no fixed schema across vendors, and they must round-trip untouched or a Modify would
   * silently drop whatever the UI did not understand. The name is the camera's label for the slot
   * ('Field') and is independent of the XML inside it ('Polygon') — deriving one from the other
   * renames the parameter.
   */
  elementItems: AnalyticsElementItemDto[]
}

export type AnalyticsElementItemDto = {
  name: string
  xml: string
}

export type OnvifAnalyticsModulesResponseDto = {
  modules: OnvifAnalyticsModuleDto[]
  error?: string
}

export type AnalyticsParameterDescriptionDto = {
  name: string
  type: string
}

/**
 * How the camera lays out its motion-detection cells. An ActiveCells bitmask is a flat run of bits;
 * only these two numbers say which cell is where. Optional in ONVIF and absent on many firmwares,
 * so a UI must be able to do without it.
 */
export type AnalyticsCellLayoutDto = {
  columns: number
  rows: number
}

export type OnvifAnalyticsModuleDescriptionDto = {
  type: string
  maxInstances?: string
  parameters: AnalyticsParameterDescriptionDto[]
  cellLayout?: AnalyticsCellLayoutDto
}

export type OnvifAnalyticsModuleDescriptionsResponseDto = {
  descriptions: OnvifAnalyticsModuleDescriptionDto[]
  error?: string
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

/** One box on the live overlay, as the tracking service names it. */
export interface TrackingTargetDto {
  source: AnalyticsDetectionSource
  trackId: number
  kind: string
}

/** What the automatic "keep this object centred" loop is doing on one camera. */
export interface PtzTrackingStateDto {
  cameraId: string
  active: boolean
  /** Absent while the mode is armed but nothing has been acquired yet. */
  target?: TrackingTargetDto | null
  /** The operator named this target, rather than it being picked up automatically. */
  explicitTarget: boolean
  targetSeenAt?: string | null
  /** Last PTZ failure; empty while the camera is obeying. */
  lastError: string
  /** Whose picture this camera follows; absent when it is its own. */
  masterCameraId?: string | null
}

/**
 * Whether a camera follows objects on its own, and which ones it prefers.
 *
 * Configuration rather than a control: a camera with this on moves by itself around the clock,
 * whether or not anyone has the monitor open, and comes back doing so after a restart.
 */
export interface PtzTrackingPolicyDto {
  enabled: boolean
  /** In priority order; the first of these in the picture is followed. Empty means any object. */
  kinds: string[]
  /** Which producers of boxes to follow: "cv" and/or "onvif". Empty means either. */
  sources: AnalyticsDetectionSource[]
  /** Whose objects to follow. Null/absent is the camera's own picture. */
  masterCameraId?: string | null
}

/** Which way a slave is driven to the place a master's object maps to. */
export type PtzSlaveBindingMethod = 'absolute' | 'presets' | 'geo'

/**
 * One marked correspondence: a spot in the master's frame, and where the slave was pointing.
 *
 * Both a position and a preset is the normal case, not redundancy — one trip to the spot yields
 * both — and it is what lets the method be changed later without marking the zones out again.
 */
export interface PtzSlaveBindingPointDto {
  /** 0..1 from the left of the master's frame. */
  masterX: number
  /** 0..1 from the top. The spot on the ground, not the middle of an object. */
  masterY: number
  /** Where the slave looked, if the camera would say. Its own units, uninterpreted. */
  pan?: number | null
  tilt?: number | null
  presetToken?: string | null
  presetName?: string | null
  /**
   * How far the rest of the survey lands from this point when it is left out of the fit — so a
   * point that stands out is one marked wrong. Null where the survey is too small to check it, or
   * the point has no position. Read-only: computed on the way out, ignored on the way in.
   */
  residual?: number | null
}

/** How a slave is aimed at the objects its master sees. */
export interface PtzSlaveBindingDto {
  masterCameraId: string
  method: PtzSlaveBindingMethod
  calibratedAt?: string | null
  points: PtzSlaveBindingPointDto[]
  /** Why this binding cannot aim the camera as it stands; null when it can. */
  unusable?: string | null
}

/** Where a test aim sent the camera. */
export interface PtzSlaveAimResultDto {
  pan?: number | null
  tilt?: number | null
  presetToken?: string | null
  presetName?: string | null
  /** Outside the marked area, so the mapping is continuing the measurements rather than interpolating. */
  extrapolated: boolean
  /** The zoom it was sent with, on a method that commands a position. */
  zoom?: number | null
}

/** Where a camera says it is pointing. Its own units. */
export interface PtzStatusDto {
  /** False when the camera would not answer. Answering is not proof the numbers move with it. */
  available: boolean
  pan: number
  tilt: number
  zoom: number
  moving: boolean
  error: string
}

// ── Maps & plans ────────────────────────────────────────────────────────────
// A space is one world things are placed in: a real map, or a plan nested in another space. Every
// position is WGS84 plus a height in metres above the floor of its space; a space's elevationM is
// that floor's height above the ground. A plan in made-up coordinates uses WGS84 made up around (0, 0).

export type GeoPointDto = { lat: number; lon: number }

/** Three corners of a plan image in its space — an affine placement. */
export type PlanCornersDto = { topLeft: GeoPointDto; topRight: GeoPointDto; bottomLeft: GeoPointDto }

export type SpacePlanDto = {
  corners: PlanCornersDto
  widthPx: number
  heightPx: number
  mime: string
  /** Changes whenever the image does; part of the image URL so a cached copy is never stale. */
  etag: string
}

/** 'map' is a root over map tiles, in real coordinates; 'plan' is drawn on its own picture. */
export type SpaceKind = 'map' | 'plan'

/** A rectangle, by its south-west and north-east corners. */
export type GeoBoundsDto = { southWest: GeoPointDto; northEast: GeoPointDto }

export type SpaceDto = {
  id?: string | null
  /** Null for a root. */
  parentId?: string | null
  name: string
  kind: SpaceKind
  /** Spaces share geometry when they are the same one or both are real. */
  realCoordinates: boolean
  /** Order among siblings: the floors of a building, bottom to top. */
  ordinal: number
  elevationM: number
  /** What the space shows when it is opened. */
  view: GeoBoundsDto
  /** Where it lies on its parent, in the parent's coordinates; empty when none is drawn. */
  footprint: GeoPointDto[]
  /** Read-only here: a plan is uploaded and aligned through its own endpoints. */
  plan?: SpacePlanDto | null
}

export type CameraPlacementDto = {
  cameraId: string
  spaceId: string
  /** Where the camera really stands; aiming and calibration go by it. Its other placements are pictures. */
  isReference: boolean
  lat: number
  lon: number
  heightM: number
  /** Compass bearing of the optical axis: 0 = north, clockwise. */
  headingDeg: number
  /** Only draw the view cone, like rangeM. */
  hfovDeg: number
  rangeM: number
}

/** A named point with a known position, used to calibrate cameras. */
export type LandmarkDto = {
  id?: string | null
  spaceId: string
  name: string
  lat: number
  lon: number
  heightM: number
}

export type PtzGeoObservationDto = {
  landmarkId: string
  /** The camera's own units, as /api/ptz/status reported them. */
  pan: number
  tilt: number
  /** The zoom a slave uses for an object at this landmark's distance, camera units; absent when none was recorded. */
  zoom?: number | null
  /** Leave-one-out miss; null/absent when the rest cannot be fitted without this one. */
  panResidual?: number | null
  tiltResidual?: number | null
  /** From the camera to the landmark. Read-only, like the residuals. */
  distanceM?: number | null
}

export type PtzGeoCalibrationDto = {
  observations: PtzGeoObservationDto[]
  /** Null when the tracking service could not be asked. */
  fitted?: boolean | null
  error?: string | null
}

/** Where a landmark stands on the ground in a fixed camera's picture, 0..1 across and down the frame. */
export type GroundObservationDto = {
  landmarkId: string
  x: number
  y: number
  /** How far the fit of all the marks puts this landmark, in metres; absent with only four marks. */
  residualM?: number | null
}

/**
 * Row-major 3×3 from the normalised frame to WGS84: (x, y, 1) maps to (lat·w, lon·w, w); a point
 * with w ≤ 0 is beyond the horizon.
 */
export type GroundMatrix = number[]

export type GroundCalibrationDto = {
  observations: GroundObservationDto[]
  /** Null when the tracking service could not be asked. */
  fitted?: boolean | null
  error?: string | null
  matrix?: GroundMatrix | null
}

export type GroundProjectionDto = {
  cameraId: string
  matrix: GroundMatrix
}

export type PtzGeoAimResultDto = {
  pan: number
  tilt: number
  azimuthDeg: number
  elevationDeg: number
  distanceM: number
  extrapolated: boolean
  /** From the camera's zoom samples by that distance; 0, the wide end, without any. */
  zoom: number
}
