import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ConfirmDialog } from '@/components/ui/ConfirmDialog'
import { getErrorMessage } from '@/lib/errorHandler'
import { hasServerPermission } from '@/lib/serverPermissions'
import { serverService } from '@/services/api'
import type { ServerMember } from '@/types'

const preview = import.meta.env.DEV && import.meta.env.VITE_DEMO_MODE === 'true'
const uuid = /^[\da-f]{8}(?:-[\da-f]{4}){3}-[\da-f]{12}$/i
const permissionGroups = {
  'Operação': {
    'websocket.connect': 'Ler o console', 'control.console': 'Enviar comandos',
    'control.start': 'Iniciar', 'control.stop': 'Parar', 'control.restart': 'Reiniciar',
  },
  'Arquivos': {
    'file.read': 'Listar arquivos', 'file.read-content': 'Ler e baixar arquivos',
    'file.create': 'Criar, copiar, editar e extrair arquivos', 'file.update': 'Mover, renomear e alterar permissões de arquivos',
    'file.archive': 'Compactar arquivos e pastas',
    'file.delete': 'Excluir arquivos e pastas',
  },
  'Backups': {
    'backup.read': 'Listar backups', 'backup.create': 'Criar backups', 'backup.download': 'Baixar backups',
    'backup.restore': 'Preparar restauração', 'backup.delete': 'Proteger e excluir backups',
  },
  'Agendamentos': {
    'schedule.read': 'Listar agendas', 'schedule.create': 'Criar agendas',
    'schedule.update': 'Ativar e pausar agendas', 'schedule.delete': 'Excluir agendas',
  },
  'Membros': {
    'user.read': 'Listar membros', 'user.create': 'Adicionar membros',
    'user.update': 'Editar permissões de membros', 'user.delete': 'Revogar acesso de membros',
  },
  'Painel': { 'settings.rename': 'Editar nome e descrição', 'integration.ai': 'Usar o assistente local' },
}

type MemberChange = { userId: string; operation: 'remove' } | { userId: string; operation: 'create' | 'update'; permissions: string[] }

