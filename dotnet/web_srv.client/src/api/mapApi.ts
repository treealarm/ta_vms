import { enc, jsonRequest, requestJson } from './requestJson'
import type {
  MapAlarm,
  MapAlarmList,
  MapAlarmStateList,
  MapBox,
  MapCreateReplicaRequest,
  MapDemoReport,
  MapDemoStatus,
  MapDiagram,
  MapDiagramContent,
  MapDiagramList,
  MapDiagramType,
  MapDiagramTypeList,
  MapEffectiveRights,
  MapEffectiveRightsRequest,
  MapEventList,
  MapEventsFilter,
  MapFileInfo,
  MapFigure,
  MapFigures,
  MapGetAlarmsRequest,
  MapGetByParentResponse,
  MapGetDiagramTypesRequest,
  MapGetLevelResponse,
  MapLevel,
  MapLevelList,
  MapMarker,
  MapMarkerList,
  MapObjProps,
  MapObject,
  MapObjectList,
  MapObjectPropertiesList,
  MapObjectStateList,
  MapOperationStatus,
  MapPropertyFilter,
  MapRightActionTable,
  MapRightKeyList,
  MapRightList,
  MapRightsMode,
  MapRightsModeResponse,
  MapStateDescription,
  MapStateDescriptionList,
  MapTrackPointList,
  MapTracksByBoxRequest,
  MapTracksByFilterRequest,
  MapValueList,
  MapVisual,
  MapVisualStates,
} from '../dto/mapDtos'

const BASE = '/api/map'
const ADMIN = `${BASE}/admin`

/** What a watch stream follows: a rectangle or an exact set of nodes. Changing it is a new
 *  stream — the gateway takes the scope as a connection parameter. */
export type MapLiveScope = { box: MapBox } | { ids: string[] }

const query = (params: Record<string, string | number | undefined>): string => {
  const qs = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== '') qs.set(key, String(value))
  }
  return qs.toString()
}

const post = <T>(url: string, body: unknown) => requestJson<T>(url, jsonRequest('POST', body))
const put = <T>(url: string, body: unknown) => requestJson<T>(url, jsonRequest('PUT', body))

/**
 * The map reads, one function per rpc the gateway exposes. Bodies are the proto JSON shapes: a
 * bare array where the controller takes one, otherwise the request message as written.
 */
