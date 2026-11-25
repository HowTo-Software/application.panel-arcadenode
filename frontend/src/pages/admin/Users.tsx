import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import toast from 'react-hot-toast'
import { adminService } from '@/services/api'
import { getErrorMessage } from '@/lib/errorHandler'
import type { InviteCode } from '@/types'
import {
  UsersIcon,
  TrashIcon,
  TicketIcon,
  ClipboardDocumentIcon,
  PlusIcon,
  ArrowPathIcon,
} from '@heroicons/react/24/outline'
import clsx from 'clsx'
import { formatDistanceToNow, format } from 'date-fns'
import { usePageTitle } from '@/lib/usePageTitle'

export default function Users() {
  usePageTitle('Usuários e convites')
  const queryClient = useQueryClient()
  const [inviteMaxUses, setInviteMaxUses] = useState(1)
  const [inviteExpiry, setInviteExpiry] = useState('')
  const [showDeleteConfirm, setShowDeleteConfirm] = useState<string | null>(
    null
  )

  const { data: users, isLoading } = useQuery({
    queryKey: ['users'],
    queryFn: adminService.getAllUsers,
  })

  const { data: inviteCodes, isLoading: isLoadingInvites } = useQuery({
    queryKey: ['inviteCodes'],
    queryFn: adminService.getInviteCodes,
  })

  const deleteMutation = useMutation({
    mutationFn: adminService.deleteUser,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['users'] })
      toast.success('Usuário excluído')
    },
    onError: (error: unknown) => {
      toast.error(getErrorMessage(error, 'Não foi possível excluir o usuário'))
    },
  })

  const createInviteMutation = useMutation({
    mutationFn: () =>
      adminService.createInviteCode(
        inviteMaxUses,
        inviteExpiry ? new Date(inviteExpiry).toISOString() : undefined
      ),
    onSuccess: (data: InviteCode) => {
      queryClient.invalidateQueries({ queryKey: ['inviteCodes'] })
      toast.success(`Invite code created: ${data.code}`)
      setInviteMaxUses(1)
      setInviteExpiry('')
    },
    onError: (error: unknown) => {
      toast.error(getErrorMessage(error, 'Não foi possível criar o convite'))
    },
  })

  const revokeInviteMutation = useMutation({
    mutationFn: adminService.revokeInviteCode,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['inviteCodes'] })
      toast.success('Convite revogado')
    },
    onError: (error: unknown) => {
      toast.error(getErrorMessage(error, 'Não foi possível revogar o convite'))
    },
  })

  const copyToClipboard = (code: string) => {
    navigator.clipboard.writeText(code)
    toast.success('Código copiado')
  }

  const getRoleBadgeColor = (role: string) => {
    switch (role) {
      case 'SuperAdmin':
        return 'bg-red-500/20 text-red-400'
      case 'Admin':
        return 'bg-orange-500/20 text-orange-400'
      case 'SubUser':
        return 'bg-blue-500/20 text-blue-400'
      default:
        return 'bg-gray-500/20 text-gray-400'
    }
  }

  if (isLoading) {
    return (
      <div className="flex items-center justify-center min-h-[400px]">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-primary-500" />
      </div>
    )
  }

  return (
    <div>
      <div className="page-heading">
        <div>
          <h1>Usuários e convites</h1>
          <p>Controle o acesso ao seu workspace e convide sua equipe.</p>
        </div>
        <span className="feature-label">
          {users?.length ?? 0} usuários cadastrados
        </span>
      </div>

      {users && users.length > 0 ? (
        <div className="bg-panel-card border border-panel-border rounded-xl overflow-x-auto">
          <table
            className="w-full min-w-[650px]"
            aria-label="Usuários cadastrados"
          >
            <thead>
              <tr className="border-b border-panel-border">
                <th className="text-left px-6 py-4 text-sm font-medium text-gray-400">
                  Usuário
                </th>
                <th className="text-left px-6 py-4 text-sm font-medium text-gray-400">
                  Permissão
                </th>
                <th className="text-left px-6 py-4 text-sm font-medium text-gray-400">
                  Status
                </th>
                <th className="text-left px-6 py-4 text-sm font-medium text-gray-400">
                  Último acesso
                </th>
                <th className="text-right px-6 py-4 text-sm font-medium text-gray-400">
                  Ações
                </th>
              </tr>
            </thead>
            <tbody className="divide-y divide-panel-border">
              {users.map((user) => (
                <tr key={user.id} className="hover:bg-panel-dark/50">
                  <td className="px-6 py-4">
                    <div>
                      <p className="font-medium text-white">{user.username}</p>
                      <p className="text-sm text-gray-400">{user.email}</p>
                    </div>
                  </td>
                  <td className="px-6 py-4">
                    <span
                      className={clsx(
                        'inline-flex px-2 py-1 text-xs font-medium rounded-full',
                        getRoleBadgeColor(user.role)
                      )}
                    >
                      {user.role}
                    </span>
                  </td>
                  <td className="px-6 py-4">
                    <span
                      className={clsx(
                        'inline-flex items-center gap-1.5 text-sm',
                        user.isActive ? 'text-green-400' : 'text-red-400'
                      )}
                    >
                      <span
                        className={clsx(
                          'w-2 h-2 rounded-full',
                          user.isActive ? 'bg-green-400' : 'bg-red-400'
                        )}
                      />
                      {user.isActive ? 'Ativo' : 'Inativo'}
                    </span>
                  </td>
                  <td className="px-6 py-4 text-sm text-gray-400">
                    {user.lastLoginAt
                      ? formatDistanceToNow(new Date(user.lastLoginAt), {
                          addSuffix: true,
                        })
                      : 'Nunca'}
                  </td>
                  <td className="px-6 py-4 text-right">
                    <button
                      onClick={() => setShowDeleteConfirm(user.id)}
                      disabled={user.role === 'SuperAdmin'}
                      className="p-2 text-gray-400 hover:text-red-400 hover:bg-red-500/10 rounded-lg transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
                      title={
                        user.role === 'SuperAdmin'
                          ? 'Não é possível excluir um superadministrador'
                          : 'Excluir usuário'
                      }
                    >
                      <TrashIcon className="h-5 w-5" />
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        <div className="bg-panel-card border border-panel-border rounded-xl p-12 text-center">
          <UsersIcon className="h-16 w-16 text-gray-600 mx-auto mb-4" />
          <h3 className="text-lg font-medium text-white mb-2">
            Nenhum usuário encontrado
          </h3>
          <p className="text-gray-400">
            Os usuários aparecerão aqui após o cadastro por convite.
          </p>
        </div>
      )}

      {/* Delete Confirmation Modal */}
      {showDeleteConfirm && (
        <div className="fixed inset-0 bg-black/70 flex items-center justify-center z-50 backdrop-blur-sm">
          <div className="bg-panel-card border border-panel-border rounded-xl p-6 max-w-md mx-4 shadow-2xl">
            <h3 className="text-xl font-semibold text-white mb-2">
              Excluir usuário
            </h3>
            <p className="text-gray-400 mb-4">
              Deseja excluir este usuário? Esta ação é permanente e não pode ser
              desfeita.
            </p>
            <div className="flex justify-end gap-3">
              <button
                onClick={() => setShowDeleteConfirm(null)}
                className="px-4 py-2 bg-gray-700 hover:bg-gray-600 text-white font-medium rounded-lg transition-colors"
              >
                Cancelar
              </button>
              <button
                onClick={() => {
                  deleteMutation.mutate(showDeleteConfirm)
                  setShowDeleteConfirm(null)
                }}
                disabled={deleteMutation.isPending}
                className="flex items-center gap-2 px-4 py-2 bg-red-600 hover:bg-red-500 disabled:bg-red-800 text-white font-medium rounded-lg transition-colors"
              >
                {deleteMutation.isPending ? (
                  <>
                    <ArrowPathIcon className="h-4 w-4 animate-spin" />
                    Excluindo...
                  </>
                ) : (
                  <>
                    <TrashIcon className="h-4 w-4" />
                    Excluir usuário
                  </>
                )}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Invite Codes Section */}
      <div className="mt-10">
        <div className="flex items-center justify-between mb-6">
          <h2 className="text-xl font-bold text-white flex items-center gap-2">
            <TicketIcon className="h-6 w-6 text-primary-400" />
            Convites de acesso
          </h2>
          <p className="text-gray-400">
            {inviteCodes?.filter((i) => i.isValid).length ?? 0} convites ativos
          </p>
        </div>

        {/* Create invite form */}
        <div className="bg-panel-card border border-panel-border rounded-xl p-4 mb-4">
          <div className="flex items-end gap-4 flex-wrap">
            <div>
              <label className="block text-sm font-medium text-gray-300 mb-1">
                Limite de usos
              </label>
              <input
                type="number"
                min={1}
                max={100}
                aria-label="Limite de usos do convite"
                value={inviteMaxUses}
                onChange={(e) =>
                  setInviteMaxUses(parseInt(e.target.value) || 0)
                }
                className="w-24 px-3 py-2 bg-panel-dark border border-panel-border rounded-lg text-white text-sm focus:outline-none focus:ring-2 focus:ring-primary-500"
              />
              <p className="text-xs text-gray-500 mt-0.5">De 1 a 100 usos</p>
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-300 mb-1">
                Validade (opcional)
              </label>
              <input
                type="datetime-local"
                aria-label="Validade do convite"
                value={inviteExpiry}
                onChange={(e) => setInviteExpiry(e.target.value)}
                className="px-3 py-2 bg-panel-dark border border-panel-border rounded-lg text-white text-sm focus:outline-none focus:ring-2 focus:ring-primary-500"
              />
            </div>
            <button
              onClick={() => createInviteMutation.mutate()}
              disabled={createInviteMutation.isPending}
              className="flex items-center gap-1.5 px-4 py-2 bg-primary-600 hover:bg-primary-700 disabled:bg-primary-800 disabled:cursor-not-allowed text-white text-sm font-medium rounded-lg transition-colors"
            >
              <PlusIcon className="h-4 w-4" />
              {createInviteMutation.isPending ? 'Criando...' : 'Gerar convite'}
            </button>
          </div>
        </div>

        {/* Invite codes list */}
        {isLoadingInvites ? (
          <div className="flex items-center justify-center min-h-[100px]">
            <div className="animate-spin rounded-full h-6 w-6 border-b-2 border-primary-500" />
          </div>
        ) : inviteCodes && inviteCodes.length > 0 ? (
          <div className="bg-panel-card border border-panel-border rounded-xl overflow-x-auto">
            <table
              className="w-full min-w-[650px]"
              aria-label="Convites de acesso"
            >
              <thead>
                <tr className="border-b border-panel-border">
                  <th className="text-left px-6 py-3 text-sm font-medium text-gray-400">
                    Código
                  </th>
                  <th className="text-left px-6 py-3 text-sm font-medium text-gray-400">
                    Utilizações
                  </th>
                  <th className="text-left px-6 py-3 text-sm font-medium text-gray-400">
                    Status
                  </th>
                  <th className="text-left px-6 py-3 text-sm font-medium text-gray-400">
                    Validade
                  </th>
                  <th className="text-left px-6 py-3 text-sm font-medium text-gray-400">
                    Criado em
                  </th>
                  <th className="text-right px-6 py-3 text-sm font-medium text-gray-400">
                    Ações
                  </th>
                </tr>
              </thead>
              <tbody className="divide-y divide-panel-border">
                {inviteCodes.map((invite) => (
                  <tr key={invite.id} className="hover:bg-panel-dark/50">
                    <td className="px-6 py-3">
                      <code className="font-mono text-sm text-white tracking-widest">
                        {invite.code}
                      </code>
                    </td>
                    <td className="px-6 py-3 text-sm text-gray-400">
                      {invite.timesUsed} /{' '}
                      {invite.maxUses === 0 ? '∞' : invite.maxUses}
                    </td>
                    <td className="px-6 py-3">
                      <span
                        className={clsx(
                          'inline-flex items-center gap-1.5 text-xs font-medium px-2 py-0.5 rounded-full',
                          invite.isValid
                            ? 'bg-green-500/20 text-green-400'
                            : invite.isRevoked
                              ? 'bg-red-500/20 text-red-400'
                              : 'bg-gray-500/20 text-gray-400'
                        )}
                      >
                        <span
                          className={clsx(
                            'w-1.5 h-1.5 rounded-full',
                            invite.isValid
                              ? 'bg-green-400'
                              : invite.isRevoked
                                ? 'bg-red-400'
                                : 'bg-gray-400'
                          )}
                        />
                        {invite.isRevoked
                          ? 'Revogado'
                          : invite.isValid
                            ? 'Ativo'
                            : 'Expirado'}
                      </span>
                    </td>
                    <td className="px-6 py-3 text-sm text-gray-400">
                      {invite.expiresAt
                        ? format(
                            new Date(invite.expiresAt),
                            'MMM d, yyyy HH:mm'
                          )
                        : 'Nunca'}
                    </td>
                    <td className="px-6 py-3 text-sm text-gray-400">
                      <div>
                        <p>
                          {formatDistanceToNow(new Date(invite.createdAt), {
                            addSuffix: true,
                          })}
                        </p>
                        <p className="text-xs text-gray-500">
                          por {invite.createdByUsername}
                        </p>
                      </div>
                    </td>
                    <td className="px-6 py-3 text-right">
                      <div className="flex items-center justify-end gap-1">
                        <button
                          onClick={() => copyToClipboard(invite.code)}
                          className="p-1.5 text-gray-400 hover:text-white hover:bg-panel-dark rounded-lg transition-colors"
                          title="Copiar c?digo"
                        >
                          <ClipboardDocumentIcon className="h-4 w-4" />
                        </button>
                        {invite.isValid && (
                          <button
                            onClick={() =>
                              revokeInviteMutation.mutate(invite.id)
                            }
                            disabled={revokeInviteMutation.isPending}
                            className="p-1.5 text-gray-400 hover:text-red-400 hover:bg-red-500/10 rounded-lg transition-colors"
                            title="Revogar convite"
                          >
                            <TrashIcon className="h-4 w-4" />
                          </button>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        ) : (
          <div className="bg-panel-card border border-panel-border rounded-xl p-8 text-center">
            <TicketIcon className="h-12 w-12 text-gray-600 mx-auto mb-3" />
            <h3 className="text-lg font-medium text-white mb-1">
              Nenhum convite criado
            </h3>
            <p className="text-gray-400 text-sm">
              Gere um convite acima para permitir novos cadastros.
            </p>
          </div>
        )}
      </div>
    </div>
  )
}
