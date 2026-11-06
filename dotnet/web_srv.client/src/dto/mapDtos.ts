// The map contract as the browser receives it. The HTTP endpoints speak the proto JSON their
// gRPC contract is written in: lowerCamelCase field names, absent defaults, `bytes` as base64 and
// `Timestamp` as an RFC3339 string. These types mirror that shape so a reader can compare them
// with proto/vms_map.proto; do not rename fields here.

export type MapCoordinate = { lat?: number; lon?: number }

/** "Point", "LineString" or "Polygon"; a polygon's ring is closed (first point repeated). */
export type MapGeometry = { coord?: MapCoordinate[]; type?: string }

export type MapExtraProperty = {
  propName?: string
  strVal?: string
  /** How the value is read and edited: plain text when empty, else "__clr", "__datetime", "__double" or "__int". */
  visualType?: string
}

export type MapVisual = {
  objectId?: string
  color?: string
  /** A small icon, base64. */
  image?: string
  imageWidth?: number
  imageHeight?: number
  /** Degrees clockwise from north for a camera's heading, or from upright for an icon. */
  rotationDeg?: number
  text?: string
}

export type MapNodeKind = 'space' | 'diagram' | 'landmark' | 'camera' | 'part' | 'figure' | 'object'

export type MapMarker = {
  id: string
  parentId?: string
  /** Set on a replica: the node whose states, values and events this one shows. */
  ownerId?: string
  name?: string
  hasChildren?: boolean
  /** Set on a camera node or a picture of one. */
  cameraId?: string
  version?: number
  nodeKind?: MapNodeKind
}

export type MapFigure = MapMarker & {
  geometry?: MapGeometry
  /** A point may be drawn as a circle of this radius, in metres. */
  radius?: number
  /** The scale layer this node becomes visible at; absent means always. */
  zoomLevel?: string
  extraProps?: MapExtraProperty[]
  visual?: MapVisual
}

export type MapFigures = { figs?: MapFigure[]; addTracks?: boolean }

/** A node as a write carries it: the tree row alone, without its geometry. */
export type MapObject = {
  id?: string
  parentId?: string
  ownerId?: string
  name?: string
}

export type MapObjectList = { objects?: MapObject[] }

export type MapObjectProperties = { id?: string; properties?: MapExtraProperty[] }

export type MapObjectPropertiesList = {
  objects?: MapObjectProperties[]
  /** True replaces the node's properties, false merges the sent ones into them. */
  replaceProps?: boolean
}

export type MapVisualList = { visuals?: MapVisual[] }

export type MapCreateReplicaRequest = {
  ownerId?: string
  parentId?: string
  /** Empty copies the owner's name. */
  name?: string
}

export type MapFileInfo = { id?: string; mime?: string; size?: string; etag?: string }

export type MapMarkerList = { markers?: MapMarker[] }

export type MapGetByParentResponse = {
  parentId?: string
  /** The chain from the root down to the parent, for breadcrumbs. */
  parents?: MapMarker[]
  children?: MapMarker[]
  startId?: string
  endId?: string
}

export type MapObjProps = {
  marker?: MapMarker
  extraProps?: MapExtraProperty[]
  zoomMin?: number
  zoomMax?: number
  visual?: MapVisual
}

export type MapBox = {
  wn?: MapCoordinate
  es?: MapCoordinate
  zoom?: number
  ids?: string[]
  count?: number
  zone?: MapGeometry[]
  notInZone?: boolean
  /** A canvas instead of a rectangle: everything drawn in that space's coordinates. */
  canvasId?: string
}

export type MapPropertyFilter = { propName?: string; strVal?: string }

export type MapGetByBoxRequest = {
  box?: MapBox
  propertyFilter?: MapPropertyFilter[]
}

export type MapGetByParamsRequest = {
  props?: MapPropertyFilter[]
  startId?: string
  forward?: number
  count?: number
}

export type MapObjectState = {
  id?: string
  /** The full current set for the object, not a delta. */
  states?: string[]
  timestamp?: string
  version?: number
}

