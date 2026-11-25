import { lazy, Suspense, useState, useEffect, useRef } from 'react'
import { Link, useParams, useNavigate } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  ArrowLeftIcon,
  PlayIcon,
  StopIcon,
  ArrowPathIcon,
  CommandLineIcon,
  ChartBarIcon,
  Cog6ToothIcon,
  FolderIcon,
  ArchiveBoxIcon,
  TrashIcon,
  GlobeAltIcon,
  ClockIcon,
  CpuChipIcon,
  CircleStackIcon,
  CubeIcon,
  PaperAirplaneIcon,
  ArrowDownTrayIcon,
  ArrowUpTrayIcon,
  ClipboardDocumentIcon,
  ChatBubbleLeftRightIcon,
  UserGroupIcon,
} from '@heroicons/react/24/outline'
import toast from 'react-hot-toast'
import { AxiosError } from 'axios'
import { serverService } from '@/services/api'
import { ServerSchedules } from '@/components/ServerSchedules'
import { ServerAssistant } from '@/components/ServerAssistant'
import { useAuthStore } from '@/stores/authStore'
import { getErrorMessage } from '@/lib/errorHandler'
import { useLiveConsole } from '@/lib/useLiveConsole'
import { hasServerPermission } from '@/lib/serverPermissions'
import { usePageTitle } from '@/lib/usePageTitle'
import { gameVisual } from '@/lib/gameVisuals'
import { StatusBadge } from '@/components/ui/StatusBadge'
import { ConfirmDialog } from '@/components/ui/ConfirmDialog'
import type { Server } from '@/types'

const ServerBackups = lazy(() => import('@/components/ServerBackups'))
const ServerFiles = lazy(() => import('@/components/ServerFiles'))
const ServerMembers = lazy(() => import('@/components/ServerMembers'))

type Tab = 'console' | 'stats' | 'files' | 'backups' | 'schedules' | 'assistant' | 'settings' | 'members'
function serverAccessEnded(error: unknown) {
  const status = error instanceof AxiosError ? error.response?.status : undefined
  return status === 401 || status === 403 || status === 404
}

