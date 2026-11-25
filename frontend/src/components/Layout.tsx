import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import toast from 'react-hot-toast'
import { authService } from '@/services/api'
import {
  Link,
  NavLink,
  Outlet,
  useLocation,
  useNavigate,
} from 'react-router-dom'
import {
  Squares2X2Icon,
  ServerStackIcon,
  AdjustmentsHorizontalIcon,
  ArrowRightOnRectangleIcon,
  ChevronDownIcon,
  CommandLineIcon,
  Bars3Icon,
  XMarkIcon,
  Cog6ToothIcon,
  DocumentTextIcon,
  ArrowsRightLeftIcon,
  CubeIcon,
  UsersIcon,
  PlusIcon,
} from '@heroicons/react/24/outline'
import { useAuthStore } from '@/stores/authStore'
import { ArcadeNodeIcon } from '@/components/ArcadeNodeIcon'

export default function Layout() {
  const { user, logout } = useAuthStore()
  const navigate = useNavigate()
  const { pathname } = useLocation()
  const [menuOpen, setMenuOpen] = useState(false)
  const [isLoggingOut, setIsLoggingOut] = useState(false)
  const queryClient = useQueryClient()
  const isAdmin = user?.role === 'Admin' || user?.role === 'SuperAdmin'
  const isDemo =
    import.meta.env.DEV && import.meta.env.VITE_DEMO_MODE === 'true'
  const adminPage = pathname.startsWith('/admin')
  const links = [
    { to: '/', label: 'Visão geral', icon: Squares2X2Icon, end: true },
    { to: '/servers', label: 'Servidores', icon: ServerStackIcon, end: false },
    ...(isAdmin
      ? [
          {
            to: '/admin/nodes',
            label: 'Administração',
            icon: AdjustmentsHorizontalIcon,
            end: false,
          },
        ]
      : []),
  ]
  return (
    <div className="app-shell">
      <a className="skip-link" href="#main-content">
        Ir para o conteúdo
      </a>
      <header className="app-header">
        <div className="header-inner">
          <Link to="/" className="brand" aria-label="ArcadeNode — início">
            <span className="brand-mark">
              <ArcadeNodeIcon className="h-6 w-6" />
            </span>
            <span>
              Arcade<span className="brand-light">Node</span>
              <small>by HowToo Software</small>
            </span>
          </Link>
          <span className="header-divider" />
          <nav className="primary-nav" aria-label="Navegação principal">
            {links.map(({ to, label, icon: Icon, end }) => (
              <NavLink
                key={to}
                to={to}
                end={end}
                className={({ isActive }) =>
                  `top-link ${isActive || (label === 'Administração' && adminPage) ? 'active' : ''}`
                }
              >
                <Icon />
                {label}
              </NavLink>
            ))}
          </nav>
          <div className="header-actions">
            {isDemo && (
              <span className="preview-chip">
                <span />
                Prévia
              </span>
            )}
            <Link
              to="/settings"
              className="account-link"
              aria-label="Configurações da conta"
            >
              <span className="avatar">
                {(user?.firstName || user?.username || 'A')
                  .slice(0, 1)
                  .toUpperCase()}
              </span>
              <span className="account-name">
                {user?.firstName || user?.username}
                <ChevronDownIcon />
              </span>
            </Link>
            <button
              className="icon-button logout-button"
              aria-label="Sair da conta"
              title="Sair da conta"
              disabled={isLoggingOut}
              onClick={async () => {
                setIsLoggingOut(true)
                try {
                  if (!isDemo) await authService.logout()
                } catch {
                  toast.error('Não foi possível encerrar a sessão. Tente novamente.')
                  setIsLoggingOut(false)
                  return
                }
                await queryClient.cancelQueries()
                queryClient.clear()
                logout()
                navigate('/login')
              }}
            >
              <ArrowRightOnRectangleIcon />
            </button>
            <button
              className="icon-button mobile-menu-toggle"
              aria-label={menuOpen ? 'Fechar navegação' : 'Abrir navegação'}
              aria-expanded={menuOpen}
              aria-controls="mobile-navigation"
              onClick={() => setMenuOpen(!menuOpen)}
            >
              {menuOpen ? <XMarkIcon /> : <Bars3Icon />}
            </button>
          </div>
        </div>
      </header>
      {menuOpen && (
        <nav
          id="mobile-navigation"
          className="mobile-navigation"
          aria-label="Navegação móvel"
        >
          {links.map(({ to, label, icon: Icon }) => (
            <Link to={to} key={to} onClick={() => setMenuOpen(false)}>
              <Icon />
              {label}
            </Link>
          ))}
          <Link to="/settings" onClick={() => setMenuOpen(false)}>
            <Cog6ToothIcon />
            Minha conta
          </Link>
        </nav>
      )}
      <div className="workspace-bar">
        <div className="workspace-inner">
          <span className="workspace-label">
            <CommandLineIcon />
            Workspace <span>/</span>{' '}
            <strong>
              {adminPage
                ? 'Administração'
                : pathname.startsWith('/servers/')
                  ? 'Gerenciar servidor'
                  : pathname === '/settings'
                    ? 'Minha conta'
                    : 'Painel de controle'}
            </strong>
          </span>
          <span className="workspace-note">
            {isDemo
              ? 'Ambiente de demonstração'
              : 'Gerenciamento de servidores de jogos'}
          </span>
        </div>
      </div>
      {adminPage && (
        <nav className="admin-subnav content-width" aria-label="Administração">
          <NavLink to="/admin/nodes">
            <CubeIcon />
            Nós de hospedagem
          </NavLink>
          <NavLink to="/admin/users">
            <UsersIcon />
            Usuários e convites
          </NavLink>
          <NavLink to="/admin/eggs">
            <DocumentTextIcon />
            Eggs e categorias
          </NavLink>
          <NavLink to="/admin/migrations">
            <ArrowsRightLeftIcon />
            Migrações
          </NavLink>
          <NavLink to="/admin/deploy">
            <PlusIcon />
            Criar servidor
          </NavLink>
        </nav>
      )}
      <main
        id="main-content"
        className={`page-main content-width ${adminPage ? 'admin-page' : ''}`}
      >
        <Outlet />
      </main>
      <footer className="app-footer content-width">
        <span>
          <ArcadeNodeIcon className="h-4 w-4" />
          ArcadeNode <span className="footer-separator">/</span> HowToo Software
        </span>
        <span>
          {isDemo
            ? 'Prévia com dados fictícios. Operações reais desativadas.'
            : 'Seu próximo mundo começa aqui.'}
        </span>
      </footer>
    </div>
  )
}
