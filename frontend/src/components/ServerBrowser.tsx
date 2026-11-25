import { useState } from 'react'
import {
  ArrowPathIcon,
  MagnifyingGlassIcon,
  Squares2X2Icon,
  ListBulletIcon,
  ServerStackIcon,
} from '@heroicons/react/24/outline'
import type { Server } from '@/types'
import ServerCard from '@/components/ServerCard'

type Filter = 'all' | 'online' | 'offline'
export default function ServerBrowser({
  servers,
  loading,
  error,
  refreshing,
  onRefresh,
}: {
  servers: Server[]
  loading: boolean
  error: boolean
  refreshing: boolean
  onRefresh: () => void
}) {
  const [search, setSearch] = useState('')
  const [filter, setFilter] = useState<Filter>('all')
  const [view, setView] = useState<'grid' | 'list'>('grid')
  const filtered = servers.filter((server) => {
    const matches =
      `${server.name} ${server.description} ${server.dockerImage} ${server.nodeName} ${server.ipAddress}:${server.port}`
        .toLowerCase()
        .includes(search.toLowerCase())
    return (
      matches &&
      (filter === 'all' ||
        (filter === 'online'
          ? server.status === 'Running'
          : server.status === 'Stopped'))
    )
  })
  return (
    <section aria-label="Seus servidores">
      <div className="section-heading">
        <div className="section-title">
          <ServerStackIcon />
          <h2>Seus servidores</h2>
          <span className="count-badge">{servers.length}</span>
        </div>
        <button
          className="text-button"
          onClick={onRefresh}
          disabled={refreshing}
        >
          <ArrowPathIcon className={refreshing ? 'animate-spin' : ''} />
          Atualizar
        </button>
      </div>
      <div className="browser-toolbar">
        <div className="filter-tabs" aria-label="Filtrar por status">
          {(
            [
              { id: 'all', label: 'Todos' },
              { id: 'online', label: 'Online' },
              { id: 'offline', label: 'Offline' },
            ] as const
          ).map((item) => (
            <button
              key={item.id}
              aria-pressed={filter === item.id}
              onClick={() => setFilter(item.id)}
              className={filter === item.id ? 'selected' : ''}
            >
              {item.id !== 'all' && (
                <span className={`filter-dot ${item.id}`} />
              )}
              {item.label}
              <span>
                {item.id === 'all'
                  ? servers.length
                  : servers.filter(
                      (server) =>
                        server.status ===
                        (item.id === 'online' ? 'Running' : 'Stopped')
                    ).length}
              </span>
            </button>
          ))}
        </div>
        <label className="search-field">
          <MagnifyingGlassIcon />
          <input
            type="search"
            aria-label="Buscar servidores"
            placeholder="Buscar por nome, jogo ou endereço..."
            value={search}
            onChange={(event) => setSearch(event.target.value)}
          />
        </label>
        <div className="view-switch">
          <button
            aria-label="Visualizar em grade"
            aria-pressed={view === 'grid'}
            className={view === 'grid' ? 'selected' : ''}
            onClick={() => setView('grid')}
          >
            <Squares2X2Icon />
          </button>
          <button
            aria-label="Visualizar em lista"
            aria-pressed={view === 'list'}
            className={view === 'list' ? 'selected' : ''}
            onClick={() => setView('list')}
          >
            <ListBulletIcon />
          </button>
        </div>
      </div>
      {error ? (
        <div className="empty-state error-state">
          <ServerStackIcon />
          <h3>Não foi possível carregar os servidores</h3>
          <p>Verifique sua conexão e tente atualizar a lista.</p>
          <button className="button button-secondary" onClick={onRefresh}>
            Tentar novamente
          </button>
        </div>
      ) : loading ? (
        <div className="server-grid" aria-label="Carregando servidores">
          {[0, 1, 2].map((item) => (
            <div key={item} className="server-skeleton" />
          ))}
        </div>
      ) : filtered.length ? (
        <div className={view === 'grid' ? 'server-grid' : 'server-list'}>
          {filtered.map((server) => (
            <ServerCard
              key={server.id}
              server={server}
              list={view === 'list'}
            />
          ))}
        </div>
      ) : (
        <div className="empty-state">
          <ServerStackIcon />
          <h3>
            {servers.length
              ? 'Nenhum servidor encontrado'
              : 'Seu primeiro servidor começa aqui'}
          </h3>
          <p>
            {servers.length
              ? 'Tente outro termo ou selecione um status diferente.'
              : 'Crie um servidor ou peça acesso ao administrador do painel.'}
          </p>
          {!!servers.length && (
            <button
              className="text-button"
              onClick={() => {
                setSearch('')
                setFilter('all')
              }}
            >
              Limpar filtros
            </button>
          )}
        </div>
      )}
      {!loading && !error && (
        <div className="results-caption">
          Exibindo {filtered.length} de {servers.length} servidores
          <span>Recursos exibidos correspondem aos limites alocados.</span>
        </div>
      )}
    </section>
  )
}
