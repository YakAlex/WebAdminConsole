/** Базова помилка REST-запиту (не 2xx, не auth). */
export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

/** 401/403 — окремий тип, щоб UI міг відрізнити "немає доступу" від "щось зламалось". */
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
 * GET-запит до /api/*. `credentials: 'include'` — щоб Windows-автентифікація
 * (Negotiate) коректно проходила через Vite dev-proxy до бекенду.
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

  return (await response.json()) as T
}

/**
 * POST/DELETE до /api/* з JSON-тілом. На non-2xx намагається витягти
 * `{ error: "..." }` з тіла відповіді (саме так бекенд повертає
 * BadRequest — напр. CredentialsController/TelegramUsersController) —
 * інакше форма показала б лише "Bad Request" без пояснення чому.
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
      // тіло не JSON — лишаємо statusText
    }
    throw new ApiError(response.status, message)
  }

  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

export const apiPost = <T = void>(path: string, body?: unknown): Promise<T> => apiSend<T>(path, 'POST', body)

export const apiPut = <T = void>(path: string, body?: unknown): Promise<T> => apiSend<T>(path, 'PUT', body)

export const apiDelete = <T = void>(path: string): Promise<T> => apiSend<T>(path, 'DELETE')
