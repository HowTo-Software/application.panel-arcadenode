import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { useNavigate } from 'react-router-dom'
import { useForm } from 'react-hook-form'
import toast from 'react-hot-toast'
import { authService } from '@/services/api'
import { useAuthStore } from '@/stores/authStore'
import { getErrorMessage } from '@/lib/errorHandler'
import { usePageTitle } from '@/lib/usePageTitle'

interface ProfileForm {
  firstName: string
  lastName: string
  email: string
}

interface PasswordForm {
  currentPassword: string
  newPassword: string
  confirmPassword: string
}

export default function Settings() {
  usePageTitle('Minha conta')
  const { user, updateUser, logout } = useAuthStore()
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const [isProfileLoading, setIsProfileLoading] = useState(false)
  const [isPasswordLoading, setIsPasswordLoading] = useState(false)

  const profileForm = useForm<ProfileForm>({
    defaultValues: {
      firstName: user?.firstName || '',
      lastName: user?.lastName || '',
      email: user?.email || '',
    },
  })

  const passwordForm = useForm<PasswordForm>()

  const onProfileSubmit = async (data: ProfileForm) => {
    setIsProfileLoading(true)
    try {
      await authService.updateProfile(data.firstName, data.lastName, data.email)
      updateUser(data)
      toast.success('Perfil atualizado')
    } catch (error) {
      toast.error(getErrorMessage(error, 'Não foi possível atualizar o perfil'))
    } finally {
      setIsProfileLoading(false)
    }
  }

  const onPasswordSubmit = async (data: PasswordForm) => {
    if (data.newPassword !== data.confirmPassword) {
      toast.error('As senhas não coincidem')
      return
    }

    setIsPasswordLoading(true)
    try {
      await authService.changePassword(data.currentPassword, data.newPassword)
      passwordForm.reset()
      await queryClient.cancelQueries()
      queryClient.clear()
      logout()
      navigate('/login', { replace: true })
      toast.success('Senha alterada. Entre novamente.')
    } catch (error) {
      toast.error(getErrorMessage(error, 'Não foi possível alterar a senha'))
    } finally {
      setIsPasswordLoading(false)
    }
  }

  return (
    <div className="account-page max-w-3xl">
      <div className="page-heading">
        <div>
          <h1>Minha conta</h1>
          <p>Atualize seus dados e mantenha seu acesso protegido.</p>
        </div>
      </div>

      {/* Profile Settings */}
      <div className="bg-panel-card border border-panel-border rounded-xl p-6 mb-6">
        <h2 className="text-lg font-semibold text-white mb-4">
          Informações pessoais
        </h2>
        {user && <div className="mb-4 flex flex-wrap items-center gap-2 text-sm text-gray-300">
          <span>ID da conta</span><code className="break-all">{user.id}</code>
          <button className="button button-secondary" type="button" onClick={() => {
            void navigator.clipboard.writeText(user.id).then(() => toast.success('ID copiado'))
              .catch(() => toast.error('Não foi possível copiar o ID'))
          }}>Copiar ID</button>
        </div>}
        <form
          onSubmit={profileForm.handleSubmit(onProfileSubmit)}
          className="space-y-4"
        >
          <div className="grid grid-cols-2 gap-4">
            <div>
              <label className="block text-sm font-medium text-gray-300 mb-1">
                Nome
              </label>
              <input
                type="text"
                aria-label="Nome"
                {...profileForm.register('firstName')}
                className="w-full px-4 py-2 bg-panel-dark border border-panel-border rounded-lg text-white focus:outline-none focus:ring-2 focus:ring-primary-500"
              />
            </div>
            <div>
              <label className="block text-sm font-medium text-gray-300 mb-1">
                Sobrenome
              </label>
              <input
                type="text"
                aria-label="Sobrenome"
                {...profileForm.register('lastName')}
                className="w-full px-4 py-2 bg-panel-dark border border-panel-border rounded-lg text-white focus:outline-none focus:ring-2 focus:ring-primary-500"
              />
            </div>
          </div>

          <div>
            <label className="block text-sm font-medium text-gray-300 mb-1">
              Email
            </label>
            <input
              type="email"
              aria-label="Email"
              {...profileForm.register('email', { required: true })}
              className="w-full px-4 py-2 bg-panel-dark border border-panel-border rounded-lg text-white focus:outline-none focus:ring-2 focus:ring-primary-500"
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-gray-300 mb-1">
              Usuário
            </label>
            <input
              type="text"
              aria-label="Usuário"
              value={user?.username}
              disabled
              className="w-full px-4 py-2 bg-panel-dark border border-panel-border rounded-lg text-gray-400 cursor-not-allowed"
            />
            <p className="mt-1 text-xs text-gray-500">
              O nome de usuário não pode ser alterado.
            </p>
          </div>

          <button
            type="submit"
            disabled={isProfileLoading}
            className="px-4 py-2 bg-primary-600 hover:bg-primary-700 disabled:bg-primary-800 text-white font-medium rounded-lg transition-colors"
          >
            {isProfileLoading ? 'Salvando...' : 'Salvar alterações'}
          </button>
        </form>
      </div>

      {/* Password Settings */}
      <div className="bg-panel-card border border-panel-border rounded-xl p-6">
        <h2 className="text-lg font-semibold text-white mb-4">Alterar senha</h2>
        <form
          onSubmit={passwordForm.handleSubmit(onPasswordSubmit)}
          className="space-y-4"
        >
          <div>
            <label className="block text-sm font-medium text-gray-300 mb-1">
              Senha atual
            </label>
            <input
              type="password"
              aria-label="Senha atual"
              {...passwordForm.register('currentPassword', { required: true })}
              className="w-full px-4 py-2 bg-panel-dark border border-panel-border rounded-lg text-white focus:outline-none focus:ring-2 focus:ring-primary-500"
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-gray-300 mb-1">
              Nova senha
            </label>
            <input
              type="password"
              aria-label="Nova senha"
              {...passwordForm.register('newPassword', {
                required: true,
                minLength: 8,
              })}
              className="w-full px-4 py-2 bg-panel-dark border border-panel-border rounded-lg text-white focus:outline-none focus:ring-2 focus:ring-primary-500"
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-gray-300 mb-1">
              Confirmar nova senha
            </label>
            <input
              type="password"
              aria-label="Confirmar nova senha"
              {...passwordForm.register('confirmPassword', { required: true })}
              className="w-full px-4 py-2 bg-panel-dark border border-panel-border rounded-lg text-white focus:outline-none focus:ring-2 focus:ring-primary-500"
            />
          </div>

          <button
            type="submit"
            disabled={isPasswordLoading}
            className="px-4 py-2 bg-primary-600 hover:bg-primary-700 disabled:bg-primary-800 text-white font-medium rounded-lg transition-colors"
          >
            {isPasswordLoading ? 'Changing...' : 'Alterar senha'}
          </button>
        </form>
      </div>
    </div>
  )
}
