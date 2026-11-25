import api from '@/lib/httpClient'
import type {
  AuthResponse,
  Server,
  ServerBackup,
  ServerMemberships,
  ServerMigration,
  MigrationAction,
  ServerStats,
  Node,
  Allocation,
  Egg,
  EggRuntime,
  Nest,
  ImportedEgg,
  NestSummary,
  DashboardStats,
  CreateServerRequest,
  CreateNodeRequest,
  User,
  NodeConnectionTestResult,
  InviteCode,
  GameTemplate,
  DeployServerRequest,
  DeploymentProgress,
  ResourceRecommendation,
  CartridgeListItem,
  CartridgeDetail,
  CartridgeCategory,
  CreateCartridgeServerRequest,
} from '@/types'

// Auth
export const authService = {
  logout: async (): Promise<void> => {
    await api.post('/auth/logout')
  },
  login: async (username: string, password: string, totpCode?: string): Promise<AuthResponse> => {
    const { data } = await api.post<AuthResponse>('/auth/login', { username, password, totpCode })
    return data
  },

  register: async (
    username: string,
    email: string,
    password: string,
    inviteCode: string,
    firstName?: string,
    lastName?: string
  ): Promise<AuthResponse> => {
    const { data } = await api.post<AuthResponse>('/auth/register', {
      username,
      email,
      password,
      inviteCode,
      firstName,
      lastName,
    })
    return data
  },

  getCurrentUser: async (): Promise<User> => {
    const { data } = await api.get<User>('/auth/me')
    return data
  },

  updateProfile: async (firstName?: string, lastName?: string, email?: string) => {
    await api.put('/auth/me', { firstName, lastName, email })
  },

  changePassword: async (currentPassword: string, newPassword: string) => {
    await api.post('/auth/change-password', { currentPassword, newPassword })
  },
}

