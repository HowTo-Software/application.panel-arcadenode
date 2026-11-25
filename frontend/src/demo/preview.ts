import { AxiosError } from 'axios'
import api from '@/lib/httpClient'
import { useAuthStore } from '@/stores/authStore'
import type {
  CartridgeDetail,
  CartridgeListItem,
  DashboardStats,
  Node,
  Server,
  ServerMigration,
  User,
} from '@/types'

const createdAt = '2026-11-01T12:00:00Z'
const user: User = {
  id: 'demo-user',
  username: 'preview',
  email: 'preview@example.com',
  firstName: 'Administrador',
  lastName: 'Demo',
  role: 'SuperAdmin',
  isActive: true,
  createdAt,
  lastLoginAt: null,
}

const games = [
  {
    id: 'minecraft-java',
    name: 'Minecraft Survival',
    game: 'Minecraft Java',
    icon: '⛏️',
    port: 25565,
    memory: 4096,
  },
  {
    id: 'project-zomboid',
    name: 'Zomboid Brasil',
    game: 'Project Zomboid',
    icon: '🧟',
    port: 16261,
    memory: 8192,
  },
  {
    id: 'arma-reforger',
    name: 'Arma Operations',
    game: 'Arma Reforger',
    icon: '🎖️',
    port: 2001,
    memory: 8192,
  },
  {
    id: 'minecraft-bedrock',
    name: 'Bedrock Friends',
    game: 'Minecraft Bedrock',
    icon: '🧱',
    port: 19132,
    memory: 2048,
  },
  {
    id: 'minecraft-mods',
    name: 'Minecraft Modded',
    game: 'Minecraft Java',
    icon: '⛏️',
    port: 25566,
    memory: 6144,
  },
  {
    id: 'zomboid-hardcore',
    name: 'Zomboid Hardcore',
    game: 'Project Zomboid',
    icon: '🧟',
    port: 16263,
    memory: 4096,
  },
]

const servers: Server[] = games.map((game, index) => ({
  id: game.id,
  name: game.name,
  description: `${game.game} — demonstração visual`,
  identifier: `demo000${index}`,
  containerId: `demo-container-${index}`,
  dockerImage: `arcadenode/${game.id}:demo`,
  startupCommand: null,
  memoryLimit: game.memory,
  cpuLimit: 200,
  diskLimit: 20480,
  port: game.port,
  ipAddress: '192.0.2.10',
  status: index === 2 ? 'Stopped' : 'Running',
  isSuspended: false,
  createdAt,
  lastStartedAt: new Date(Date.now() - 7200000).toISOString(),
  ownerId: user.id,
  ownerUsername: user.username,
  nodeId: 'demo-node',
  nodeName: 'São Paulo · BR-01',
}))

const migrationStages = ['AwaitingVerification', 'Prepared', 'Released'] as const
const migrations: ServerMigration[] = migrationStages.map((stage, index) => ({
  id: `80f641c6-0212-47ed-a043-0510ef00000${index + 1}`,
  name: servers[index].name, identifier: servers[index].identifier,
  nodeName: servers[index].nodeName ?? 'Node de demonstração', stage,
  volumeSha256: index ? 'ab'.repeat(32) : null,
  verifiedAt: index ? '2026-11-25T04:05:00Z' : null,
  releasedAt: index === 2 ? '2026-11-25T04:14:00Z' : null,
}))

const nodes: Node[] = [
  {
    id: 'demo-node',
    name: 'São Paulo · BR-01',
    description: 'Nó fictício para prévia',
    fqdn: 'node.example.com',
    daemonPort: 8080,
    sftpPort: 2022,
    useSsl: true,
    behindProxy: false,
    dockerEndpoint: 'demo://offline',
    totalMemory: 65536,
    totalDisk: 524288,
    memoryCapacity: 65536,
    diskCapacity: 524288,
    cpuCapacityPercent: 1400,
    allocatedCpuPercent: servers.reduce((sum, server) => sum + server.cpuLimit, 0),
    allocatedMemory: servers.reduce(
      (sum, server) => sum + server.memoryLimit,
      0
    ),
    allocatedDisk: servers.length * 20480,
    isOnline: true,
    isMaintenanceMode: false,
    serverCount: servers.length,
    createdAt,
    lastCheckedAt: new Date().toISOString(),
  },
]