export type MapAlarmState = {
  id?: string
  alarm?: boolean
  /** How many direct children are alarmed; this is what raises the colour up the tree. */
  childrenAlarms?: number
  version?: number
}

export type MapStateDescription = {
  id?: string
  state?: string
  stateDescr?: string
  stateColor?: string
  alarm?: boolean
}

export type MapVisualStates = {
  alarmedObjects?: MapAlarmState[]
  states?: MapObjectState[]
  statesDescr?: MapStateDescription[]
}

export type MapObjectStateList = { states?: MapObjectState[] }
export type MapAlarmStateList = { states?: MapAlarmState[] }
export type MapStateDescriptionList = { descriptions?: MapStateDescription[] }

export type MapEvent = {
  timestamp?: string
  id?: string
  objectId?: string
  eventName?: string
  eventPriority?: number
  param0?: string
  param1?: string
  extraProps?: MapExtraProperty[]
}

export type MapEventList = { events?: MapEvent[] }

export type MapEventsFilter = {
  timeStart?: string
  timeEnd?: string
  /** The named nodes and everything below them; empty is the whole journal. */
  objects?: string[]
  imagesOnly?: boolean
  param0?: string
  param1?: string
  /** Keyset cursor: the id of the last event the caller has. */
  startId?: string
  count?: number
  sortKey?: string
  sortOrder?: string
}

/** What a shape is on its own, as a track point carries it: no name, no node fields. */
export type MapGeoObject = {
  id?: string
  location?: MapGeometry
  radius?: number
  zoomLevel?: string
}

export type MapTrackPoint = {
  /** The node the point belongs to, not a row id: every point of one node carries it. */
  id?: string
  figure?: MapGeoObject
  timestamp?: string
  extraProps?: MapExtraProperty[]
}

export type MapTrackPointList = { tracks?: MapTrackPoint[] }

export type MapTracksByFilterRequest = {
  timeStart?: string
  timeEnd?: string
  propertyFilter?: MapPropertyFilter[]
  startId?: string
  forward?: number
  count?: number
}

export type MapTracksByBoxRequest = {
  box?: MapBox
  timeStart?: string
  timeEnd?: string
  /** Negative means newest first. */
  sort?: number
}

export type MapValueType = {
  doubleValue?: number
  intValue?: number
  stringValue?: string
}

export type MapValue = {
  id?: string
  /** A replica never owns values; the read rewrites it to the replica. */
  ownerId?: string
  name?: string
  value?: MapValueType
  version?: number
}

export type MapValueList = { values?: MapValue[] }

export type MapDiagramCoordinate = {
  top?: number
  left?: number
  width?: number
  height?: number
  /** Degrees clockwise; absent means upright. */
  rotation?: number
}

export type MapDiagram = {
  id?: string
  /** The diagram's own canvas: where and how big it is drawn when it is opened. */
  geometry?: MapDiagramCoordinate
  /** The region of the parent's diagram type this node sits in; the region then gives its place. */
  regionId?: string
  /** The id of a diagram type, not its name. */
  dgrType?: string
  /** A row of `files`, served by api/map/files/{id}. */
  fileId?: string
}

export type MapDiagramList = { diagrams?: MapDiagram[] }

export type MapDiagramTypeRegion = {
  regionKey?: string
  geometry?: MapDiagramCoordinate
  styles?: Record<string, string>
}

export type MapDiagramType = {
  id?: string
  name?: string
  fileId?: string
  regions?: MapDiagramTypeRegion[]
}

export type MapDiagramTypeList = { diagramTypes?: MapDiagramType[] }

export type MapDiagramContent = {
  diagramId?: string
  depth?: number
  children?: MapMarker[]
  content?: MapDiagram[]
  dgrTypes?: MapDiagramType[]
  parents?: MapMarker[]
  visuals?: MapVisual[]
}

export type MapGetDiagramTypesRequest = {
  ids?: string[]
  names?: string[]
  filter?: string
  startId?: string
  forward?: boolean
  count?: number
}

