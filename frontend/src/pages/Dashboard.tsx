import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import {
  PlusIcon,
  ArrowUpRightIcon,
  ServerStackIcon,
  SignalIcon,
  CpuChipIcon,
  CubeIcon,
  ChevronRightIcon,
} from '@heroicons/react/24/outline'
import { serverService, nodeService } from '@/services/api'
import { useAuthStore } from '@/stores/authStore'
import { usePageTitle } from '@/lib/usePageTitle'
import ServerBrowser from '@/components/ServerBrowser'

export default function Dashboard() {
  usePageTitle('Visão geral')
  const { user } = useAuthStore()
  const isAdmin = user?.role === 'Admin' || user?.role === 'SuperAdmin'
  const serversQuery = useQuery({
    queryKey: ['servers'],
    queryFn: serverService.getAll,
    refetchInterval: 15000,
  })
  const nodesQuery = useQuery({
    queryKey: ['nodes'],
    queryFn: nodeService.getAll,
    enabled: isAdmin,
  })
  const servers = serversQuery.data ?? []
  const nodes = nodesQuery.data ?? []
  const loading = serversQuery.isLoading
  const unavailable = serversQuery.isError
  const metrics = [
    {
      label: 'Servidores',
      value: servers.length,
      icon: ServerStackIcon,
      hint: 'No seu workspace',
      tone: 'purple',
    },
    {
      label: 'Em execução',
      value: servers.filter((server) => server.status === 'Running').length,
      icon: SignalIcon,
      hint: `${servers.filter((server) => server.status === 'Stopped').length} offline`,
      tone: 'green',
    },
    {
      label: 'Memória alocada',
      value: `${(servers.reduce((sum, server) => sum + server.memoryLimit, 0) / 1024).toLocaleString('pt-BR', { maximumFractionDigits: 1 })} GB`,
      icon: CpuChipIcon,
      hint: 'Distribuída entre servidores',
      tone: 'blue',
    },
    ...(isAdmin
      ? [
          {
            label: 'Nós de hospedagem',
            value: nodes.length,
            icon: CubeIcon,
            hint: `${nodes.filter((node) => node.isOnline).length} disponíveis`,
            tone: 'blue',
          },
        ]
      : []),
  ]
  return (
    <div className="overview-page">
      <div className="page-heading">
        <div>
          <div className="page-eyebrow">Seu centro de operações</div>
          <h1>Visão geral</h1>
          <p>
            Tudo pronto para o próximo mundo. Seus servidores, em um só lugar.
          </p>
        </div>
        {isAdmin && (
          <Link className="button button-primary" to="/admin/deploy">
            <PlusIcon />
            Criar servidor
          </Link>
        )}
      </div>
      <div
        className="metrics-strip"
        style={{
          gridTemplateColumns: `repeat(${metrics.length}, minmax(0, 1fr))`,
        }}
      >
        {metrics.map(({ label, value, icon: Icon, hint, tone }) => (
          <div className="metric" key={label}>
            <div className="metric-label">
              {label}
              <Icon className={`metric-icon ${tone}`} />
            </div>
            <div className="metric-value">
              {loading ||
              unavailable ||
              (label === 'Nós de hospedagem' &&
                (nodesQuery.isLoading || nodesQuery.isError))
                ? '—'
                : value}
            </div>
            <p>{hint}</p>
          </div>
        ))}
      </div>
      <ServerBrowser
        servers={servers}
        loading={loading}
        error={unavailable}
        refreshing={serversQuery.isFetching}
        onRefresh={() => {
          void serversQuery.refetch()
        }}
      />
      {isAdmin && (
        <section className="infrastructure-section">
          <div className="section-heading">
            <div className="section-title">
              <CubeIcon />
              <h2>Infraestrutura</h2>
            </div>
            <Link className="text-button" to="/admin/nodes">
              Gerenciar nós
              <ArrowUpRightIcon />
            </Link>
          </div>
          {nodesQuery.isError ? (
            <div className="infrastructure-empty">
              Não foi possível consultar os nós.{' '}
              <button
                className="text-button"
                onClick={() => {
                  void nodesQuery.refetch()
                }}
              >
                Tentar novamente
              </button>
            </div>
          ) : nodes.length ? (
            <div className="node-summary-grid">
              {nodes.map((node) => (
                <Link to="/admin/nodes" className="node-summary" key={node.id}>
                  <div className="node-summary-icon">
                    <CubeIcon />
                  </div>
                  <div className="node-summary-name">
                    <strong>{node.name}</strong>
                    <span>{node.fqdn}</span>
                  </div>
                  <span
                    className={`node-availability ${node.isOnline ? 'available' : ''}`}
                  >
                    <i />
                    {node.isOnline ? 'Disponível' : 'Indisponível'}
                  </span>
                  <div className="node-memory">
                    <span>
                      Memória alocada{' '}
                      <strong>
                        {(node.allocatedMemory / 1024).toLocaleString('pt-BR', {
                          maximumFractionDigits: 1,
                        })}{' '}
                        /{' '}
                        {node.totalMemory
                          ? `${(node.totalMemory / 1024).toLocaleString('pt-BR')} GB`
                          : 'Sem limite'}
                      </strong>
                    </span>
                    <div className="resource-track">
                      <i
                        style={{
                          width: `${node.totalMemory ? Math.min(100, (node.allocatedMemory / node.totalMemory) * 100) : 0}%`,
                        }}
                      />
                    </div>
                  </div>
                  <ChevronRightIcon className="node-chevron" />
                </Link>
              ))}
            </div>
          ) : (
            <div className="infrastructure-empty">
              {nodesQuery.isLoading
                ? 'Consultando nós de hospedagem...'
                : 'Nenhum nó cadastrado. Adicione um nó para hospedar seus servidores.'}
            </div>
          )}
        </section>
      )}
    </div>
  )
}