const dashboard: DashboardStats = {
  totalUsers: 1,
  totalServers: servers.length,
  totalNodes: nodes.length,
  runningServers: servers.filter((server) => server.status === 'Running')
    .length,
  onlineNodes: 1,
  totalAllocatedMemory: nodes[0].allocatedMemory,
  totalAllocatedDisk: nodes[0].allocatedDisk,
}

// The real cartridge catalog currently supports these two games.
const cartridges: CartridgeListItem[] = games.slice(0, 2).map((game) => ({
  id: game.id,
  name: game.game,
  description:
    game.id === 'minecraft-java'
      ? 'Crie seu mundo com Paper, Vanilla ou mods.'
      : 'Sobreviva ao apocalipse com sua comunidade.',
  category: game.id === 'minecraft-java' ? 'Sandbox' : 'Sobrevivência',
  tags: ['Multiplayer'],
  icon:
    game.id === 'minecraft-java'
      ? '/games/minecraft-icon.png'
      : '/games/zomboid-icon.png',
  dockerImage: `arcadenode/${game.id}:demo`,
  recommendedMemory: game.memory,
  recommendedCpu: 100,
  defaultPort: game.port,
}))

function cartridgeDetail(item: CartridgeListItem): CartridgeDetail {
  return {
    ...item,
    author: 'ArcadeNode',
    website: null,
    docker: { image: item.dockerImage, baseImage: null },
    ports: [
      {
        name: 'Jogo',
        default: item.defaultPort,
        protocol: 'tcp',
        required: true,
        description: 'Porta principal',
      },
    ],
    resources: {
      memory: {
        minimum: 1024,
        recommended: item.recommendedMemory,
        maximum: 16384,
      },
      cpu: { minimum: 25, recommended: 100, maximum: 400 },
    },
    variables: [
      {
        id: 'server-name',
        name: 'Nome no jogo',
        description: 'Nome exibido aos jogadores.',
        envVariable: 'SERVER_NAME',
        type: 'string',
        default: 'Meu servidor',
        required: true,
        userViewable: true,
        userEditable: true,
        validation: null,
        ui: null,
      },
    ],
    variableGroups: [],
    console: null,
    startup: { command: '', readyPattern: null, readyTimeout: 120 },
    process: { stopCommand: 'stop', stopTimeout: 30, restartOnCrash: false },
  }
}

