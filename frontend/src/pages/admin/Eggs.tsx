import { useRef, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import toast from 'react-hot-toast'
import { getErrorMessage } from '@/lib/errorHandler'
import { usePageTitle } from '@/lib/usePageTitle'
import { adminService } from '@/services/api'
import type { NestSummary } from '@/types'

const PAGE_SIZE = 20
const MAX_EGG_BYTES = 64 * 1024
const fieldClass = 'min-w-0 rounded-lg border border-panel-border bg-panel-dark px-3 py-2 text-sm text-white'
const isDemo = import.meta.env.DEV && import.meta.env.VITE_DEMO_MODE === 'true'

export default function Eggs() {
  usePageTitle('Eggs e categorias')
  const client = useQueryClient()
  const [nestPage, setNestPage] = useState(1)
  const [selectedNest, selectNest] = useState<NestSummary | null>(null)
  const [eggPage, setEggPage] = useState(1)
  const [file, setFile] = useState<File | null>(null)
  const fileInput = useRef<HTMLInputElement>(null)
  const [name, setName] = useState('')

  const nests = useQuery({
    queryKey: ['egg-nests', nestPage],
    queryFn: ({ signal }) => adminService.getEggNests(nestPage, PAGE_SIZE, signal),
  })
  const nest = selectedNest ?? nests.data?.items[0]
  const nestId = nest?.id ?? ''
  const eggs = useQuery({
    queryKey: ['imported-eggs', nestId, eggPage],
    queryFn: ({ signal }) => adminService.getImportedEggs(nestId, eggPage, PAGE_SIZE, signal),
    enabled: !!nestId,
  })

  const create = useMutation({
    mutationFn: adminService.createNest,
    onSuccess: (created) => {
      selectNest(created)
      setEggPage(1)
      setName('')
      toast.success('Categoria criada')
      return client.invalidateQueries({ queryKey: ['egg-nests'] })
    },
    onError: (error) => toast.error(getErrorMessage(error, 'Falha ao criar categoria')),
  })
  const upload = useMutation({
    mutationFn: ({ id, manifest }: { id: string; manifest: File }) => adminService.importEgg(id, manifest),
    onSuccess: (added, { id }) => {
      setFile(null)
      if (fileInput.current) fileInput.current.value = ''
      toast.success(added ? 'Egg importada' : 'Este manifesto já está na categoria')
      return Promise.all([
        client.invalidateQueries({ queryKey: ['egg-nests'] }),
        client.invalidateQueries({ queryKey: ['imported-eggs', id] }),
      ])
    },
    onError: (error) => toast.error(getErrorMessage(error, 'Falha ao importar egg')),
  })
  const download = useMutation({
    mutationFn: adminService.exportEgg,
    onSuccess: (blob, id) => {
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = `egg-${id}.json`
      link.click()
      window.setTimeout(() => URL.revokeObjectURL(url), 1000)
    },
    onError: (error) => toast.error(getErrorMessage(error, 'Falha ao baixar egg')),
  })

  function importEgg(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!nestId || !file) return
    if (file.size > MAX_EGG_BYTES) {
      toast.error('Selecione um arquivo JSON de até 64 KB.')
      return
    }
    upload.mutate({ id: nestId, manifest: file })
  }

  const outsidePage = nest && nests.data && !nests.data.items.some((item) => item.id === nest.id)
  return (
    <div>
      <header className="page-heading">
        <div>
          <h1>Eggs e categorias</h1>
          <p>Importe e exporte manifestos Pterodactyl PTDL v1 e v2. As eggs importadas ficam disponíveis para revisão.</p>
        </div>
      </header>

      <div className="grid gap-5 lg:grid-cols-[minmax(260px,1fr)_2fr]">
        <section className="min-w-0 rounded-xl border border-panel-border bg-panel-card p-5">
          <h2 className="mb-4 font-semibold">Categorias</h2>
          <form className="mb-5 flex flex-wrap gap-2" onSubmit={(event) => {
            event.preventDefault()
            if (name.trim()) create.mutate(name.trim())
          }}>
            <input className={`${fieldClass} flex-1`} aria-label="Nome da categoria" placeholder="Nova categoria" maxLength={100}
              value={name} onChange={(event) => setName(event.target.value)} disabled={isDemo || create.isPending} required />
            <button className="button button-secondary" disabled={isDemo || create.isPending}>Criar</button>
          </form>
          {nests.isPending && <p role="status">Carregando categorias…</p>}
          {nests.isError && <p role="alert">Falha ao carregar categorias.</p>}
          {nests.isSuccess && (
            <>
              <label htmlFor="egg-nest" className="mb-2 block text-sm">Categoria ativa</label>
              <select id="egg-nest" className={`${fieldClass} w-full`} value={nestId} onChange={(event) => {
                selectNest(nests.data.items.find((item) => item.id === event.target.value) ?? null)
                setEggPage(1)
              }}>
                {!nest && <option value="">Nenhuma categoria</option>}
                {outsidePage && <option value={nest.id}>{nest.name}</option>}
                {nests.data.items.map((item) => <option key={item.id} value={item.id}>{item.name} ({item.importedEggCount})</option>)}
              </select>
              <PageControls label="Páginas de categorias" page={nestPage} total={nests.data.total} onChange={(page) => {
                setNestPage(page)
                if (!selectedNest) setEggPage(1)
              }} />
            </>
          )}
        </section>

        <section className="min-w-0 rounded-xl border border-panel-border bg-panel-card p-5" aria-labelledby="imported-eggs-heading">
          <h2 id="imported-eggs-heading" className="mb-4 break-words font-semibold">{nest?.name ?? 'Eggs importadas'}</h2>
          <form className="mb-5 flex flex-wrap items-end gap-3" onSubmit={importEgg}>
            <div className="min-w-0 flex-1 basis-56">
              <label htmlFor="egg-file" className="mb-2 block text-sm">Arquivo JSON do Pterodactyl</label>
              <input id="egg-file" ref={fileInput} className={`${fieldClass} w-full`} type="file" accept=".json,application/json"
                onChange={(event) => setFile(event.target.files?.[0] ?? null)} disabled={isDemo || !nestId || upload.isPending} />
            </div>
            <button className="button button-primary" disabled={isDemo || !file || upload.isPending}>
              {upload.isPending ? 'Importando…' : 'Importar egg'}
            </button>
          </form>
          {nest && eggs.isPending && <p role="status">Carregando eggs…</p>}
          {eggs.isError && <p role="alert">Falha ao carregar as eggs da categoria.</p>}
          {eggs.isSuccess && (
            <>
              {eggs.data.items.length === 0 && <p className="text-sm text-slate-400">Nenhuma egg importada nesta categoria.</p>}
              <ul className="divide-y divide-panel-border">
                {eggs.data.items.map((egg) => (
                  <li key={egg.id} className="flex flex-wrap items-start gap-x-4 gap-y-2 py-3">
                    <div className="min-w-0 flex-1 basis-40 break-words">
                      <h3 className="font-medium">{egg.name}</h3>
                      {egg.description && <p className="mt-1 text-sm text-slate-400">{egg.description}</p>}
                      <p className="mt-1 break-all text-xs text-slate-400">SHA-256 {egg.sha256}</p>
                      <p className="mt-1 break-all text-xs text-slate-400">UUID {egg.id}</p>
                    </div>
                    <button className="button button-secondary" disabled={isDemo || download.isPending} onClick={() => download.mutate(egg.id)}
                      aria-label={`Baixar JSON de ${egg.name}`}>Baixar JSON</button>
                    {egg.cartridgeId && <Link className="button button-primary" to={`/admin/deploy?egg=${egg.id}`}>Criar servidor</Link>}
                  </li>
                ))}
              </ul>
              <PageControls label="Páginas de eggs" page={eggPage} total={eggs.data.total} onChange={setEggPage} />
            </>
          )}
        </section>
      </div>
    </div>
  )
}

function PageControls({ label, page, total, onChange }: { label: string; page: number; total: number; onChange: (page: number) => void }) {
  const pages = Math.max(1, Math.ceil(total / PAGE_SIZE))
  if (pages === 1) return null
  return (
    <nav aria-label={label} className="mt-4 flex flex-wrap items-center gap-2 text-sm">
      <button className="button button-secondary" disabled={page <= 1} onClick={() => onChange(page - 1)}>Anterior</button>
      <span>{page} de {pages}</span>
      <button className="button button-secondary" disabled={page >= pages} onClick={() => onChange(page + 1)}>Próxima</button>
    </nav>
  )
}
