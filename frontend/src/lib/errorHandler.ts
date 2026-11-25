import { AxiosError } from 'axios'
import { getDiagnosticReference } from '@/lib/diagnostics'

interface ApiErrorResponse {
  message?: string
  title?: string
  errors?: Record<string, string[]>
}

/**
 * Extracts a user-friendly error message from an unknown caught error.
 * Handles AxiosError responses, native Error objects, and unknown types.
 */
export function getErrorMessage(error: unknown, fallback = 'An unexpected error occurred'): string {
  if (error instanceof AxiosError) {
    const data = error.response?.data as ApiErrorResponse | undefined
    return (data?.message || data?.title || error.message || fallback) + getDiagnosticReference(error)
  }

  if (error instanceof Error) {
    return error.message || fallback
  }

  if (typeof error === 'string') {
    return error
  }

  return fallback
}