function readPreview(path: string, directory?: string): unknown {
  const demoNestId = '00000000-0000-0000-0000-000000000002'
  const demoEggId = '00000000-0000-0000-0000-000000000003'
  if (path === '/admin/egg-catalog/nests') return [{ id: demoNestId, name: 'Minecraft', description: '', importedEggCount: 1 }]
  if (path === `/admin/egg-catalog/eggs/${demoEggId}/runtime`) return { cartridgeId: 'minecraft-java' }
  if (path === `/admin/egg-catalog/nests/${demoNestId}/eggs`) return [{
    id: demoEggId, nestId: demoNestId, name: 'Paper', description: 'Perfil de demonstração',
    importedAt: '2026-11-24T00:00:00Z', sha256: '0'.repeat(64), cartridgeId: 'minecraft-java',
  }]
  if (path === '/auth/me') return user
  if (path === '/admin/dashboard') return dashboard
  if (path === '/admin/users') return [user]
  if (path === '/servers') return servers
  if (path === '/nodes') return nodes
  if (path === '/deployment/templates')
    return games.slice(0, 4).map((game) => ({
      id: game.id,
      name: game.game,
      description: 'Prévia do catálogo de jogos',
      icon: game.icon,
      defaultMemory: game.memory,
      ports: [
        {
          name: 'Game',
          defaultPort: game.port,
          protocol: 'udp',
          required: true,
        },
      ],
    }))
  if (path === '/cartridges') return cartridges
  if (path === '/cartridges/categories')
    return cartridges.map((item) => ({ name: item.category, count: 1 }))
  if (path.startsWith('/cartridges/')) {
    const item = cartridges.find((item) => item.id === path.split('/')[2])
    if (item) return cartridgeDetail(item)
  }
  if (['/admin/invites', '/admin/nests', '/admin/eggs'].includes(path))
    return []
  if (path.endsWith('/cpu-cores')) return [0, 1, 2, 3]
  if (path.startsWith('/nodes/'))
    return path.endsWith('/allocations') ? [] : nodes[0]
  const match = path.match(/^\/servers\/([^/]+)(?:\/(stats|logs|files|backups|schedules|storage))?$/)
  if (match) {
    const migration = migrations.find((item) => item.id === match[1])
    const entry = servers.find((item) => item.id === match[1] || item.identifier === migration?.identifier)
    if (!entry) throw new Error('Servidor de demonstração não encontrado')
    const server = migration ? { ...entry, id: migration.id } : entry
    if (match[2] === 'storage') return { logicalBytes: 2147483648, allocatedBytes: 2147483648, sampledAt: new Date().toISOString() }
    if (match[2] === 'backups') return [{
      id: 'demo-backup', name: 'Antes da atualização', size: 5242880, checksum: 'ab'.repeat(32),
      isSuccessful: true, isQueued: false, createdAt: '2026-11-24T19:00:00Z', completedAt: '2026-11-24T19:01:00Z',
      isLocked: false, deletionStatus: 'None', canDelete: true,
      restoreAvailable: true, restoreStatus: 'None', restoreNodeId: null, restoreCompletedAt: null,
      restoredFiles: null, restoredDirectories: null, restoredBytes: null,
    }]
    if (match[2] === 'schedules') return [{
      id: 'demo-maintenance', name: 'Reinício diário', cronExpression: '0 4 * * *', isActive: true, isProcessing: false, waitingForBackup: false,
      onlyWhenOnline: true, nextRunAt: new Date(Date.now() + 3600000).toISOString(),
      tasks: [{ action: 'Power', payload: 'restart', timeOffset: 0, continueOnFailure: false }],
    }]
    if (match[2] === 'files') return {
      entries: directory
        ? [{ name: 'save.dat', isDirectory: false }]
        : [{ name: 'saves', isDirectory: true }, { name: 'server.cfg', isDirectory: false }],
      truncated: false,
    }
    if (match[2] === 'logs')
      return {
        logs: '[ArcadeNode] Ambiente de demonstração carregado.\n[ArcadeNode] Este console exibe dados fictícios, sem conexão com um jogo real.\n[INFO] Preparando ambiente do servidor...\n[INFO] Configurações e recursos carregados.\n[INFO] Aguardando conexão dos jogadores.\n[INFO] Prévia pronta. Explore as abas para conhecer o painel.',
      }
    if (match[2] === 'stats')
      return {
        serverId: server.id,
        status: server.status,
        memoryUsage: server.memoryLimit * 1024 * 1024 * 0.42,
        cpuUsage: 18.5,
        diskUsage: 2147483648,
        blockIoBytes: 4194304,
        networkRxBytes: 52428800,
        networkTxBytes: 104857600,
        timestamp: new Date().toISOString(),
      }
    return server
  }
  throw new Error('Esta tela ainda não possui dados de demonstração')
}

/** Development-only visual preview. Never contacts a backend or writes a session. */
export function enableDemo() {
  // Preserve any real stored session: demo state exists only in memory.
  useAuthStore.persist.setOptions({
    storage: { getItem: () => null, setItem: () => {}, removeItem: () => {} },
  })
  useAuthStore.setState({ user, token: null, isAuthenticated: true })

  api.defaults.adapter = async (config) => {
    if (config.method?.toLowerCase() !== 'get') {
      throw new AxiosError(
        'Prévia visual: alterações e comandos estão desativados.',
        'ERR_DEMO_READONLY',
        config
      )
    }
    if (config.url === '/admin/migrations/servers') {
      const term = String(config.params?.query ?? '').toLowerCase()
      const result = migrations.filter((item) => [item.id, item.name, item.identifier].some((value) => value.toLowerCase().includes(term)))
      const size = Number(config.params?.pageSize ?? 12)
      const from = (Number(config.params?.page ?? 1) - 1) * size
      return { data: result.slice(from, from + size), status: 200, statusText: 'OK', headers: { 'x-total-count': String(result.length) }, config }
    }
    const directory = config.params?.path
    const data = readPreview(config.url?.split('?')[0] ?? '', typeof directory === 'string' ? directory : undefined)
    return { data, status: 200, statusText: 'OK', headers: {}, config }
  }

  if (location.pathname === '/login' || location.pathname === '/register') {
    history.replaceState(null, '', '/')
  }
}