function formatBytes(bytes: number) {
  if (bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const index = Math.min(
    Math.floor(Math.log(bytes) / Math.log(1024)),
    units.length - 1
  )
  return `${(bytes / 1024 ** index).toLocaleString('pt-BR', { maximumFractionDigits: 1 })} ${units[index]}`
}

function ForceStopControl({ serverName, pending, onConfirm }: {
  serverName: string; pending: boolean; onConfirm: () => void
}) {
  const [open, setOpen] = useState(false)
  return (
    <>
      <button className="button button-secondary" disabled={pending} onClick={() => setOpen(true)}>
        Forçar parada
      </button>
      <ConfirmDialog
        open={open}
        pending={pending}
        title="Forçar a parada do servidor?"
        description={`O processo de ${serverName} será encerrado imediatamente. Alterações ainda não salvas podem ser perdidas.`}
        confirmLabel="Forçar parada"
        pendingLabel="Encerrando…"
        onCancel={() => setOpen(false)}
        onConfirm={() => { setOpen(false); onConfirm() }}
      />
    </>
  )
}

function ServerSettings({ server }: { server: Server }) {
  const [name, setName] = useState(server.name)
  const [description, setDescription] = useState(server.description)
  const client = useQueryClient()
  const mutation = useMutation({
    mutationFn: () =>
      serverService.update(server.id, { name: name.trim(), description }),
    onSuccess: () => {
      toast.success('Configurações salvas')
      void client.invalidateQueries({ queryKey: ['server', server.id] })
      void client.invalidateQueries({ queryKey: ['servers'] })
    },
    onError: (error) =>
      toast.error(
        getErrorMessage(error, 'Não foi possível salvar as configurações')
      ),
  })
  return (
    <div className="surface-panel server-settings">
      <div className="panel-heading">
        <Cog6ToothIcon />
        <h2>Configurações do servidor</h2>
      </div>
      <p className="panel-description">
        Personalize a identificação do seu servidor no painel.
      </p>
      <form
        onSubmit={(event) => {
          event.preventDefault()
          mutation.mutate()
        }}
      >
        <label className="field-label" htmlFor="server-name">
          Nome do servidor
        </label>
        <input
          id="server-name"
          className="form-input"
          required
          maxLength={100}
          value={name}
          onChange={(event) => setName(event.target.value)}
        />
        <label className="field-label" htmlFor="server-description">
          Descrição
        </label>
        <textarea
          id="server-description"
          className="form-input"
          rows={4}
          value={description}
          onChange={(event) => setDescription(event.target.value)}
        />
        <div className="form-footer">
          <span>
            Alterações nos limites são gerenciadas pela administração.
          </span>
          <button
            className="button button-primary"
            disabled={
              mutation.isPending ||
              !name.trim() ||
              (name === server.name && description === server.description)
            }
          >
            {mutation.isPending ? 'Salvando...' : 'Salvar alterações'}
          </button>
        </div>
      </form>
    </div>
  )
}

export default function ServerDetail() {
  const { id = '' } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const client = useQueryClient()
  const user = useAuthStore((state) => state.user)
  const isAdmin = user?.role === 'Admin' || user?.role === 'SuperAdmin'
  const [requestedTab, setTab] = useState<Tab>('console')
  const [openedFileServer, setOpenedFileServer] = useState<string | null>(null)
  const [command, setCommand] = useState('')
  const [deleteOpen, setDeleteOpen] = useState(false)
  const outputRef = useRef<HTMLDivElement>(null)
  const serverQuery = useQuery({
    queryKey: ['server', id],
    queryFn: () => serverService.getById(id),
    enabled: !!id,
    refetchInterval: (query) => serverAccessEnded(query.state.error) ? false : 5000,
    retry: (attempt, error) => !serverAccessEnded(error) && attempt < 2,
  })
  const server = serverAccessEnded(serverQuery.error) ? undefined : serverQuery.data
  const permissions = server?.sharedPermissions
  const can = (permission: string) => !!server && hasServerPermission(permissions, permission)
  const accessibleTabs: Record<Tab, boolean> = {
    console: can('websocket.connect') || can('control.console'),
    stats: true,
    files: can('file.read') || can('file.read-content') || can('file.create') || can('file.update') || can('file.archive') || can('file.delete'),
    backups: can('backup.read') || can('backup.create'),
    schedules: can('schedule.read') || can('schedule.create'),
    assistant: can('integration.ai'),
    settings: can('settings.rename'),
    members: can('user.read') || can('user.create') || can('user.update') || can('user.delete'),
  }
  const tab = accessibleTabs[requestedTab] ? requestedTab : 'stats'
  usePageTitle(server?.name || 'Servidor')
  const statsQuery = useQuery({
    queryKey: ['server-stats', id, server?.status],
    queryFn: () => serverService.getStats(id),
    enabled: !!id && !!server?.containerId && !server.isSuspended,
    refetchInterval: (query) => serverAccessEnded(query.state.error) ? false : server?.status === 'Running' ? 5000 : 15000,
    retry: (attempt, error) => !serverAccessEnded(error) && attempt < 2,
  })
  const storageQuery = useQuery({
    queryKey: ['server-storage', id],
    queryFn: ({ signal }) => serverService.getStorageUsage(id, signal),
    enabled: !!server && !server.isSuspended && tab === 'stats',
    refetchInterval: 30000,
    retry: false,
  })
  const liveEnabled = can('websocket.connect') && !!server?.containerId && !server.isSuspended && server.status === 'Running' &&
    tab === 'console' && !(import.meta.env.DEV && import.meta.env.VITE_DEMO_MODE === 'true')
  const consoleStream = useLiveConsole(id, liveEnabled)
  const logsQuery = useQuery({
    queryKey: ['server-logs', id],
    queryFn: () => serverService.getLogs(id, 200),
    enabled: can('websocket.connect') && !!server?.containerId && tab === 'console' && (!liveEnabled || consoleStream.failed),
    refetchInterval: 5000,
  })
  const runtime = server && !server.isSuspended && !statsQuery.isError ? statsQuery.data : undefined
  const stats = runtime?.status === 'Running' && runtime.metricsAvailable !== false ? runtime : undefined
  const consoleText = consoleStream.text ?? logsQuery.data?.logs ?? ''
  const logLines = consoleText.split('\n').filter((line) => line.trim()).slice(-200)
  useEffect(() => {
    if (outputRef.current)
      outputRef.current.scrollTop = outputRef.current.scrollHeight
  }, [consoleText])
  useEffect(() => {
    setTab('console')
    setCommand('')
  }, [id])

  const power = useMutation({
    mutationFn: (action: Parameters<typeof serverService.power>[1]) =>
      serverService.power(id, action),
    onSuccess: (result) => {
      if (result.success === false) {
        toast.error('O servidor não confirmou a operação. Verifique os logs.')
        return
      }
      toast.success('Operação enviada ao servidor')
      void client.invalidateQueries({ queryKey: ['server', id] })
      void client.invalidateQueries({ queryKey: ['server-stats', id] })
      void client.invalidateQueries({ queryKey: ['servers'] })
    },
    onError: (error) =>
      toast.error(
        getErrorMessage(error, 'Não foi possível executar a operação')
      ),
  })
  const sendCommand = useMutation({
    mutationFn: () => serverService.sendCommand(id, command.trim()),
    onSuccess: (result) => {
      if (result.success === false) {
        toast.error('Não foi possível enviar o comando')
        return
      }
      setCommand('')
      if (!liveEnabled || consoleStream.failed) void logsQuery.refetch()
    },
    onError: (error) =>
      toast.error(getErrorMessage(error, 'Não foi possível enviar o comando')),
  })
  const remove = useMutation({
    mutationFn: () => serverService.delete(id),
    onSuccess: () => {
      toast.success('Servidor excluído')
      void client.invalidateQueries({ queryKey: ['servers'] })
      navigate('/servers')
    },
    onError: (error) =>
      toast.error(
        getErrorMessage(error, 'Não foi possível excluir o servidor')
      ),
  })

  if (serverQuery.isLoading)
    return (
      <div className="empty-state">
        <ArrowPathIcon className="animate-spin" />
        <h3>Carregando servidor...</h3>
      </div>
    )
  if (serverQuery.isError || !server)
    return (
      <div className="empty-state">
        <CubeIcon />
        <h3>Não foi possível abrir o servidor</h3>
        <p>Confira se o servidor existe e se você tem acesso a ele.</p>
        <Link className="button button-secondary" to="/servers">
          Voltar aos servidores
        </Link>
      </div>
    )

  const game = gameVisual(server)
  const containerActive = server.status === 'Running' || server.status === 'Paused'
  const canForceStop = can('control.stop') && !server.isSuspended &&
    (containerActive || server.status === 'Stopping')
  const address = `${server.ipAddress}:${server.port}`
  const memoryPercent =
    stats && server.memoryLimit > 0
      ? Math.min(
          100,
          (stats.memoryUsage / (server.memoryLimit * 1024 ** 2)) * 100
        )
      : 0
  const tabs = [
    { id: 'console', name: 'Console', icon: CommandLineIcon },
    { id: 'stats', name: 'Estatísticas', icon: ChartBarIcon },
    { id: 'files', name: 'Arquivos', icon: FolderIcon },
    { id: 'backups', name: 'Backups', icon: ArchiveBoxIcon },
    { id: 'schedules', name: 'Agendamentos', icon: ClockIcon },
    { id: 'assistant', name: 'Assistente', icon: ChatBubbleLeftRightIcon },
    { id: 'settings', name: 'Configurações', icon: Cog6ToothIcon },
    { id: 'members', name: 'Membros', icon: UserGroupIcon },
  ] as const
  const startedAt = stats?.startedAt ?? server.lastStartedAt
  const uptimeMinutes =
    startedAt && server.status === 'Running'
      ? Math.max(
          0,
          Math.floor(
            (Date.now() - new Date(startedAt).getTime()) / 60000
          )
        )
      : 0
  return (
    <div className="server-detail-page">
      <Link to="/servers" className="text-button back-link">
        <ArrowLeftIcon />
        Todos os servidores
      </Link>
      <div className="server-detail-heading">
        <div className="server-detail-title">
          <div className={`detail-game-icon game-${game.key}`}>
            {game.icon ? <img src={game.icon} alt="" /> : <CubeIcon />}
          </div>
          <div>
            <div className="server-title-line">
              <h1>{server.name}</h1>
              <StatusBadge status={server.status} />
            </div>
            <p>
              {game.label}
              <span>/</span>
              {server.nodeName}
              <span>/</span>
              {server.identifier}
            </p>
            {runtime?.exitCode != null && runtime.exitCode !== 0 && !power.isPending && (
              <p>{runtime.oomKilled ? 'O processo encerrou por falta de memória.' : `O processo encerrou com código ${runtime.exitCode}.`}</p>
            )}
          </div>
        </div>
        <div className="power-controls">
          {can('control.start') && <button
            className="button button-secondary"
            disabled={
              server.status !== 'Stopped' ||
              server.isSuspended ||
              power.isPending
            }
            onClick={() => power.mutate('start')}
          >
            <PlayIcon />
            Iniciar
          </button>}
          {can('control.restart') && <button
            className="button button-secondary"
            disabled={
              !containerActive ||
              server.isSuspended ||
              power.isPending
            }
            onClick={() => power.mutate('restart')}
          >
            <ArrowPathIcon />
            Reiniciar
          </button>}
          {can('control.stop') && <button
            className="button button-danger"
            disabled={!containerActive || server.isSuspended || power.isPending}
            onClick={() => power.mutate('stop')}
          >
            <StopIcon />
            Parar
          </button>}
          {canForceStop && <ForceStopControl key={server.id} serverName={server.name} pending={power.isPending}
            onConfirm={() => power.mutate('kill')} />}
        </div>
      </div>
      <div className="server-workspace">
        <aside className="server-sidebar">
          <div className="server-sidebar-label">Gerenciar servidor</div>
          <nav aria-label="Navegação do servidor">
            {tabs.filter(({ id: tabId }) => accessibleTabs[tabId]).map(({ id: tabId, name, icon: Icon }) => (
              <button
                key={tabId}
                aria-current={tab === tabId ? 'page' : undefined}
                className={tab === tabId ? 'active' : ''}
                onClick={() => {
                  setTab(tabId)
                  if (tabId === 'files') setOpenedFileServer(server.id)
                }}
              >
                <Icon />
                {name}
              </button>
            ))}
          </nav>
          <div className="server-sidebar-info">
            <CubeIcon />
            <span>
              Hospedado em<strong>{server.nodeName}</strong>
            </span>
          </div>
          {isAdmin && (
            <button
              className="server-delete"
              disabled={server.status !== 'Stopped'}
              title={
                server.status !== 'Stopped'
                  ? 'Pare o servidor antes de excluí-lo'
                  : 'Excluir servidor'
              }
              onClick={() => setDeleteOpen(true)}
            >
              <TrashIcon />
              Excluir servidor
            </button>
          )}
        </aside>
        <div className="server-workspace-content">
          {tab === 'console' && (
            <>
              <div className="server-resource-grid">
                <div className="resource-mini">
                  <span>
                    <CpuChipIcon />
                    Memória
                  </span>
                  <strong>
                    {stats ? formatBytes(stats.memoryUsage) : '—'}
                    <small>
                      / {(server.memoryLimit / 1024).toLocaleString('pt-BR')} GB
                    </small>
                  </strong>
                  <div className="resource-track">
                    <i style={{ width: `${memoryPercent}%` }} />
                  </div>
                </div>
                <div className="resource-mini">
                  <span>
                    <ChartBarIcon />
                    Processamento
                  </span>
                  <strong>
                    {stats ? `${stats.cpuUsage.toLocaleString('pt-BR')}%` : '—'}
                    <small>/ {server.cpuLimit}%</small>
                  </strong>
                  <div className="resource-track">
                    <i
                      style={{
                        width: `${stats ? Math.min(100, (stats.cpuUsage / Math.max(1, server.cpuLimit)) * 100) : 0}%`,
                      }}
                    />
                  </div>
                </div>
                <div className="resource-mini">
                  <span>
                    <ClockIcon />
                    Tempo online
                  </span>
                  <strong>
                    {server.status === 'Running'
                      ? `${Math.floor(uptimeMinutes / 60)}h ${uptimeMinutes % 60}m`
                      : '—'}
                  </strong>
                  <small>
                    {server.status === 'Running'
                      ? 'Desde a última inicialização'
                      : 'Servidor desligado'}
                  </small>
                </div>
              </div>
              <section
                className="console-panel"
                aria-label="Console do servidor"
              >
                <div className="console-toolbar">
                  <div>
                    <CommandLineIcon />
                    <h2>Console</h2>
                    <span>Saída do servidor</span>
                  </div>
                  {can('websocket.connect') && <button
                    className="text-button"
                    onClick={() => {
                      if (liveEnabled) consoleStream.reconnect()
                      else void logsQuery.refetch()
                    }}
                    disabled={!server.containerId || logsQuery.isFetching}
                    aria-label="Atualizar console"
                  >
                    <ArrowPathIcon
                      className={logsQuery.isFetching ? 'animate-spin' : ''}
                    />
                  </button>}
                </div>
                {can('websocket.connect') && <div
                  ref={outputRef}
                  className="console-output"
                  role="log"
                  aria-label="Logs do servidor"
                >
                  {(consoleStream.failed || !liveEnabled) && logsQuery.isError ? (
                    <p className="console-error">
                      Não foi possível carregar os logs. Tente atualizar o
                      console.
                    </p>
                  ) : logLines.length ? (
                    logLines.map((line, index) => (
                      <div
                        className={`console-line ${/error|exception|failed/i.test(line) ? 'console-error' : /warn/i.test(line) ? 'console-warning' : ''}`}
                        key={index}
                      >
                        <span>{String(index + 1).padStart(2, '0')}</span>
                        <code>{line}</code>
                      </div>
                    ))
                  ) : (
                    <p className="console-placeholder">
                      {!server.containerId
                        ? 'O contêiner ainda não foi criado.'
                        : logsQuery.isLoading
                          ? 'Conectando à saída do servidor...'
                          : 'Aguardando saída do servidor...'}
                    </p>
                  )}
                </div>}
                {can('control.console') && <form
                  className="console-command"
                  onSubmit={(event) => {
                    event.preventDefault()
                    if (command.trim()) sendCommand.mutate()
                  }}
                >
                  <span>❯</span>
                  <input
                    aria-label="Comando do servidor"
                    value={command}
                    onChange={(event) => setCommand(event.target.value)}
                    placeholder={
                      server.status === 'Running'
                        ? 'Digite um comando para o servidor...'
                        : 'Inicie o servidor para enviar comandos'
                    }
                    disabled={
                      server.status !== 'Running' ||
                      server.isSuspended ||
                      sendCommand.isPending
                    }
                  />
                  <button
                    aria-label="Enviar comando"
                    type="submit"
                    disabled={
                      !command.trim() ||
                      server.status !== 'Running' ||
                      server.isSuspended ||
                      sendCommand.isPending
                    }
                  >
                    <PaperAirplaneIcon />
                  </button>
                </form>}
              </section>
              <div className="connection-panel">
                <GlobeAltIcon />
                <div>
                  <span>Endereço de conexão</span>
                  <strong>{address}</strong>
                </div>
                <button
                  className="text-button"
                  onClick={() => {
                    void navigator.clipboard
                      .writeText(address)
                      .then(() => toast.success('Endereço copiado'))
                      .catch(() =>
                        toast.error('Não foi possível copiar o endereço')
                      )
                  }}
                >
                  <ClipboardDocumentIcon />
                  Copiar endereço
                </button>
              </div>
            </>
          )}
          {tab === 'stats' && (
            <section className="surface-panel">
              <div className="panel-heading">
                <ChartBarIcon />
                <h2>Estatísticas de recursos</h2>
              </div>
              <p className="panel-description">
                {statsQuery.isError
                  ? 'Não foi possível consultar as métricas. Tente novamente.'
                  : (runtime?.status ?? server.status) === 'Running'
                    ? 'Leitura atual do servidor. Atualização a cada 5 segundos.'
                    : 'As métricas ficam disponíveis enquanto o servidor está online.'}
              </p>
              <div className="stats-detail-grid">
                {[
                  {
                    icon: CpuChipIcon,
                    label: 'Memória em uso',
                    value: stats ? formatBytes(stats.memoryUsage) : '—',
                  },
                  {
                    icon: ChartBarIcon,
                    label: 'Uso de CPU',
                    value: stats
                      ? `${stats.cpuUsage.toLocaleString('pt-BR')}%`
                      : '—',
                  },
                  {
                    icon: CircleStackIcon,
                    label: 'Leitura e escrita em disco',
                    value: stats ? formatBytes(stats.blockIoBytes) : '—',
                  },
                  {
                    icon: CircleStackIcon,
                    label: 'Ocupação do volume',
                    value: storageQuery.data ? formatBytes(storageQuery.data.allocatedBytes) : '—',
                  },
                  {
                    icon: ArrowDownTrayIcon,
                    label: 'Tráfego recebido',
                    value: stats ? formatBytes(stats.networkRxBytes) : '—',
                  },
                  {
                    icon: ArrowUpTrayIcon,
                    label: 'Tráfego enviado',
                    value: stats ? formatBytes(stats.networkTxBytes) : '—',
                  },
                ].map(({ icon: Icon, label, value }) => (
                  <div className="stat-detail" key={label}>
                    <Icon />
                    <span>{label}</span>
                    <strong>{value}</strong>
                  </div>
                ))}
              </div>
            </section>
          )}
          {tab === 'settings' && (
            <ServerSettings key={server.id} server={server} />
          )}
          {accessibleTabs.files && (tab === 'files' || openedFileServer === server.id) && (
            <div hidden={tab !== 'files'}>
              <Suspense fallback={<p role="status" className="text-sm text-gray-400">Carregando arquivos…</p>}>
                <ServerFiles key={server.id} serverId={server.id} visible={tab === 'files'} suspended={server.isSuspended}
                  stopped={server.status === 'Stopped'} permissions={permissions} />
              </Suspense>
            </div>
          )}
          {tab === 'backups' && (
            <Suspense fallback={<p role="status" className="text-sm text-gray-400">Carregando backups…</p>}>
              <ServerBackups key={server.id} serverId={server.id} permissions={permissions}
                canCreate={!server.isSuspended && server.status === 'Stopped' && !!server.containerId} />
            </Suspense>
          )}
          {tab === 'schedules' && <ServerSchedules key={server.id} serverId={server.id} permissions={permissions} />}
          {tab === 'assistant' && <ServerAssistant serverId={server.id} />}
          {tab === 'members' && <Suspense fallback={<p role="status">Carregando membros…</p>}>
            <ServerMembers key={server.id} serverId={server.id} sharedPermissions={permissions} />
          </Suspense>}
        </div>
      </div>
      <ConfirmDialog
        open={deleteOpen}
        pending={remove.isPending}
        title="Excluir este servidor?"
        description={`O servidor ${server.name} será removido do painel. Esta ação é permanente. Faça um backup dos arquivos antes de continuar.`}
        onCancel={() => setDeleteOpen(false)}
        onConfirm={() => remove.mutate()}
      />
    </div>
  )
}