// Servers
export const serverService = {
  listMembers: async (id: string, signal: AbortSignal): Promise<ServerMemberships> =>
    (await api.get<ServerMemberships>(`/servers/${id}/members`, { signal })).data,

  setMemberPermissions: async (id: string, userId: string, permissions: string[], operation: 'create' | 'update'): Promise<void> => {
    await api.put(`/servers/${id}/members/${userId}`, { permissions, operation })
  },

  removeMember: async (id: string, userId: string): Promise<void> => {
    await api.delete(`/servers/${id}/members/${userId}`)
  },

  getAll: async (): Promise<Server[]> => {
    const { data } = await api.get<Server[]>('/servers')
    return data
  },

  getById: async (id: string): Promise<Server> => {
    const { data } = await api.get<Server>(`/servers/${id}`)
    return data
  },

  getByIdentifier: async (identifier: string): Promise<Server> => {
    const { data } = await api.get<Server>(`/servers/identifier/${identifier}`)
    return data
  },

  create: async (request: CreateServerRequest): Promise<Server> => {
    const { data } = await api.post<Server>('/servers', request)
    return data
  },

  update: async (id: string, request: Partial<CreateServerRequest>): Promise<Server> => {
    const { data } = await api.put<Server>(`/servers/${id}`, request)
    return data
  },

  delete: async (id: string): Promise<void> => {
    await api.delete(`/servers/${id}`)
  },

  power: async (id: string, action: 'start' | 'stop' | 'restart' | 'kill') => {
    const timeout = action === 'stop' || action === 'restart' ? 145_000 : 30_000
    const { data } = await api.post(`/servers/${id}/power`, { action }, { timeout })
    return data
  },

  getLogs: async (id: string, tail: number = 100): Promise<{ logs: string }> => {
    const { data } = await api.get<{ logs: string }>(`/servers/${id}/logs`, {
      params: { tail },
    })
    return data
  },

  downloadFile: async (id: string, path: string): Promise<Blob> => {
    const { data } = await api.get<Blob>(`/servers/${id}/files/download`, { params: { path }, responseType: 'blob' })
    return data
  },

  listFiles: async (id: string, path: string, signal: AbortSignal, cursor: string | null, filter: string) => {
    const { data } = await api.get<{
      entries: { name: string; isDirectory: boolean; mode?: string | null }[]
      truncated: boolean
      nextCursor?: string | null
    }>(
      `/servers/${id}/files`, { params: { path, cursor: cursor ?? undefined, filter: filter || undefined }, signal }
    )
    return data
  },

  getStorageUsage: async (id: string, signal: AbortSignal) =>
    (await api.get<{ logicalBytes: number; allocatedBytes: number; sampledAt: string }>(
      `/servers/${id}/storage`, { signal }
    )).data,

  listBackups: async (id: string, signal: AbortSignal): Promise<ServerBackup[]> =>
    (await api.get<ServerBackup[]>(`/servers/${id}/backups`, { signal })).data,

  createBackup: async (id: string, name: string, exclusions: string): Promise<ServerBackup> =>
    (await api.post<ServerBackup>(`/servers/${id}/backups`, { name, exclusions })).data,

  prepareBackupRestore: async (id: string, backupId: string): Promise<ServerBackup> =>
    (await api.post<ServerBackup>(`/servers/${id}/backups/${backupId}/restore`)).data,

  setBackupProtection: async (id: string, backupId: string, isLocked: boolean) => {
    await api.put(`/servers/${id}/backups/${backupId}/protection`, { isLocked })
  },

  deleteBackup: async (id: string, backupId: string) => {
    await api.delete(`/servers/${id}/backups/${backupId}`)
  },

  uploadFile: async (id: string, path: string, file: File, signal: AbortSignal,
    onProgress: (percentage: number) => void): Promise<void> => {
    await api.post(`/servers/${id}/files/upload`, file, {
      params: { path }, headers: { 'Content-Type': 'application/octet-stream' }, signal, timeout: 315000,
      onUploadProgress: ({ loaded }) => onProgress(file.size ? Math.min(100, Math.floor(100 * loaded / file.size)) : 100),
    })
  },

  editTextFile: async (id: string, path: string, content: string, sha256: string): Promise<void> => {
    await api.put(`/servers/${id}/files/text`, { content, sha256 }, { params: { path } })
  },

  createDirectory: async (id: string, path: string): Promise<void> => {
    await api.post(`/servers/${id}/files/directories`, { path })
  },

  moveFile: async (id: string, source: string, destination: string): Promise<void> => {
    await api.post(`/servers/${id}/files/move`, { source, destination })
  },

  changeFilePermissions: async (id: string, directory: string, entries: string[], mode: string): Promise<void> => {
    await api.post(`/servers/${id}/files/chmod`, { directory, entries, mode })
  },

  copyFile: async (id: string, source: string, destination: string): Promise<void> => {
    await api.post(`/servers/${id}/files/copy`, { source, destination }, { timeout: 38000 })
  },

  compressFiles: async (id: string, directory: string, entries: string[], destination: string): Promise<void> => {
    await api.post(`/servers/${id}/files/compress`, { directory, entries, destination }, { timeout: 38000 })
  },

  extractArchive: async (id: string, source: string, destination: string): Promise<void> => {
    await api.post(`/servers/${id}/files/extract`, { source, destination }, { timeout: 38000 })
  },

  deleteFileEntries: async (id: string, directory: string, entries: string[]): Promise<void> => {
    await api.post(`/servers/${id}/files/delete`, { directory, entries }, { timeout: 32_000 })
  },

  sendCommand: async (id: string, command: string) => {
    const { data } = await api.post(`/servers/${id}/command`, { command })
    return data
  },

  getStats: async (id: string): Promise<ServerStats> => {
    const { data } = await api.get<ServerStats>(`/servers/${id}/stats`)
    return data
  },

  suspend: async (id: string, suspended: boolean) => {
    await api.post(`/servers/${id}/suspend`, null, { params: { suspended } })
  },
}

