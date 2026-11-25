import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { PlusIcon } from '@heroicons/react/24/outline'
import { serverService } from '@/services/api'
import { useAuthStore } from '@/stores/authStore'
import { usePageTitle } from '@/lib/usePageTitle'
import ServerBrowser from '@/components/ServerBrowser'

export default function Servers() {
  usePageTitle('Servidores')
  const user = useAuthStore((state) => state.user)
  const query = useQuery({
    queryKey: ['servers'],
    queryFn: serverService.getAll,
    refetchInterval: 15000,
  })
  return (
    <div>
      <div className="page-heading">
        <div>
          <div className="page-eyebrow">Seu workspace</div>
          <h1>Servidores</h1>
          <p>Encontre seu servidor e continue de onde parou.</p>
        </div>
        {(user?.role === 'Admin' || user?.role === 'SuperAdmin') && (
          <Link className="button button-primary" to="/admin/deploy">
            <PlusIcon />
            Criar servidor
          </Link>
        )}
      </div>
      <ServerBrowser
        servers={query.data ?? []}
        loading={query.isLoading}
        error={query.isError}
        refreshing={query.isFetching}
        onRefresh={() => {
          void query.refetch()
        }}
      />
    </div>
  )
}
