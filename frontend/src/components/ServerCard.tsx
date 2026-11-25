import { useState } from 'react'
import { Link } from 'react-router-dom'
import {
  ArrowUpRightIcon,
  CpuChipIcon,
  CircleStackIcon,
  CubeIcon,
} from '@heroicons/react/24/outline'
import type { Server } from '@/types'
import { gameVisual } from '@/lib/gameVisuals'
import { StatusBadge } from '@/components/ui/StatusBadge'

export default function ServerCard({
  server,
  list = false,
}: {
  server: Server
  list?: boolean
}) {
  const game = gameVisual(server)
  const [imageFailed, setImageFailed] = useState(false)
  return (
    <Link
      to={`/servers/${server.id}`}
      className={`server-card ${list ? 'server-card-list' : ''} game-${game.key}`}
      aria-label={`Gerenciar ${server.name}`}
    >
      <div className="server-artwork">
        {game.cover && !imageFailed ? (
          <img
            className="server-cover"
            src={game.cover}
            alt=""
            onError={() => setImageFailed(true)}
          />
        ) : (
          <div className="server-cover-fallback">
            <span>{game.short}</span>
            <CubeIcon />
          </div>
        )}
        <div className="server-artwork-shade" />
        <div className="server-card-top">
          <StatusBadge status={server.status} />
          <span className="game-edition">{game.label}</span>
        </div>
        <div className="server-card-identity">
          <div className="game-icon">
            {game.icon ? <img src={game.icon} alt="" /> : <CubeIcon />}
          </div>
          <div>
            <h3>{server.name}</h3>
            <span className="server-address">
              {server.ipAddress}:{server.port}
            </span>
          </div>
        </div>
      </div>
      <div className="server-card-resources">
        <span title="Memória alocada">
          <CpuChipIcon />
          {(server.memoryLimit / 1024).toLocaleString('pt-BR', {
            maximumFractionDigits: 1,
          })}{' '}
          GB RAM
        </span>
        <span title="Armazenamento alocado">
          <CircleStackIcon />
          {(server.diskLimit / 1024).toLocaleString('pt-BR', {
            maximumFractionDigits: 1,
          })}{' '}
          GB
        </span>
        <span className="server-node" title={server.nodeName}>
          <CubeIcon />
          {server.nodeName}
        </span>
      </div>
      <div className="server-card-footer">
        <span>Gerenciar servidor</span>
        <ArrowUpRightIcon />
      </div>
    </Link>
  )
}