// Nodes
export const nodeService = {
  getCpuCores: async (id: string, signal: AbortSignal): Promise<number[]> => {
    const { data } = await api.get<number[]>(`/nodes/${id}/cpu-cores`, { signal })
    return data
  },
  getAll: async (): Promise<Node[]> => {
    const { data } = await api.get<Node[]>('/nodes')
    return data
  },

  getById: async (id: string): Promise<Node> => {
    const { data } = await api.get<Node>(`/nodes/${id}`)
    return data
  },

  create: async (request: CreateNodeRequest): Promise<Node> => {
    const { data } = await api.post<Node>('/nodes', request)
    return data
  },

  update: async (id: string, request: Partial<CreateNodeRequest>): Promise<Node> => {
    const { data } = await api.put<Node>(`/nodes/${id}`, request)
    return data
  },

  delete: async (id: string): Promise<void> => {
    await api.delete(`/nodes/${id}`)
  },

  getAllocations: async (nodeId: string): Promise<Allocation[]> => {
    const { data } = await api.get<Allocation[]>(`/nodes/${nodeId}/allocations`)
    return data
  },

  createAllocations: async (
    nodeId: string,
    ipAddress: string,
    startPort: number,
    endPort?: number,
    alias?: string
  ) => {
    await api.post(`/nodes/${nodeId}/allocations`, {
      ipAddress,
      startPort,
      endPort,
      alias,
    })
  },

  deleteAllocation: async (allocationId: string) => {
    await api.delete(`/nodes/allocations/${allocationId}`)
  },

  testConnection: async (dockerEndpoint: string): Promise<NodeConnectionTestResult> => {
    const { data } = await api.post<NodeConnectionTestResult>('/nodes/test-connection', {
      dockerEndpoint,
    })
    return data
  },

  testNodeConnection: async (nodeId: string): Promise<NodeConnectionTestResult> => {
    const { data } = await api.post<NodeConnectionTestResult>(`/nodes/${nodeId}/test-connection`)
    return data
  },

  regenerateToken: async (nodeId: string): Promise<void> => {
    await api.post(`/nodes/${nodeId}/regenerate-token`)
  },
}

// Admin
export const adminService = {
  getMigrations: async (page: number, pageSize: number, query: string, signal: AbortSignal) => {
    const { data, headers } = await api.get<ServerMigration[]>('/admin/migrations/servers', { params: { page, pageSize, query }, signal })
    return { items: data, total: Number(headers['x-total-count'] ?? data.length) }
  },

  submitMigrationReceipt: async (id: string, action: MigrationAction, json: string) => {
    await api.post(`/admin/migrations/servers/${encodeURIComponent(id)}/${action}`, json, {
      headers: { 'Content-Type': 'application/json' },
      timeout: 38_000,
    })
  },
  getEggRuntime: async (id: string, signal: AbortSignal) =>
    (await api.get<EggRuntime>(`/admin/egg-catalog/eggs/${id}/runtime`, { signal })).data,
  getDashboardStats: async (): Promise<DashboardStats> => {
    const { data } = await api.get<DashboardStats>('/admin/dashboard')
    return data
  },

  getAllUsers: async (): Promise<User[]> => {
    const { data } = await api.get<User[]>('/admin/users')
    return data
  },

  deleteUser: async (id: string): Promise<void> => {
    await api.delete(`/admin/users/${id}`)
  },

  getNests: async (): Promise<Nest[]> => {
    const { data } = await api.get<Nest[]>('/admin/nests')
    return data
  },

  getEggs: async (nestId?: string): Promise<Egg[]> => {
    const url = nestId ? `/admin/nests/${nestId}/eggs` : '/admin/eggs'
    const { data } = await api.get<Egg[]>(url)
    return data
  },

  getEggNests: async (page: number, pageSize: number, signal?: AbortSignal) => {
    const { data, headers } = await api.get<NestSummary[]>('/admin/egg-catalog/nests', { params: { page, pageSize }, signal })
    return { items: data, total: Number(headers['x-total-count'] ?? data.length) }
  },

  createNest: async (name: string): Promise<NestSummary> => {
    const { data } = await api.post<NestSummary>('/admin/egg-catalog/nests', { name })
    return data
  },

  getImportedEggs: async (nestId: string, page: number, pageSize: number, signal?: AbortSignal) => {
    const { data, headers } = await api.get<ImportedEgg[]>(`/admin/egg-catalog/nests/${nestId}/eggs`, { params: { page, pageSize }, signal })
    return { items: data, total: Number(headers['x-total-count'] ?? data.length) }
  },

  importEgg: async (nestId: string, manifest: File) => {
    const { status } = await api.post(`/admin/egg-catalog/nests/${nestId}/eggs/import`, manifest, {
      headers: { 'Content-Type': 'application/json' },
    })
    return status === 201
  },

  exportEgg: async (id: string): Promise<Blob> => {
    const { data } = await api.get<Blob>(`/admin/egg-catalog/eggs/${id}/export`, { responseType: 'blob' })
    return data
  },

  // Invite code management
  getInviteCodes: async (): Promise<InviteCode[]> => {
    const { data } = await api.get<InviteCode[]>('/admin/invites')
    return data
  },

  createInviteCode: async (maxUses: number = 1, expiresAt?: string): Promise<InviteCode> => {
    const { data } = await api.post<InviteCode>('/admin/invites', { maxUses, expiresAt })
    return data
  },

  revokeInviteCode: async (id: string): Promise<void> => {
    await api.delete(`/admin/invites/${id}`)
  },
}

