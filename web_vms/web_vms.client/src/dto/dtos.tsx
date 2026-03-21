export type PlayerStateDto = {
  cameraId: string
  clientId: string
  position: string
}

export const toIsoUtc = (value: string) => {
  const date = new Date(value)
  return isNaN(date.getTime()) ? null : date.toISOString()
}