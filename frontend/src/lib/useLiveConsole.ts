import { useEffect, useState } from 'react'
import api from '@/lib/httpClient'
import { useAuthStore } from '@/stores/authStore'

const maxText = 262144
const retryDelay = 1500

export function useLiveConsole(serverId: string, enabled: boolean) {
  const token = useAuthStore((state) => state.token)
  const [generation, setGeneration] = useState(0)
  const [latest, setLatest] = useState<{ serverId: string; text: string } | null>(null)
  const [failedFor, setFailedFor] = useState<string | null>(null)

  useEffect(() => {
    if (!enabled || !token) return
    const request = new AbortController()
    const baseUrl = (api.defaults.baseURL ?? '/api').replace(/\/$/, '')
    let retryTimer: number | undefined

    async function follow() {
      try {
        const response = await fetch(`${baseUrl}/servers/${encodeURIComponent(serverId)}/logs/live?tail=200`, {
          headers: { Authorization: `Bearer ${token}` },
          signal: request.signal,
          credentials: 'omit',
          cache: 'no-store',
          redirect: 'error',
        })
        if (response.status === 401) {
          const auth = useAuthStore.getState()
          if (auth.token === token) auth.logout()
          return
        }
        if (!response.ok || !response.body) throw new Error('Console unavailable')
        setFailedFor(null)
        const reader = response.body.getReader()
        const decoder = new TextDecoder()
        let text = ''
        try {
          while (!request.signal.aborted) {
            const { done, value } = await reader.read()
            if (done) break
            text = (text + decoder.decode(value, { stream: true })).slice(-maxText)
            if (!request.signal.aborted) setLatest({ serverId, text })
          }
          text = (text + decoder.decode()).slice(-maxText)
          if (!request.signal.aborted) setLatest({ serverId, text })
        } finally {
          reader.releaseLock()
        }
      } catch {
        if (!request.signal.aborted) setFailedFor(serverId)
      }
      if (!request.signal.aborted) retryTimer = window.setTimeout(() => void follow(), retryDelay)
    }
    void follow()
    return () => {
      request.abort()
      window.clearTimeout(retryTimer)
    }
  }, [enabled, generation, serverId, token])

  const failed = enabled && failedFor === serverId
  return {
    text: enabled && !failed && latest?.serverId === serverId ? latest.text : null,
    failed,
    reconnect: () => setGeneration((current) => current + 1),
  }
}
