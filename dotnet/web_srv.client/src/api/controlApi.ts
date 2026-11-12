import type { ControlObjectCursorDto, ControlObjectDto, ControlObjectPageDto } from '../dto/dtos'
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
