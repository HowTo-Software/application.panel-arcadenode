import { useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { getErrorMessage } from '@/lib/errorHandler'
import { usePageTitle } from '@/lib/usePageTitle'
import { adminService } from '@/services/api'
import type { MigrationAction, ServerMigration } from '@/types'

const pageSize = 12
const maximumReceiptBytes = 8192
const demo = import.meta.env.DEV && import.meta.env.VITE_DEMO_MODE === 'true'
const dateFormat = new Intl.DateTimeFormat('pt-BR', { dateStyle: 'short', timeStyle: 'short' })
const stages = {
  AwaitingVerification: { label: 'Aguardando verificação', style: 'bg-amber-500/10 text-amber-300' },
  Prepared: { label: 'Container preparado', style: 'bg-blue-500/10 text-blue-300' },
  Released: { label: 'Liberado', style: 'bg-emerald-500/10 text-emerald-300' },
} as const

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

async function readReceipt(file: File, serverId: string, kind: 'verification' | 'source') {
  if (file.size === 0 || file.size > maximumReceiptBytes) throw new Error('Selecione um relatório JSON de até 8 KiB.')
  const text = await file.text()
  let receipt: unknown
  try { receipt = JSON.parse(text) }
  catch { throw new Error('O arquivo selecionado precisa ser um relatório JSON válido.') }
  if (!isObject(receipt)) throw new Error('Selecione o relatório gerado para esta etapa da migração.')
  const belongsToServer = (candidate: unknown) => typeof candidate === 'string' && candidate.toLowerCase() === serverId.toLowerCase()
  if (kind === 'verification') {
    if (receipt.mode !== 'verify_volume' || receipt.matches !== true || !isObject(receipt.source) || !isObject(receipt.destination))
      throw new Error('Selecione o relatório de verificação com a origem e o destino correspondentes.')
    if (!belongsToServer(receipt.source.server_id) || !belongsToServer(receipt.destination.server_id))
      throw new Error('Os UUIDs do relatório precisam corresponder ao servidor selecionado.')
  } else {
    if (receipt.schema_version !== 2 || receipt.writes_fenced !== true || !isObject(receipt.source_process))
      throw new Error('Gere uma nova confirmação da origem com o preflight atualizado e as escritas bloqueadas.')
    if (!belongsToServer(receipt.server_id)) throw new Error('O UUID da confirmação precisa corresponder ao servidor selecionado.')
  }
  return receipt
}

export default function Migrations() {
  usePageTitle('Migrações')
  const client = useQueryClient()
  const [search, setSearch] = useState('')
  const [query, setQuery] = useState('')
  const [page, setPage] = useState(1)
  const [selectedId, setSelectedId] = useState<string>()
  const migrations = useQuery({
    queryKey: ['pterodactyl-migrations', page, query],
    queryFn: ({ signal }) => adminService.getMigrations(page, pageSize, query, signal),
  })
  const submit = useMutation({
    mutationFn: async ({ id, action, file, destination }: { id: string; action: MigrationAction; file: File; destination?: File }) => {
      let receipt
      if (action === 'release-prepared') {
        if (!destination) throw new Error('Selecione a nova verificação do volume de destino.')
        const [source, verification] = await Promise.all([readReceipt(file, id, 'source'), readReceipt(destination, id, 'verification')])
        receipt = { ...source, destination_verification: verification }
      } else {
        receipt = await readReceipt(file, id, 'verification')
      }
      const json = JSON.stringify(receipt)
      if (new TextEncoder().encode(json).length > maximumReceiptBytes) throw new Error('Os relatórios somados excedem 8 KiB.')
      return adminService.submitMigrationReceipt(id, action, json)
    },
    onSuccess: (_, { id }) => Promise.all([
      client.invalidateQueries({ queryKey: ['pterodactyl-migrations'] }),
      client.invalidateQueries({ queryKey: ['server', id] }),
    ]),
  })
  const selected = migrations.data?.items.find((item) => item.id === selectedId) ?? migrations.data?.items[0]
  const totalPages = Math.max(1, Math.ceil((migrations.data?.total ?? 0) / pageSize))

  function applySearch(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (submit.isPending) return
    submit.reset()
    setQuery(search.trim())
    setSelectedId(undefined)
    setPage(1)
  }

  function changePage(next: number) {
    submit.reset()
    setSelectedId(undefined)
    setPage(next)
  }

  return (
    <div>
      <header className="page-heading">
        <div>
          <h1>Migrações</h1>
          <p>Verifique os volumes do Pterodactyl e libere o destino quando a origem estiver parada.</p>
        </div>
      </header>
      <div className="grid items-start gap-5 lg:grid-cols-[minmax(290px,1fr)_minmax(0,1.6fr)]">
        <section className="min-w-0 rounded-xl border border-panel-border bg-panel-card p-5" aria-labelledby="migration-servers">
          <h2 id="migration-servers" className="font-semibold">Servidores em migração</h2>
          <form className="mt-4" onSubmit={applySearch}>
            <label htmlFor="migration-search" className="mb-1 block text-sm font-medium">Buscar por nome, identificador ou UUID</label>
            <div className="flex flex-wrap gap-2">
              <input id="migration-search" type="search" value={search} onChange={(event) => setSearch(event.target.value)}
                maxLength={100} className="min-w-0 flex-1 rounded-lg border border-panel-border bg-panel-dark px-3 py-2 text-sm" />
              <button className="button button-secondary" disabled={submit.isPending}>Buscar</button>
            </div>
          </form>
          {migrations.isPending && <p className="mt-4 text-sm text-gray-400" role="status">Carregando servidores…</p>}
          {migrations.isError && <div className="mt-4 flex flex-wrap items-center gap-2">
            <p className="text-sm text-red-300" role="alert">Não foi possível carregar as migrações.</p>
            <button type="button" className="button button-secondary" onClick={() => void migrations.refetch()}>Tentar novamente</button>
          </div>}
          {migrations.data?.items.length === 0 && <p className="mt-4 text-sm text-gray-400">
            {query ? 'Nenhum servidor corresponde à busca.' : 'Importe os servidores do Pterodactyl para iniciar a verificação.'}
          </p>}
          {migrations.data && migrations.data.items.length > 0 && (
            <ul className="mt-4 space-y-2" aria-label="Escolher servidor">
              {migrations.data.items.map((item) => (
                <li key={item.id}>
                  <button type="button" aria-pressed={selected?.id === item.id} disabled={submit.isPending}
                    className="flex w-full flex-wrap items-start justify-between gap-x-3 gap-y-2 rounded-lg border border-panel-border bg-panel-dark px-3 py-2.5 text-left hover:border-blue-400/50 aria-pressed:border-blue-400/70"
                    onClick={() => { submit.reset(); setSelectedId(item.id) }}>
                    <span className="min-w-0 break-words">
                      <span className="block text-sm font-semibold">{item.name}</span>
                      <span className="block text-xs text-gray-400">{item.nodeName}</span>
                    </span>
                    <span className={`rounded px-2 py-1 text-xs ${stages[item.stage].style}`}>{stages[item.stage].label}</span>
                  </button>
                </li>
              ))}
            </ul>
          )}
          {migrations.data && totalPages > 1 && (
            <nav className="mt-4 flex flex-wrap items-center justify-between gap-2 text-sm" aria-label="Páginas de migrações">
              <button type="button" className="button button-secondary" disabled={submit.isPending || page <= 1}
                onClick={() => changePage(page - 1)}>Anterior</button>
              <span>Página {page} de {totalPages}</span>
              <button type="button" className="button button-secondary" disabled={submit.isPending || page >= totalPages}
                onClick={() => changePage(page + 1)}>Próxima</button>
            </nav>
          )}
        </section>
        {selected && <MigrationDetails key={`${selected.id}-${selected.stage}`} migration={selected} pending={submit.isPending}
          error={submit.isError ? getErrorMessage(submit.error, 'Falha ao enviar a verificação.') : null}
          onChange={() => submit.reset()}
          onSubmit={(action, file, destination) => submit.mutate({ id: selected.id, action, file, destination })} />}
      </div>
    </div>
  )
}

function MigrationDetails({ migration, pending, error, onChange, onSubmit }: {
  migration: ServerMigration
  pending: boolean
  error: string | null
  onChange: () => void
  onSubmit: (action: MigrationAction, file: File, destination?: File) => void
}) {
  const [file, setFile] = useState<File>()
  const [destination, setDestination] = useState<File>()
  const [reverify, setReverify] = useState(false)
  const canRelease = migration.stage === 'Prepared' && !reverify

  return (
    <section className="min-w-0 rounded-xl border border-panel-border bg-panel-card p-5" aria-labelledby="migration-detail">
      <h2 id="migration-detail" className="break-words text-lg font-semibold">{migration.name}</h2>
      <dl className="mt-3 grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
        <div><dt className="text-gray-400">Destino</dt><dd className="break-words">{migration.nodeName}</dd></div>
        <div><dt className="text-gray-400">Estado</dt><dd>{stages[migration.stage].label}</dd></div>
        <div className="sm:col-span-2"><dt className="text-gray-400">UUID do servidor</dt><dd className="break-all font-mono text-xs">{migration.id}</dd></div>
        {migration.verifiedAt && <div><dt className="text-gray-400">Volume verificado</dt>
          <dd><time dateTime={migration.verifiedAt}>{dateFormat.format(new Date(migration.verifiedAt))}</time></dd></div>}
        {migration.releasedAt && <div><dt className="text-gray-400">Liberação</dt>
          <dd><time dateTime={migration.releasedAt}>{dateFormat.format(new Date(migration.releasedAt))}</time></dd></div>}
        {migration.volumeSha256 && <div className="sm:col-span-2"><dt className="text-gray-400">SHA-256 do volume</dt>
          <dd className="break-all font-mono text-xs">{migration.volumeSha256}</dd></div>}
      </dl>
      <div className="mt-5 border-t border-panel-border pt-5">
        {migration.stage === 'Released' ? (
          <Link className="button button-primary" to={`/servers/${migration.id}`}>Abrir servidor</Link>
        ) : (
          <form className="space-y-3" onSubmit={(event) => {
            event.preventDefault()
            if (file && (!canRelease || destination) && !pending && !demo)
              onSubmit(canRelease ? 'release-prepared' : 'prepare-restored', file, destination)
          }}>
            <h3 className="font-semibold">{canRelease ? 'Liberar destino' : 'Verificar volume e preparar container'}</h3>
            <p className="text-sm text-gray-400">
              {canRelease
                ? 'Com os dois lados protegidos contra gravações, gere uma nova confirmação da origem e verifique novamente o volume do destino. Envie os dois relatórios em até 15 minutos.'
                : 'Envie o relatório da origem e do destino gerado pelo comando verify-volume.'}
            </p>
            <label className="block text-sm font-medium" htmlFor="migration-receipt">
              {canRelease ? 'Confirmação da origem (source-fence.json)' : 'Relatório de verificação (verified-volume.json)'}
            </label>
            <input key={String(canRelease)} id="migration-receipt" type="file" accept=".json,application/json"
              className="block w-full max-w-full rounded-lg border border-panel-border bg-panel-dark p-2 text-sm file:mr-3 file:rounded file:border-0 file:bg-panel-card file:px-3 file:py-1 file:text-gray-200"
              disabled={demo || pending} onChange={(event) => { onChange(); setFile(event.target.files?.[0]) }} />
            {canRelease && <>
              <label className="block text-sm font-medium" htmlFor="migration-destination-receipt">
                Nova verificação do destino (final-volume.json)
              </label>
              <input id="migration-destination-receipt" type="file" accept=".json,application/json"
                className="block w-full max-w-full rounded-lg border border-panel-border bg-panel-dark p-2 text-sm file:mr-3 file:rounded file:border-0 file:bg-panel-card file:px-3 file:py-1 file:text-gray-200"
                disabled={demo || pending} onChange={(event) => { onChange(); setDestination(event.target.files?.[0]) }} />
            </>}
            {error && <p className="text-sm text-red-300" role="alert">{error}</p>}
            <div className="flex flex-wrap gap-2">
              <button className="button button-primary" disabled={demo || pending || !file || (canRelease && !destination)}>
                {pending ? 'Enviando…' : canRelease ? 'Liberar destino' : 'Preparar container'}
              </button>
              {migration.stage === 'Prepared' && <button type="button" className="button button-secondary" disabled={pending}
                onClick={() => { onChange(); setFile(undefined); setDestination(undefined); setReverify(!reverify) }}>
                {reverify ? 'Voltar à liberação' : 'Reenviar verificação'}
              </button>}
            </div>
          </form>
        )}
      </div>
    </section>
  )
}
