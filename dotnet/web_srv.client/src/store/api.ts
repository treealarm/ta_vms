import { responseErrorText } from '../api/requestJson'
import type { ApiResponseDto } from '../dto/dtos'

/** The message of a failed response; kept as a name because several callers read it directly. */
export const getErrorMessage = (response: Response): Promise<string> => responseErrorText(response)

export const parseJson = async <T>(response: Response): Promise<T> => {
  if (!response.ok) {
    throw new Error(await responseErrorText(response))
  }

  return response.json() as Promise<T>
}

/**
 * A body whose endpoint answers `{success, message}`.
 *
 * A 200 with `success:false` is a refusal, not an empty success: the caller must see it as a
 * rejection carrying the server's sentence, or it closes the detail and reports a deletion that
 * never happened.
 */
export const parseApiResponse = async (response: Response): Promise<ApiResponseDto> => {
  const body = await parseJson<ApiResponseDto>(response)
  if (body?.success === false) {
    throw new Error(body.message || 'The operation failed')
  }
  return body
}
