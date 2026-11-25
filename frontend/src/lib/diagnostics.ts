import axios from 'axios'

const points = {
  ANUI001: 'Caiu aqui: requisição à API / Failed here: API request',
  ANUI002: 'API sem resposta ou tempo esgotado / API unavailable or timed out',
  ANUI003: 'Caiu aqui: renderização da tela / Failed here: page rendering',
  ANUI004: 'Falha assíncrona não tratada / Unhandled asynchronous failure',
  ANUI005: 'Caiu aqui: inicialização do frontend / Failed here: frontend startup',
  ANUI006: 'Erro de execução ou carregamento de recurso / Runtime or resource loading error',
} as const

export type UiDiagnosticCode = keyof typeof points

const safeReference = (value: unknown, pattern: RegExp): string | undefined =>
  typeof value === 'string' && pattern.test(value) ? value : undefined

export function reportUiFailure(code: UiDiagnosticCode) {
  // No Error/AxiosError/event object: those may contain credentials or form values.
  console.warn(`[${code}] ${points[code]}`)
}

export function reportApiFailure(error: unknown) {
  if (!axios.isAxiosError(error) || axios.isCancel(error)) return
  const status = error.response?.status
  const code = error.response ? 'ANUI001' : 'ANUI002'
  const data = error.response?.data as { diagnosticCode?: unknown; traceId?: unknown } | undefined
  const apiCode = safeReference(data?.diagnosticCode ?? error.response?.headers?.['x-diagnostic-code'], /^AN\d{4}$/)
  const traceId = safeReference(data?.traceId ?? error.response?.headers?.['x-trace-id'], /^[A-Za-z0-9:._-]{1,128}$/)
  // Classify the route; never print the URL, query, body, headers or error message.
  const route = error.config?.url?.split('?')[0] ?? ''
  const area = ['auth', 'servers', 'nodes', 'admin', 'deployment', 'cartridges'].find(value => route.startsWith(`/${value}/`) || route === `/${value}`) ?? 'api'
  const method = error.config?.method?.toUpperCase()
  console.warn(`[${code}] ${points[code]}`, {
    area,
    method: method && ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'OPTIONS'].includes(method) ? method : 'unknown',
    status,
    apiCode,
    traceId,
  })
}

export function getDiagnosticReference(error: unknown): string {
  if (!axios.isAxiosError(error)) return ''
  const data = error.response?.data as { diagnosticCode?: unknown; traceId?: unknown } | undefined
  const code = safeReference(data?.diagnosticCode ?? error.response?.headers?.['x-diagnostic-code'], /^AN\d{4}$/)
  const trace = safeReference(data?.traceId ?? error.response?.headers?.['x-trace-id'], /^[A-Za-z0-9:._-]{1,128}$/)
  return code ? ` [${code}${trace ? ` · ${trace}` : ''}]` : ''
}

export function installGlobalDiagnostics() {
  const onError = () => reportUiFailure('ANUI006')
  const onRejection = () => reportUiFailure('ANUI004')
  window.addEventListener('error', onError, true)
  window.addEventListener('unhandledrejection', onRejection)
  return () => {
    window.removeEventListener('error', onError, true)
    window.removeEventListener('unhandledrejection', onRejection)
  }
}