export default function ServerMembers({ serverId, sharedPermissions }: { serverId: string; sharedPermissions?: string[] | null }) {
  const canList = hasServerPermission(sharedPermissions, 'user.read')
  const canCreate = hasServerPermission(sharedPermissions, 'user.create')
  const canUpdate = hasServerPermission(sharedPermissions, 'user.update')
  const canDelete = hasServerPermission(sharedPermissions, 'user.delete')
  const cache = useQueryClient()
  const queryKey = ['server-members', serverId]
  const [userId, setUserId] = useState('')
  const [permissions, setPermissions] = useState<string[]>([])
  const [editing, setEditing] = useState<ServerMember | null>(null)
  const [removing, setRemoving] = useState<{ userId: string; username?: string } | null>(null)
  const [directOperation, setDirectOperation] = useState<'create' | 'update'>('create')
  const [revokeId, setRevokeId] = useState('')
  const [notice, setNotice] = useState('')
  const members = useQuery({ queryKey, enabled: !preview && canList, queryFn: ({ signal }) => serverService.listMembers(serverId, signal), retry: false })
  const availablePermissions = canList ? (members.data?.availablePermissions ?? []) : (sharedPermissions ?? [])
  const operation = editing || !canCreate ? 'update' : !canList && canUpdate ? directOperation : 'create'
  const canWrite = operation === 'create' ? canCreate : canUpdate && (!canList || !!editing?.canEdit)
  const availableGroups = Object.entries(permissionGroups).map(([group, names]) => ({
    group, permissions: Object.entries(names).filter(([name]) => availablePermissions.includes(name)),
  })).filter((entry) => entry.permissions.length > 0)
  const change = useMutation({
    mutationFn: (action: MemberChange) => action.operation === 'remove'
      ? serverService.removeMember(serverId, action.userId)
      : serverService.setMemberPermissions(serverId, action.userId, action.permissions, action.operation),
    onSuccess: (_, action) => {
      setNotice(action.operation === 'remove' ? 'Acesso revogado.' : 'Acesso salvo.')
      if (action.operation !== 'remove' || editing?.userId === action.userId) resetEditor()
      setRevokeId('')
      setRemoving(null)
      return cache.invalidateQueries({ queryKey })
    },
    onError: () => setRemoving(null),
  })

  function resetEditor() {
    setEditing(null)
    setUserId('')
    setPermissions([])
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    if (canWrite && !preview && (!canList || members.isSuccess) && uuid.test(userId.trim()) && !change.isPending)
      change.mutate({ userId: userId.trim(), permissions, operation })
  }

  return (
    <section className="space-y-5 rounded-xl border border-panel-border bg-panel-card p-5" aria-labelledby="server-members-heading">
      <h3 id="server-members-heading" className="font-semibold">Compartilhar acesso</h3>
      {preview && <p className="text-sm text-gray-400">O compartilhamento fica disponível no painel conectado.</p>}
      {!preview && canList && members.isPending && <p role="status">Carregando membros…</p>}
      {canList && members.isError && <p role="alert" className="text-red-400">Não foi possível carregar os membros.</p>}
      {notice && <p role="status" className="text-sm text-gray-300">{notice}</p>}
      {change.isError && <p role="alert" className="text-sm text-red-400">{getErrorMessage(change.error, 'Não foi possível alterar o acesso.')}</p>}
      {canList && members.data && <>
        {members.data.members.length === 0 && <p className="text-sm text-gray-400">Nenhuma conta tem acesso compartilhado.</p>}
        <ul className="divide-y divide-panel-border">
          {members.data.members.map((member) => (
            <li key={member.userId} className="flex flex-wrap items-center justify-between gap-3 py-3">
              <div>
                <h4 className="font-medium">{member.username}</h4>
                <p className="text-sm text-gray-400">{!member.isActive ? 'Conta inativa' : member.permissions.length === 1 ? '1 permissão' : `${member.permissions.length} permissões`}</p>
              </div>
              {(member.canEdit || member.canRemove) && <div className="flex flex-wrap gap-2">
                {canUpdate && member.canEdit && <button type="button" className="button button-secondary" disabled={change.isPending}
                  aria-label={`Editar acesso de ${member.username}`} onClick={() => {
                    change.reset(); setNotice(''); setEditing(member); setUserId(member.userId); setPermissions(member.permissions)
                  }}>Editar</button>}
                {canDelete && member.canRemove && <button type="button" className="button button-secondary" disabled={change.isPending}
                  aria-label={`Revogar acesso de ${member.username}`} onClick={() => { change.reset(); setNotice(''); setRemoving(member) }}>Revogar</button>}
              </div>}
            </li>
          ))}
        </ul>
      </>}
      {!preview && canWrite && (!canList || members.isSuccess) && (
        <form onSubmit={submit} className="space-y-4 border-t border-panel-border pt-4">
          <h4 className="font-medium">{editing ? `Editar acesso de ${editing.username}` : operation === 'create' ? 'Adicionar membro' : 'Editar acesso'}</h4>
          {!canList && canCreate && canUpdate && <label className="block max-w-md text-sm">Ação
            <select className="mt-1 block w-full rounded-lg border border-panel-border bg-panel-dark px-3 py-2"
              value={directOperation} disabled={change.isPending}
              onChange={(event) => { setDirectOperation(event.target.value as 'create' | 'update'); setPermissions([]) }}>
              <option value="create">Adicionar membro</option><option value="update">Editar acesso</option>
            </select>
          </label>}
          <label className="block max-w-md text-sm">ID da conta
            <input className="mt-1 block w-full rounded-lg border border-panel-border bg-panel-dark px-3 py-2 font-mono"
              value={userId} onChange={(event) => setUserId(event.target.value)} readOnly={!!editing}
              disabled={change.isPending} maxLength={36} required />
          </label>
          <p className="text-sm text-gray-400">A conta encontra o próprio ID em Minha conta. Sem opções marcadas, ela pode ver o servidor e as estatísticas.</p>
          {!canList && operation === 'update' && <p className="text-sm text-gray-400">Selecione todas as permissões que a conta deve ter após a edição.</p>}
          <div className="grid gap-4 lg:grid-cols-2">
            {availableGroups.map(({ group, permissions: names }) => (
              <fieldset key={group} disabled={change.isPending} className="space-y-2 rounded-lg border border-panel-border px-3 pb-3">
                <legend className="px-1 text-sm font-medium">{group}</legend>
                {names.map(([name, label]) => (
                  <label key={name} className="flex items-center gap-2 text-sm">
                    <input type="checkbox" checked={permissions.includes(name)} onChange={(event) => {
                      const checked = event.target.checked
                      setPermissions((previous) => checked ? [...previous, name] : previous.filter((value) => value !== name))
                    }} />{label}
                  </label>
                ))}
              </fieldset>
            ))}
          </div>
          <div className="flex flex-wrap gap-2">
            <button className="button button-primary" disabled={change.isPending || !uuid.test(userId.trim())}>
              {change.isPending ? 'Salvando…' : 'Salvar acesso'}
            </button>
            {editing && <button type="button" className="button button-secondary" disabled={change.isPending} onClick={resetEditor}>Cancelar</button>}
          </div>
        </form>
      )}
      {!preview && canDelete && !canList && <form className="space-y-3 border-t border-panel-border pt-4" onSubmit={(event) => {
        event.preventDefault()
        if (!change.isPending && uuid.test(revokeId.trim())) {
          change.reset()
          setNotice('')
          setRemoving({ userId: revokeId.trim() })
        }
      }}>
        <h4 className="font-medium">Revogar acesso</h4>
        <label className="block max-w-md text-sm">ID da conta a revogar
          <input className="mt-1 block w-full rounded-lg border border-panel-border bg-panel-dark px-3 py-2 font-mono"
            value={revokeId} onChange={(event) => setRevokeId(event.target.value)} disabled={change.isPending} maxLength={36} required />
        </label>
        <button className="button button-secondary" disabled={change.isPending || !uuid.test(revokeId.trim())}>Revogar acesso</button>
      </form>}
      <ConfirmDialog open={canDelete && removing !== null} pending={change.isPending} title="Revogar acesso?"
        description={`${removing?.username ?? 'A conta'} perderá o acesso compartilhado a este servidor.`}
        confirmLabel="Revogar acesso" pendingLabel="Revogando…" onCancel={() => setRemoving(null)}
        onConfirm={() => { if (canDelete && removing) change.mutate({ userId: removing.userId, operation: 'remove' }) }} />
    </section>
  )
}