export const mapApi = {
  // ── Objects ────────────────────────────────────────────────────────────────────────────────
  getByParent: (parentId: string, count = 1000, startId?: string) =>
    requestJson<MapGetByParentResponse>(
      `${BASE}/objects/parent?${query({ parentId, count, startId })}`),

  getByIds: (ids: string[]) => post<MapFigures>(`${BASE}/objects/by-ids`, ids),

  getMarkersByIds: (ids: string[]) => post<MapMarkerList>(`${BASE}/objects/markers`, ids),

  getByBox: (box: MapBox, propertyFilter: MapPropertyFilter[] = []) =>
    post<MapFigures>(`${BASE}/objects/by-box`, { box, propertyFilter }),

  getByName: (name: string) =>
    requestJson<MapMarkerList>(`${BASE}/objects/by-name?${query({ name })}`),

  /** The logical tree searched without descending it: a node name, a part token or a camera id. */
  searchDeviceNodes: (search: string, limit = 50) =>
    requestJson<MapMarkerList>(`${BASE}/objects/search?${query({ query: search, limit })}`),

  getByParams: (
    props: MapPropertyFilter[], startId?: string, forward = 0, count = 100,
  ) => post<MapFigures>(`${BASE}/objects/by-params`, { props, startId, forward, count }),

  getObjProps: (id: string) =>
    requestJson<MapObjProps>(`${BASE}/objects/${enc(id)}/props`),

  // ── States ─────────────────────────────────────────────────────────────────────────────────
  getStates: (ids: string[]) => post<MapObjectStateList>(`${BASE}/states`, ids),

  getVisualStates: (ids: string[]) =>
    post<MapVisualStates>(`${BASE}/states/visual`, ids),

  /** An empty list asks for every alarmed node the caller may see. */
  getAlarmedStates: (ids: string[] = []) =>
    post<MapAlarmStateList>(`${BASE}/states/alarmed`, ids),

  getStateDescriptions: (states: string[] = []) =>
    post<MapStateDescriptionList>(`${BASE}/states/descriptions`, states),

  // ── Alarms ─────────────────────────────────────────────────────────────────────────────────
  // Two keyset pages in one call: open rows and the ones closed in the last quarter of an hour.
  getAlarms: (request: MapGetAlarmsRequest = {}) =>
    post<MapAlarmList>(`${BASE}/alarms`, request),

  /** Refused with 409 when the condition is still active and the node is armed. */
  acknowledgeAlarm: (id: string, comment: string) =>
    post<MapAlarm>(`${BASE}/alarms/${enc(id)}/acknowledge`, { comment }),

  /** Only an alarm whose condition is already gone may be closed by hand. */
  closeAlarm: (id: string, reason: 'false_alarm' | 'other', comment: string) =>
    post<MapAlarm>(`${BASE}/alarms/${enc(id)}/close`, { reason, comment }),

  // ── Events ─────────────────────────────────────────────────────────────────────────────────
  getEvents: (filter: MapEventsFilter) =>
    post<MapEventList>(`${BASE}/events/filter`, filter),

  // ── Tracks ─────────────────────────────────────────────────────────────────────────────────
  getTracksByFilter: (request: MapTracksByFilterRequest) =>
    post<MapTrackPointList>(`${BASE}/tracks/filter`, request),

  getTracksByBox: (request: MapTracksByBoxRequest) =>
    post<MapTrackPointList>(`${BASE}/tracks/box`, request),

  // ── Values ─────────────────────────────────────────────────────────────────────────────────
  getValuesByOwners: (ids: string[]) =>
    post<MapValueList>(`${BASE}/values/by-owners`, ids),

  // ── Diagrams ───────────────────────────────────────────────────────────────────────────────
  getDiagramContent: (diagramId: string, depth: number) =>
    requestJson<MapDiagramContent>(
      `${BASE}/diagrams/${enc(diagramId)}/content?${query({ depth })}`),

  getDiagramsByIds: (ids: string[]) =>
    post<MapDiagramList>(`${BASE}/diagrams/by-ids`, ids),

  getDiagramTypes: (request: MapGetDiagramTypesRequest = {}) =>
    post<MapDiagramTypeList>(`${BASE}/diagrams/types`, request),

  /** Draws a device from a template an administrator picked. */
  attachDeviceDiagram: (body: {
    sourceKind: string
    sourceId: string
    diagramTypeId: string
  }) => post<MapMarker>(`${ADMIN}/device-diagram`, body),

  // ── Levels ─────────────────────────────────────────────────────────────────────────────────
  getLevels: () => requestJson<MapLevelList>(`${BASE}/levels`),

  getLevel: (zoomLevel: string) =>
    requestJson<MapGetLevelResponse>(`${BASE}/levels/${enc(zoomLevel)}`),

  // ── Rights ─────────────────────────────────────────────────────────────────────────────────
  getRights: (objectIds: string[]) =>
    requestJson<MapRightList>(`${BASE}/rights?${query({ objectId: objectIds.join(',') })}`),

  getEffectiveRights: (request: MapEffectiveRightsRequest) =>
    post<MapEffectiveRights>(`${BASE}/rights/effective`, request),

  getRightActions: () => requestJson<MapRightActionTable>(`${BASE}/rights/actions`),

  getRightsMode: () => requestJson<MapRightsModeResponse>(`${BASE}/rights/mode`),

  setRightsMode: (mode: MapRightsMode) =>
    put<MapOperationStatus>(`${BASE}/rights/mode`, { mode }),

  updateRights: (rights: MapRightList['rights']) =>
    put<MapOperationStatus>(`${BASE}/rights`, { rights }),

  deleteRights: (keys: MapRightKeyList['keys']) =>
    requestJson<MapOperationStatus>(`${BASE}/rights`, jsonRequest('DELETE', { keys })),

  // ── Admin writes ───────────────────────────────────────────────────────────────────────────
  // Editing belongs to Admin → Maps; every endpoint under api/map/admin is AdminOnly. The bodies
  // are the proto messages, and a delete takes `{ ids: [...] }`.
  updateObjects: (objects: MapObject[]) => put<MapObjectList>(`${ADMIN}/objects`, { objects }),

  deleteObjects: (ids: string[]) =>
    requestJson<MapOperationStatus>(`${ADMIN}/objects`, jsonRequest('DELETE', { ids })),

  updateProperties: (objects: MapObjectPropertiesList['objects'], replaceProps = true) =>
    put<MapOperationStatus>(`${ADMIN}/properties`, { objects, replaceProps }),

  updateVisual: (visuals: MapVisual[]) => put<MapOperationStatus>(`${ADMIN}/visual`, { visuals }),

  updateFigures: (figs: MapFigure[], addTracks = false) =>
    put<MapFigures>(`${ADMIN}/figures`, { figs, addTracks }),

  createReplica: (request: MapCreateReplicaRequest) =>
    post<MapMarker>(`${ADMIN}/replicas`, request),

  updateStateDescriptions: (descriptions: MapStateDescription[]) =>
    put<MapOperationStatus>(`${ADMIN}/state-descriptions`, { descriptions }),

  updateLevels: (levels: MapLevel[]) => put<MapLevelList>(`${ADMIN}/levels`, { levels }),

  deleteLevels: (ids: string[]) =>
    requestJson<MapOperationStatus>(`${ADMIN}/levels`, jsonRequest('DELETE', { ids })),

  updateDiagrams: (diagrams: MapDiagram[]) => put<MapDiagramList>(`${ADMIN}/diagrams`, { diagrams }),

  deleteDiagrams: (ids: string[]) =>
    requestJson<MapOperationStatus>(`${ADMIN}/diagrams`, jsonRequest('DELETE', { ids })),

  updateDiagramTypes: (diagramTypes: MapDiagramType[]) =>
    put<MapDiagramTypeList>(`${ADMIN}/diagram-types`, { diagramTypes }),

  deleteDiagramTypes: (ids: string[]) =>
    requestJson<MapOperationStatus>(`${ADMIN}/diagram-types`, jsonRequest('DELETE', { ids })),

  // ── Demo world ─────────────────────────────────────────────────────────────────────────────
  // The world itself is VmsMap's; its video wall is VmsCfg's. The gateway calls both and answers
  // one report.
  getDemoStatus: () => requestJson<MapDemoStatus>(`${ADMIN}/demo`),

  installDemo: () => requestJson<MapDemoReport>(`${ADMIN}/demo/install`, { method: 'POST' }),

  removeDemo: () => requestJson<MapDemoReport>(`${ADMIN}/demo/remove`, { method: 'POST' }),

  /** A picture row for a diagram background, an icon or a plan. 20 MiB at most. */
  uploadFile: async (file: File): Promise<MapFileInfo> => {
    const body = new FormData()
    body.append('file', file)
    return requestJson<MapFileInfo>(`${ADMIN}/files`, { method: 'POST', body })
  },
}

/** A file row (`files`) as the browser loads it; the Bearer travels through the fetch wrapper. */
export const mapFileUrl = (fileId: string): string => `${BASE}/files/${enc(fileId)}`

/** The SSE URL for a watch scope. `values` also streams value changes for the followed nodes. */
export function mapLiveUrl(scope: MapLiveScope, values = false): string {
  const params: Record<string, string | number | undefined> = { values: values ? 'true' : undefined }

  if ('ids' in scope) {
    params.ids = scope.ids.join(',')
  } else {
    const { wn, es, zoom, canvasId } = scope.box
    params.north = wn?.lat
    params.west = wn?.lon
    params.south = es?.lat
    params.east = es?.lon
    params.zoom = zoom
    params.canvasId = canvasId
  }

  return `${BASE}/live?${query(params)}`
}
