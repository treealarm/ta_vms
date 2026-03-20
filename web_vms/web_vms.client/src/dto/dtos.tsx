export type PlayerInfo = {
  cameraId: string
  clientId: string
  startTime: string
}

export const toIsoUtc = (value: string) => {
  const date = new Date(value)
  return isNaN(date.getTime()) ? null : date.toISOString()
}