// Deployment
export const deploymentService = {
  getTemplates: async (): Promise<GameTemplate[]> => {
    const { data } = await api.get<GameTemplate[]>('/deployment/templates')
    return data
  },

  getTemplate: async (id: string): Promise<GameTemplate> => {
    const { data } = await api.get<GameTemplate>(`/deployment/templates/${id}`)
    return data
  },

  getTemplatesByCategory: async (category: string): Promise<GameTemplate[]> => {
    const { data } = await api.get<GameTemplate[]>(`/deployment/templates/category/${category}`)
    return data
  },

  deploy: async (request: DeployServerRequest): Promise<Server> => {
    const { data } = await api.post<Server>('/deployment/deploy', request)
    return data
  },

  quickDeploy: async (
    gameTemplateId: string,
    serverName: string,
    nodeId: string,
    overrideVariables?: Record<string, string>
  ): Promise<Server> => {
    const { data } = await api.post<Server>('/deployment/quick-deploy', {
      gameTemplateId,
      serverName,
      nodeId,
      overrideVariables,
    })
    return data
  },

  getDeploymentStatus: async (serverId: string): Promise<DeploymentProgress> => {
    const { data } = await api.get<DeploymentProgress>(`/deployment/status/${serverId}`)
    return data
  },

  getRecommendations: async (templateId: string, playerCount: number): Promise<ResourceRecommendation> => {
    const { data } = await api.get<ResourceRecommendation>(
      `/deployment/templates/${templateId}/recommendations`,
      { params: { playerCount } }
    )
    return data
  },
}

// Cartridges
export const cartridgeService = {
  getAll: async (): Promise<CartridgeListItem[]> => {
    const { data } = await api.get<CartridgeListItem[]>('/cartridges')
    return data
  },

  getById: async (id: string): Promise<CartridgeDetail> => {
    const { data } = await api.get<CartridgeDetail>(`/cartridges/${id}`)
    return data
  },

  getByCategory: async (category: string): Promise<CartridgeListItem[]> => {
    const { data } = await api.get<CartridgeListItem[]>(`/cartridges/category/${category}`)
    return data
  },

  getCategories: async (): Promise<CartridgeCategory[]> => {
    const { data } = await api.get<CartridgeCategory[]>('/cartridges/categories')
    return data
  },

  validateVariables: async (
    cartridgeId: string, 
    values: Record<string, unknown>
  ): Promise<{ valid: boolean; errors: string[] }> => {
    const { data } = await api.post<{ valid: boolean; errors: string[] }>(
      `/cartridges/${cartridgeId}/validate`,
      values
    )
    return data
  },

  reload: async (): Promise<void> => {
    await api.post('/cartridges/reload')
  },

  createServer: async (request: CreateCartridgeServerRequest, eggId?: string): Promise<Server> => {
    const endpoint = eggId ? `/servers/from-egg/${eggId}` : '/servers/from-cartridge'
    const { data } = await api.post<Server>(endpoint, request)
    return data
  },
}
