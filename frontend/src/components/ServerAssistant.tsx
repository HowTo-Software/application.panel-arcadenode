import { useState, type FormEvent } from 'react'
import { useMutation } from '@tanstack/react-query'
import { AxiosError } from 'axios'
import api from '@/lib/httpClient'

const preview = import.meta.env.DEV && import.meta.env.VITE_DEMO_MODE === 'true'

export function ServerAssistant({ serverId }: { serverId: string }) {
  const [question, setQuestion] = useState('')
  const [includeRuntime, setIncludeRuntime] = useState(false)
  const ask = useMutation({
    mutationFn: async (input: { question: string; includeRuntime: boolean }) => (
      await api.post<{ answer: string; runtimeIncluded: boolean }>(
        `/servers/${serverId}/assistant`, input, { timeout: 130_000 }
      )
    ).data,
  })
  const status = ask.error instanceof AxiosError ? ask.error.response?.status : undefined
  const error = status === 503 ? 'O assistente local ainda não está configurado.'
    : status === 429 ? 'O assistente está ocupado. Tente novamente em instantes.'
      : status === 504 ? 'O modelo local atingiu o limite de tempo.'
        : 'O assistente local não conseguiu responder.'

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (question.trim() && !preview && !ask.isPending) ask.mutate({ question: question.trim(), includeRuntime })
  }

  return (
    <section className="rounded-xl border border-panel-border bg-panel-card p-5" aria-labelledby="server-assistant-title">
      <h3 id="server-assistant-title" className="font-semibold">Assistente local</h3>
      <p className="mt-1 text-sm text-gray-400">Tire dúvidas sobre configuração, desempenho e operação deste servidor.</p>
      <form className="mt-4 space-y-2" onSubmit={submit}>
        <label className="block text-sm font-medium" htmlFor="assistant-question">Sua pergunta</label>
        <textarea id="assistant-question" rows={4} maxLength={2000}
          className="w-full resize-y rounded-lg border border-panel-border bg-panel-dark px-3 py-2"
          placeholder="Como posso reduzir o uso de memória deste servidor?" value={question}
          onChange={(event) => setQuestion(event.target.value)} disabled={preview || ask.isPending} required />
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={includeRuntime} onChange={(event) => setIncludeRuntime(event.target.checked)}
            disabled={preview || ask.isPending} className="accent-primary-500" />
          Incluir estado, CPU e memória atuais
        </label>
        <button className="button button-primary" disabled={preview || ask.isPending || !question.trim()}>
          {ask.isPending ? 'Consultando…' : 'Perguntar'}
        </button>
      </form>
      {preview && <p className="mt-3 text-sm text-gray-400">As respostas ficam disponíveis no painel conectado.</p>}
      {ask.isError && <p role="alert" className="mt-3 text-sm text-red-400">{error}</p>}
      {ask.data && (
        <section className="mt-4 border-t border-panel-border pt-3" aria-labelledby="assistant-answer" aria-live="polite">
          <h4 id="assistant-answer" className="font-medium">Resposta</h4>
          {ask.variables?.includeRuntime && (
            <p className="mt-1 text-sm text-gray-400">
              {ask.data.runtimeIncluded ? 'Amostra de execução incluída.' : 'Amostra indisponível. A resposta usa a configuração do painel.'}
            </p>
          )}
          <p className="mt-2 whitespace-pre-wrap break-words text-sm">{ask.data.answer}</p>
        </section>
      )}
    </section>
  )
}
