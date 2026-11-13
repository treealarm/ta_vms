import type {
  AcknowledgeZoneResultDto,
  ArmMode,
  CameraArmMode,
  CameraArmStateDto,
  ControlObjectCursorDto,
  ControlObjectDto,
  ControlObjectPageDto,
  NodeArmingDto,
} from '../dto/dtos'
import { apiFetch } from './apiFetch'
import { requestJson } from './requestJson'

export type ControlObjectsQuery = {
  /** The space and everything under it; omitted asks for everything the caller may see. */
  spaceId?: string
  /** '' asks for every kind; otherwise 'camera', 'digital_input' or 'relay'. */
  kind?: string
  /** One device's parts, wherever they stand: the administrator's placement palette uses this. */
  cameraId?: string
  pageSize?: number
  after?: ControlObjectCursorDto | null
}

/**
 * One page of the control list. The server filters by the caller's `view_live` before the page
 * is cut, so an empty answer is a real one; the cursor is the last row's (name, id), not an offset.
 */
export async function listControlObjects(query: ControlObjectsQuery): Promise<ControlObjectPageDto> {
  const params = new URLSearchParams()
  if (query.spaceId) params.set('spaceId', query.spaceId)
  if (query.kind) params.set('kind', query.kind)
  if (query.cameraId) params.set('cameraId', query.cameraId)
  if (query.pageSize) params.set('pageSize', String(query.pageSize))
  if (query.after) {
    params.set('afterName', query.after.afterName)
    params.set('afterId', query.after.afterId)
  }

  const queryString = params.toString()
  return requestJson<ControlObjectPageDto>(
    `/api/control/objects${queryString ? `?${queryString}` : ''}`,
  )
}

/**
 * The control row of one node — canonical or any replica of it. The map's inspector selects a node,
 * and a picture of a sensor must act on the sensor. null when the node is not controllable or the
 * caller may not see it: the server answers both the same way on purpose.
 */
export async function getControlObject(objectId: string): Promise<ControlObjectDto | null> {
  const response = await fetch(`/api/control/objects/${encodeURIComponent(objectId)}`)
  if (response.status === 404) return null
  const text = await response.text()
  if (!response.ok) {
    let message = text || response.statusText
    try {
      const body = JSON.parse(text) as { error?: string }
      if (body?.error) message = body.error
    } catch {
      // Not JSON: the text itself is the message.
    }
    throw new Error(message)
  }
  return JSON.parse(text) as ControlObjectDto
}

/** The camera's own armed flag — the same command the monitor card's guard toggle sends. */
export async function setCameraArmed(cameraId: string, armed: boolean): Promise<void> {
  await apiFetch(`/api/cameras/${encodeURIComponent(cameraId)}/state`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ armed }),
  })
}

/** A camera's effective flag, and the mode it was decided with. */
export async function getCameraArmState(cameraId: string): Promise<CameraArmStateDto> {
  return apiFetch<CameraArmStateDto>(`/api/cameras/${encodeURIComponent(cameraId)}/state`)
}

/**
 * The administrator's setting: `inherit` hands the flag back to the zone the camera stands in, and
 * the map derives it from there. An operator's toggle stays the explicit command.
 */
export async function setCameraArmMode(cameraId: string, armMode: CameraArmMode): Promise<void> {
  await apiFetch(`/api/cameras/${encodeURIComponent(cameraId)}/arm-mode`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ armMode }),
  })
}

/**
 * The guard state of any node — a sensor, a camera, a space or a zone. A container's answer names
 * the zone that settled it, so the browser paints "on guard through a parent" differently from
 * "set here"; it never computes the effective state itself.
 */
export async function getNodeArming(objectId: string): Promise<NodeArmingDto | null> {
  const response = await fetch(`/api/control/objects/${encodeURIComponent(objectId)}/arming`)
  if (response.status === 404) return null
  const text = await response.text()
  if (!response.ok) throw new Error(text || response.statusText)
  return JSON.parse(text) as NodeArmingDto
}

/**
 * The one write behind every guard switch. The answer is what the node resolves to afterwards:
 * disarming a sensor inside an armed zone leaves it on guard, and only the server knows that.
 * An operator never sends 'inherit' to a device part — that is the administrator's setting.
 */
export async function setNodeArming(objectId: string, mode: ArmMode): Promise<NodeArmingDto> {
  return apiFetch<NodeArmingDto>(`/api/control/objects/${encodeURIComponent(objectId)}/arming`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ mode }),
  })
}

/**
 * Accepting a zone: every open alarm of the devices under it. Partial success is the norm — an
 * alarm still on guard, or one another operator already dealt with, comes back as a rejection.
 */
export async function acknowledgeZoneAlarms(
  objectId: string,
  comment?: string,
): Promise<AcknowledgeZoneResultDto> {
  return apiFetch<AcknowledgeZoneResultDto>(
    `/api/control/objects/${encodeURIComponent(objectId)}/alarms/acknowledge`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ comment: comment ?? '' }),
    },
  )
}