export type MapLevel = {
  id?: string
  zoomLevel?: string
  zoomMin?: number
  zoomMax?: number
}

export type MapLevelList = { levels?: MapLevel[] }
export type MapGetLevelResponse = { found?: boolean; level?: MapLevel }

export type MapRight = {
  objectId?: string
  /** A Keycloak role name or a user name. */
  subject?: string
  /** view, edit, and for a camera view_live, view_archive, ptz, control, export. */
  action?: string
}

export type MapRightList = { rights?: MapRight[] }

export type MapRightKey = { objectId?: string; subject?: string; action?: string }
export type MapRightKeyList = { keys?: MapRightKey[] }

/** The enum's value names, as proto JSON spells them. */
export type MapRightsMode = 'RIGHTS_MODE_OPEN' | 'RIGHTS_MODE_ENFORCED'
export type MapRightsModeResponse = { mode?: MapRightsMode }
export type MapSetRightsModeRequest = { mode?: MapRightsMode }

export type MapNodeRights = {
  objectId?: string
  cameraId?: string
  actions?: string[]
}

/** The admin asks about somebody else: the caller in the body is that subject. */
export type MapEffectiveRightsRequest = {
  caller?: { username?: string; roles?: string[]; unrestricted?: boolean }
}

export type MapEffectiveRights = {
  mode?: MapRightsMode
  admin?: boolean
  nodes?: MapNodeRights[]
}

export type MapRightActionEntry = { nodeKind?: string; actions?: string[] }
export type MapRightActionTable = { entries?: MapRightActionEntry[] }

export type MapOperationStatus = { success?: boolean; message?: string }

/** Whether the demo world is installed, and how many rows it owns. */
export type MapDemoStatus = { installed?: boolean; version?: number; entities?: number }

/** What installing or removing the world did: the map rows and the video wall together. */
export type MapDemoReport = {
  installed?: boolean
  version?: number
  created?: number
  updated?: number
  removed?: number
  skippedCameras?: string[]
  monitorCameras?: string[]
  /** Set when VmsCfg could not be reached: the world is installed, its video wall is not. */
  wallError?: string
}

export type MapWatchItem = { objectId?: string; cameraId?: string }

export type MapWatchAction =
  | 'set_ids2update'
  | 'set_ids2delete'
  | 'set_visual_states'
  | 'on_values_changed'

export type MapWatchMessage = { action?: string; items?: MapWatchItem[] }

// ── The alarm entity ────────────────────────────────────────────────────────────────────────────

/** One operator alarm: the ALARM state says something is wrong, this says who must deal with it. */
export type MapAlarm = {
  id?: string
  objectId?: string
  definitionId?: string
  subscriptionId?: string
  /** "sensor" | "retention" | "subscription". */
  source?: string
  dedupKey?: string
  conditionActive?: boolean
  /** "active" | "acknowledged" | "returned" | "closed". */
  status?: string
  /** "warning" | "critical". */
  severity?: string
  raisedAt?: string
  clearedAt?: string
  expectedEndTs?: string
  acknowledgedAt?: string
  acknowledgedBy?: string
  ackComment?: string
  closedAt?: string
  closeReason?: string
  closeComment?: string
  closedBy?: string
  sourceEventId?: string
  /** Whether this caller may acknowledge the row now; the service computes it. */
  canAck?: boolean
  objectName?: string
  cameraId?: string
}

/** The last row of the previous page: (time, id), as the branch that produced it orders them. */
export type MapAlarmCursor = { after?: string; afterId?: string }

export type MapAlarmPage = { alarms?: MapAlarm[]; hasMore?: boolean; next?: MapAlarmCursor }

/** Open rows and the recently closed ones, each with its own cursor. */
export type MapAlarmList = { open?: MapAlarmPage; closed?: MapAlarmPage }

export type MapGetAlarmsRequest = {
  pageSize?: number
  openAfter?: MapAlarmCursor
  closedAfter?: MapAlarmCursor
  closedWindowMinutes?: number
}
