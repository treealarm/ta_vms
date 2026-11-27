import { describe, expect, it } from 'vitest'
import { responseErrorText } from '../api/requestJson'
import { parseApiResponse } from './api'

const reply = (status: number, body: string, statusText = '') =>
  new Response(body, { status, statusText })

describe('responseErrorText', () => {
  it('reads the error field the gateway writes for gRPC failures', async () => {
    expect(await responseErrorText(reply(500, '{"error":"Camera is unavailable"}')))
      .toBe('Camera is unavailable')
  })

  it('reads a message field when that is what the endpoint wrote', async () => {
    expect(await responseErrorText(reply(400, '{"message":"A subscription needs a name"}')))
      .toBe('A subscription needs a name')
  })

  it('uses a body that is not JSON as it is', async () => {
    expect(await responseErrorText(reply(502, 'Bad gateway'))).toBe('Bad gateway')
  })

  it('falls back to the status text for an empty body', async () => {
    expect(await responseErrorText(reply(503, '', 'Service Unavailable'))).toBe('Service Unavailable')
  })
})

describe('parseApiResponse', () => {
  it('returns a successful body', async () => {
    await expect(parseApiResponse(reply(200, '{"success":true,"message":"OK"}')))
      .resolves.toEqual({ success: true, message: 'OK' })
  })

  it('rejects a 200 success:false with the sentence the server wrote', async () => {
    await expect(parseApiResponse(reply(200, '{"success":false,"message":"camera in use"}')))
      .rejects.toThrow('camera in use')
  })

  it('rejects a 200 success:false even when the server wrote no message', async () => {
    await expect(parseApiResponse(reply(200, '{"success":false,"message":""}')))
      .rejects.toThrow('The operation failed')
  })

  it('surfaces the gateway error field on an HTTP failure', async () => {
    await expect(parseApiResponse(reply(500, '{"error":"Camera is unavailable"}')))
      .rejects.toThrow('Camera is unavailable')
  })
})
