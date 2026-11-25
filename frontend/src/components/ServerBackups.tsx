import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AxiosError } from 'axios'
import { ConfirmDialog } from '@/components/ui/ConfirmDialog'
import { hasServerPermission } from '@/lib/serverPermissions'
import { serverService } from '@/services/api'
import { downloadServerBackup } from '@/services/downloads'
import type { ServerBackup } from '@/types'

const preview = import.meta.env.DEV && import.meta.env.VITE_DEMO_MODE === 'true'
const dateFormat = new Intl.DateTimeFormat('pt-BR', { dateStyle: 'short', timeStyle: 'short' })
const sizeFormat = new Intl.NumberFormat('pt-BR', { style: 'unit', unit: 'byte' })

export default function ServerBackups({ serverId, canCreate, permissions }: {
  serverId: string; canCreate: boolean; permissions?: string[] | null
}) {
  const canList = hasServerPermission(permissions, 'backup.read')
  const canRequestBackup = hasServerPermission(permissions, 'backup.create')
  const canManage = hasServerPermission(permissions, 'backup.delete')
  const canDownload = hasServerPermission(permissions, 'backup.download')
  const canRestore = hasServerPermission(permissions, 'backup.restore')
  const [name, setName] = useState('')
  const [exclusions, setExclusions] = useState('')
  const [selectedForDeletion, setSelectedForDeletion] = useState<ServerBackup | null>(null)
  const client = useQueryClient()
  const queryKey = ['server-backups', serverId]
  const backups = useQuery({
    queryKey,
    enabled: canList,
    queryFn: ({ signal }) => serverService.listBackups(serverId, signal),
    refetchInterval: (query) => query.state.data?.some((backup) => backup.isQueued || backup.restoreStatus === 'Queued' ||
      backup.deletionStatus === 'Queued') ? 5000 : false,
  })
  const create = useMutation({
    mutationFn: ({ name, exclusions }: { name: string; exclusions: string }) => serverService.createBackup(serverId, name, exclusions),
    onSuccess: async (backup) => {
      setName('')
      setExclusions('')
      if (!canList) return
      await client.cancelQueries({ queryKey })
      client.setQueryData<ServerBackup[]>(queryKey, (previous) =>
        [backup, ...(previous ?? []).filter((entry) => entry.id !== backup.id)]
      )
      void client.invalidateQueries({ queryKey })
    },
  })
  const download = useMutation({
    mutationFn: (backupId: string) => downloadServerBackup(serverId, backupId),
  })
  const restore = useMutation({
    mutationFn: (backupId: string) => serverService.prepareBackupRestore(serverId, backupId),
    onSuccess: async (backup) => {
      await client.cancelQueries({ queryKey })
      client.setQueryData<ServerBackup[]>(queryKey, (previous) =>
        previous?.map((entry) => entry.id === backup.id ? backup : entry)
      )
      void client.invalidateQueries({ queryKey })
    },
  })
  const manage = useMutation({
    mutationFn: (action: { id: string; type: 'delete' } | { id: string; type: 'protection'; locked: boolean }) =>
      action.type === 'delete' ? serverService.deleteBackup(serverId, action.id)
        : serverService.setBackupProtection(serverId, action.id, action.locked),
    onSuccess: () => {
      setSelectedForDeletion(null)
      return client.invalidateQueries({ queryKey })
    },
    onError: () => setSelectedForDeletion(null),
  })
  const queued = canList && backups.data?.some((backup) => backup.isQueued)
  const restoreQueued = backups.data?.some((backup) => backup.restoreStatus === 'Queued')
  const atLimit = canList && (backups.data?.length ?? 0) >= 20
  const disabled = preview || !canCreate || !canRequestBackup || queued || atLimit || (canList && !backups.isSuccess) || create.isPending
  const exclusionsTooLarge = new TextEncoder().encode(exclusions).length > 32 * 1024
  const status = create.error instanceof AxiosError ? create.error.response?.status : undefined
  const createError = status === 503 ? 'O node deste servidor ainda não está configurado para backups.'
    : status === 409 ? 'Confira o estado do servidor, a fila e o limite de backups.'
      : status === 400 ? 'Confira o formato das regras de exclusão.'
      : 'Não foi possível solicitar o backup.'
  const restoreStatus = restore.error instanceof AxiosError ? restore.error.response?.status : undefined
  const manageStatus = manage.error instanceof AxiosError ? manage.error.response?.status : undefined

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (!disabled && !exclusionsTooLarge && name.trim()) create.mutate({ name: name.trim(), exclusions })
  }

  return (
    <section className="rounded-xl border border-panel-border bg-panel-card p-5" aria-labelledby="server-backup-heading">
      <h3 id="server-backup-heading" className="font-semibold">Backups</h3>
      {canRequestBackup && <form className="mt-4 space-y-2" onSubmit={submit}>
        <label htmlFor="server-backup-name" className="block text-sm font-medium">Nome do backup</label>
        <div className="flex flex-wrap gap-2">
          <input id="server-backup-name" className="min-w-0 flex-1 rounded-lg border border-panel-border bg-panel-dark px-3 py-2"
            value={name} onChange={(event) => setName(event.target.value)} maxLength={100} disabled={disabled} required />
          <button className="button button-primary" disabled={disabled || exclusionsTooLarge || !name.trim()}>
            {create.isPending ? 'Enfileirando…' : 'Criar backup'}
          </button>
        </div>
        <details className="rounded-lg border border-panel-border bg-panel-dark p-3 text-sm">
          <summary className="cursor-pointer font-medium">Exclusões do backup</summary>
          <label htmlFor="backup-exclusions" className="mb-1 mt-3 block">Uma regra por linha</label>
          <textarea id="backup-exclusions" rows={4} maxLength={32768} value={exclusions} disabled={disabled}
            className="w-full rounded-lg border border-panel-border bg-panel-card px-3 py-2 font-mono"
            placeholder={'logs/\n*.tmp'} onChange={(event) => { create.reset(); setExclusions(event.target.value) }} />
          <p className="mt-1 text-gray-400">Use a sintaxe gitignore. Se o campo ficar vazio, o node usa a .pteroignore do servidor.</p>
          {exclusionsTooLarge && <p className="mt-1 text-red-300" role="alert">As regras excedem 32 KiB.</p>}
        </details>
      </form>}
      {canRequestBackup && preview && <p className="mt-2 text-sm text-gray-400">A criação fica disponível no painel conectado.</p>}
      {canRequestBackup && !preview && !canCreate && <p className="mt-2 text-sm text-gray-400">Pare o servidor para criar um backup.</p>}
      {canRequestBackup && atLimit && <p className="mt-2 text-sm text-gray-400">Este servidor atingiu o limite de 20 backups.</p>}
      {!canList && create.isSuccess && <p role="status" className="mt-2 text-sm text-gray-300">Backup solicitado.</p>}
      {create.isError && <p className="mt-2 text-sm text-red-400" role="alert">{createError}</p>}
      {download.isError && <p className="mt-2 text-sm text-red-400" role="alert">Não foi possível iniciar o download.</p>}
      {manage.isError && <p className="mt-2 text-sm text-red-400" role="alert">
        {manageStatus === 409 ? 'Este backup está protegido ou em uso.'
          : manageStatus === 503 ? 'O node de backup está indisponível.' : 'Não foi possível alterar o backup.'}
      </p>}
      {restore.isError && <p className="mt-2 text-sm text-red-400" role="alert">
        {restoreStatus === 409 ? 'Confira se há outra restauração na fila.'
          : restoreStatus === 503 ? 'O node ainda não está configurado para preparar restaurações.'
            : 'Não foi possível solicitar a preparação da restauração.'}
      </p>}
      {canList && <div className="mt-4 border-t border-panel-border pt-4">
        {backups.isPending && <p className="text-sm text-gray-400" role="status">Carregando backups…</p>}
        {backups.isError && <p className="text-sm text-red-400" role="alert">Não foi possível carregar os backups.</p>}
        {backups.data?.length === 0 && <p className="text-sm text-gray-400">Nenhum backup registrado.</p>}
        {backups.data && backups.data.length > 0 && (
          <ul className="divide-y divide-panel-border" aria-label="Histórico de backups">
            {backups.data.map((backup) => (
              <li key={backup.id} className="space-y-1 py-3 first:pt-0 last:pb-0">
                <div className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
                  <span className="break-all font-medium">{backup.name}</span>
                  <span className="text-sm text-gray-300" role={backup.isQueued || backup.deletionStatus !== 'None' ? 'status' : undefined}>
                    {backup.deletionStatus === 'Queued' ? 'Exclusão na fila' : backup.deletionStatus === 'Failed' ? 'Falha na exclusão'
                      : backup.isQueued ? 'Na fila' : backup.isSuccessful ? 'Concluído' : 'Falhou'}
                  </span>
                </div>
                <div className="flex flex-wrap gap-x-4 text-sm text-gray-400">
                  <time dateTime={backup.completedAt ?? backup.createdAt}>
                    {dateFormat.format(new Date(backup.completedAt ?? backup.createdAt))}
                  </time>
                  {backup.isSuccessful && <span>{sizeFormat.format(backup.size)}</span>}
                  {backup.isLocked && <span>Protegido</span>}
                </div>
                {backup.checksum && <p className="break-all text-xs text-gray-400">SHA-256 <code>{backup.checksum}</code></p>}
                {(canDownload || (canRestore && backup.restoreAvailable)) && backup.isSuccessful && !backup.isQueued && backup.deletionStatus === 'None' && backup.checksum && backup.size > 0 && (
                  <div className="flex flex-wrap gap-2">
                    {canDownload && <button type="button" className="button button-secondary" aria-label={`Baixar ${backup.name}`}
                      disabled={preview || download.isPending} onClick={() => download.mutate(backup.id)}>
                      Baixar backup
                    </button>}
                    {canRestore && backup.restoreAvailable && (backup.restoreStatus === 'None' || backup.restoreStatus === 'Failed') && (
                      <button type="button" className="button button-secondary" aria-label={`Preparar restauração de ${backup.name}`}
                        disabled={preview || restore.isPending || restoreQueued} onClick={() => restore.mutate(backup.id)}>
                        {backup.restoreStatus === 'Failed' ? 'Tentar preparar novamente' : 'Preparar restauração'}
                      </button>
                    )}
                  </div>
                )}
                {canManage && !backup.isQueued && backup.deletionStatus !== 'Queued' && (
                  <div className="flex flex-wrap gap-2">
                    {backup.deletionStatus === 'None' && (
                      <button type="button" className="button button-secondary" disabled={preview || manage.isPending}
                        aria-label={`${backup.isLocked ? 'Remover proteção de' : 'Proteger'} ${backup.name}`}
                        onClick={() => manage.mutate({ id: backup.id, type: 'protection', locked: !backup.isLocked })}>
                        {backup.isLocked ? 'Remover proteção' : 'Proteger'}
                      </button>
                    )}
                    <button type="button" className="button button-secondary" disabled={preview || manage.isPending || !backup.canDelete}
                      aria-label={`${backup.deletionStatus === 'Failed' ? 'Repetir exclusão de' : 'Excluir'} ${backup.name}`}
                      onClick={() => { manage.reset(); setSelectedForDeletion(backup) }}>
                      {backup.deletionStatus === 'Failed' ? 'Tentar excluir novamente' : 'Excluir'}
                    </button>
                  </div>
                )}
                {canManage && !backup.canDelete && !backup.isLocked && !backup.isQueued && backup.deletionStatus !== 'Queued' && (
                  <p className="text-sm text-gray-400">Em uso por um agendamento ou por uma restauração.</p>
                )}
                {backup.restoreStatus === 'Queued' && <p className="text-sm text-gray-400" role="status">Restauração na fila</p>}
                {backup.restoreStatus === 'Failed' && <p className="text-sm text-red-400" role="status">Falha ao preparar a cópia para restauração.</p>}
                {backup.restoreStatus === 'Prepared' && (
                  <div className="text-sm text-gray-300" role="status">
                    <p>Cópia preparada no node. A mudança do volume é feita pelo operador.</p>
                    <p>Arquivos {backup.restoredFiles} · Pastas {backup.restoredDirectories} · {sizeFormat.format(backup.restoredBytes ?? 0)}</p>
                  </div>
                )}
              </li>
            ))}
          </ul>
        )}
      </div>}
      <ConfirmDialog open={canManage && selectedForDeletion !== null} pending={manage.isPending} title="Excluir backup?"
        description={`O backup ${selectedForDeletion?.name ?? ''} será removido permanentemente do node.`}
        confirmLabel="Excluir backup" pendingLabel="Solicitando exclusão…" onCancel={() => setSelectedForDeletion(null)}
        onConfirm={() => { if (canManage && selectedForDeletion && !preview) manage.mutate({ id: selectedForDeletion.id, type: 'delete' }) }} />
    </section>
  )
}
