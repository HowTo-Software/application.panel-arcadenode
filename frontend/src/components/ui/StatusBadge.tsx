import type { ServerStatus } from '@/types'

const statuses: Record<ServerStatus, { label: string; tone: string }> = {
  Running: { label: 'Online', tone: 'online' },
  Stopped: { label: 'Offline', tone: 'offline' },
  Starting: { label: 'Iniciando', tone: 'pending' },
  Stopping: { label: 'Desligando', tone: 'pending' },
  Installing: { label: 'Instalando', tone: 'pending' },
  Error: { label: 'Com erro', tone: 'error' },
  Suspended: { label: 'Suspenso', tone: 'pending' },
  Paused: { label: 'Pausado', tone: 'pending' },
}
export function StatusBadge({ status }: { status: ServerStatus }) {
  const { label, tone } = statuses[status] || { label: status, tone: 'offline' }
  return (
    <span className={`status-badge ${tone}`}>
      <span className="status-dot" />
      {label}
    </span>
  )
}
