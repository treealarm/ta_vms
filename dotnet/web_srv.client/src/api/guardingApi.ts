import type { GuardSensorCursorDto, GuardSensorPageDto } from '../dto/dtos'
import { requestJson } from './requestJson'

export type GuardSensorsQuery = {
  /** The space and everything under it; omitted asks for everything the caller may see. */
  spaceId?: string
  pageSize?: number
  after?: GuardSensorCursorDto | null
}

/**
 * One page of the guarding list. The server filters by the caller's `view_live` before the page
 * is cut, so an empty answer is a real one; the cursor is the last row's (name, id), not an offset.
 */
export async function listGuardSensors(query: GuardSensorsQuery): Promise<GuardSensorPageDto> {
  const params = new URLSearchParams()
  if (query.spaceId) params.set('spaceId', query.spaceId)
  if (query.pageSize) params.set('pageSize', String(query.pageSize))
  if (query.after) {
    params.set('afterName', query.after.afterName)
    params.set('afterId', query.after.afterId)
  }

  const queryString = params.toString()
  return requestJson<GuardSensorPageDto>(
    `/api/guarding/sensors${queryString ? `?${queryString}` : ''}`,
  )
}
