/** Base error for a REST request (non-2xx, non-auth). */
export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

/** 401/403 — a separate type so the UI can distinguish "no access" from "something broke". */
export class ApiAuthError extends ApiError {
  constructor(status: number) {
    super(status, status === 401 ? 'Unauthorized' : 'Forbidden')
    this.name = 'ApiAuthError'
  }
}

export function isAuthError(error: unknown): error is ApiAuthError {
  return error instanceof ApiAuthError
}

/**
 * GET request to /api/*. `credentials: 'include'` is needed for
 * Windows authentication (Negotiate) to correctly pass through the
 * Vite dev proxy to the backend.
 */
export async function apiGet<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response

  try {
    response = await fetch(path, { credentials: 'include', ...init })
  } catch (cause) {
    throw new ApiError(0, cause instanceof Error ? cause.message : 'Network error')
  }

  if (response.status === 401 || response.status === 403) {
    throw new ApiAuthError(response.status)
  }

  if (!response.ok) {
    throw new ApiError(response.status, response.statusText)
  }

  try {
    return (await response.json()) as T
  } catch (cause) {
    throw new ApiError(
      response.status,
      `Failed to parse server response: ${cause instanceof Error ? cause.message : 'invalid JSON'}`,
    )
  }
}

/**
 * POST/DELETE to /api/* with a JSON body. On non-2xx, tries to extract
 * `{ error: "..." }` from the response body (this is how the backend
 * returns BadRequest — e.g. CredentialsController/TelegramUsersController)
 * — otherwise the form would only show "Bad Request" with no
 * explanation why.
 */
async function apiSend<T>(path: string, method: 'POST' | 'PUT' | 'DELETE', body?: unknown): Promise<T> {
  let response: Response

  try {
    response = await fetch(path, {
      method,
      credentials: 'include',
      headers: body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
      body: body !== undefined ? JSON.stringify(body) : undefined,
    })
  } catch (cause) {
    throw new ApiError(0, cause instanceof Error ? cause.message : 'Network error')
  }

  if (response.status === 401 || response.status === 403) {
    throw new ApiAuthError(response.status)
  }

  if (!response.ok) {
    let message = response.statusText
    try {
      const data: unknown = await response.json()
      if (data && typeof data === 'object' && 'error' in data && typeof data.error === 'string') {
        message = data.error
      }
    } catch {
      // body isn't JSON — keep statusText
    }
    throw new ApiError(response.status, message)
  }

  if (response.status === 204) return undefined as T

  try {
    return (await response.json()) as T
  } catch (cause) {
    throw new ApiError(
      response.status,
      `Failed to parse server response: ${cause instanceof Error ? cause.message : 'invalid JSON'}`,
    )
  }
}

export const apiPost = <T = void>(path: string, body?: unknown): Promise<T> => apiSend<T>(path, 'POST', body)

export const apiPut = <T = void>(path: string, body?: unknown): Promise<T> => apiSend<T>(path, 'PUT', body)

export const apiDelete = <T = void>(path: string): Promise<T> => apiSend<T>(path, 'DELETE')
