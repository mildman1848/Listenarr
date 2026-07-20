export interface ApiValidationErrorPayload {
  code?: string
  field?: string
  message: string
  resolvedDestination?: string | null
}

type ApiErrorWithBody = Error & {
  status?: number
  body?: string
}

export function getApiValidationError(
  error: unknown,
  expectedField?: string,
): ApiValidationErrorPayload | null {
  if (!(error instanceof Error)) return null

  const candidate = error as ApiErrorWithBody
  if (!candidate.body) return null

  try {
    const payload = JSON.parse(candidate.body) as Partial<ApiValidationErrorPayload>
    if (typeof payload.message !== 'string' || payload.message.trim().length === 0) {
      return null
    }
    if (expectedField && payload.field !== expectedField) return null

    return {
      code: typeof payload.code === 'string' ? payload.code : undefined,
      field: typeof payload.field === 'string' ? payload.field : undefined,
      message: payload.message,
      resolvedDestination:
        typeof payload.resolvedDestination === 'string' || payload.resolvedDestination === null
          ? payload.resolvedDestination
          : undefined,
    }
  } catch {
    return null
  }
}
