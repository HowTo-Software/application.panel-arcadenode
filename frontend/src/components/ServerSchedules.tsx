import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AxiosError } from 'axios'
import api from '@/lib/httpClient'
import { getErrorMessage } from '@/lib/errorHandler'
import { ConfirmDialog } from '@/components/ui/ConfirmDialog'
import { hasServerPermission } from '@/lib/serverPermissions'

type Task = { action: 'Power' | 'Command' | 'Backup'; payload: string; timeOffset: number; continueOnFailure: boolean }
type Draft = { name: string; cronExpression: string; onlyWhenOnline: boolean; isActive: boolean; tasks: Task[] }
type Schedule = Draft & { id: string; revision: string; isProcessing: boolean; waitingForBackup: boolean; nextRunAt: string | null }
const powerOptions = [
  { value: 'restart', label: 'Reiniciar', permission: 'control.restart' },
  { value: 'start', label: 'Iniciar', permission: 'control.start' },
  { value: 'stop', label: 'Parar', permission: 'control.stop' },
  { value: 'kill', label: 'Forçar parada', permission: 'control.stop' },
]
const taskOptions = [{ value: 'Power', label: 'Energia' }, { value: 'Command', label: 'Comando de console' },
  { value: 'Backup', label: 'Backup do volume' }] as const
const field = 'w-full rounded-lg border border-panel-border bg-panel-dark px-3 py-2'
const preview = import.meta.env.DEV && import.meta.env.VITE_DEMO_MODE === 'true'

export function ServerSchedules({ serverId, permissions }: { serverId: string; permissions?: string[] | null }) {
  const can = (name: string) => hasServerPermission(permissions, name)
  const canRead = can('schedule.read')
  const powers = powerOptions.filter((option) => can(option.permission))
  const allowedActions = taskOptions.filter((option) => option.value === 'Power' ? powers.length > 0
    : can(option.value === 'Command' ? 'control.console' : 'backup.create'))
  const canCreate = can('schedule.create') && allowedActions.length > 0
  const canUpdate = can('schedule.update') && allowedActions.length > 0
  const defaultAction = allowedActions[0]?.value ?? 'Power'
  const emptyTask: Task = { action: defaultAction, payload: defaultAction === 'Power' ? (powers[0]?.value ?? '') : '', timeOffset: 0, continueOnFailure: false }
  const canRunTask = (task: Task) => allowedActions.some(({ value }) => value === task.action) &&
    (task.action !== 'Power' || powers.some(({ value }) => value === task.payload))
  const path = `/servers/${serverId}/schedules`
  const cache = useQueryClient()
  const queryKey = ['server-schedules', serverId]
  const [name, setName] = useState('')
  const [cronExpression, setCronExpression] = useState('0 4 * * *')
  const [onlyWhenOnline, setOnlyWhenOnline] = useState(false)
  const [isActive, setIsActive] = useState(true)
  const [tasks, setTasks] = useState<Task[]>([emptyTask])
  const [selected, setSelected] = useState<Schedule | null>(null)
  const [editing, setEditing] = useState<Schedule | null>(null)
  const schedules = useQuery({
    queryKey,
    enabled: canRead,
    queryFn: async ({ signal }) => (await api.get<Schedule[]>(path, { signal })).data,
    refetchInterval: (query) => preview ? false : query.state.data?.some((schedule) => schedule.isProcessing) ? 5000 : 15000,
  })
  const changes = useMutation({
    mutationFn: async (change: { type: 'create'; draft: Draft } | { type: 'edit'; schedule: Schedule; draft: Draft } |
      { type: 'toggle' | 'delete'; schedule: Schedule }) => {
      if (change.type === 'create') {
        await api.post(path, change.draft)
      } else if (change.type === 'edit') {
        await api.put(`${path}/${change.schedule.id}`, change.draft, { headers: { 'If-Match': `"${change.schedule.revision}"` } })
      } else if (change.type === 'toggle') {
        await api.put(`${path}/${change.schedule.id}/activity`, { isActive: !change.schedule.isActive })
      } else {
        await api.delete(`${path}/${change.schedule.id}`)
      }
    },
    onSuccess: (_, change) => {
      if (change.type === 'create') {
        setName('')
        setTasks([emptyTask])
      }
      if (change.type === 'edit') resetEdit()
      setSelected(null)
      return cache.invalidateQueries({ queryKey })
    },
    onError: (error, change) => {
      if (change.type === 'delete') setSelected(null)
      if (change.type === 'edit' && error instanceof AxiosError && error.response?.status === 412)
        return cache.invalidateQueries({ queryKey })
    },
  })
  const formError = changes.isError && (changes.variables?.type === 'create' || changes.variables?.type === 'edit')
  const staleEdit = changes.error instanceof AxiosError && changes.error.response?.status === 412

  function startEdit(schedule: Schedule) {
    changes.reset()
    setEditing(schedule)
    setName(schedule.name)
    setCronExpression(schedule.cronExpression)
    setOnlyWhenOnline(schedule.onlyWhenOnline)
    setIsActive(schedule.isActive)
    setTasks(schedule.tasks.map((task) => ({ ...task, payload: task.payload ?? '' })))
  }

  function resetEdit() {
    setEditing(null)
    setName('')
    setCronExpression('0 4 * * *')
    setOnlyWhenOnline(false)
    setIsActive(true)
    setTasks([emptyTask])
  }

  function updateTask(index: number, changes: Partial<Task>) {
    setTasks((current) => current.map((task, position) => position === index ? { ...task, ...changes } : task))
  }

  function moveTask(index: number, offset: -1 | 1) {
    setTasks((current) => {
      const next = [...current]
      const destination = index + offset
      if (destination < 0 || destination >= next.length) return current
      const moved = next[index]
      next[index] = next[destination]
      next[destination] = moved
      return next
    })
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    if (preview || changes.isPending || !tasks.every(canRunTask)) return
    const draft = { name, cronExpression, onlyWhenOnline, isActive, tasks }
    if (editing && canUpdate) changes.mutate({ type: 'edit', draft, schedule: editing })
    else if (!editing && canCreate) changes.mutate({ type: 'create', draft })
  }

  return (
    <div className="space-y-5">
      {canRead && <section className="rounded-xl border border-panel-border bg-panel-card p-5">
        <h3 className="font-semibold">Agendamentos</h3>
        {schedules.isPending && <p role="status">Carregando agendamentos…</p>}
        {schedules.isError && <p role="alert">Não foi possível carregar os agendamentos.</p>}
        {schedules.data?.length === 0 && <p className="mt-2 text-sm text-gray-400">Nenhum agendamento cadastrado.</p>}
        <ul className="mt-2 divide-y divide-panel-border">
          {schedules.data?.map((schedule) => (
            <li key={schedule.id} className="flex flex-wrap items-center justify-between gap-3 py-3">
              <div className="min-w-0 break-words">
                <h4 className="font-medium">{schedule.name}</h4>
                <p className="text-sm text-gray-400">{schedule.cronExpression} UTC · {schedule.tasks.length} tarefa(s)</p>
                <p className="text-sm text-gray-400">
                  {schedule.waitingForBackup ? 'Aguardando backup' : schedule.isProcessing ? 'Em execução' : schedule.isActive ? 'Ativo' : 'Pausado'}
                  {schedule.nextRunAt && ` · Próxima execução ${new Date(schedule.nextRunAt).toLocaleString('pt-BR', { timeZone: 'UTC' })} UTC`}
                </p>
              </div>
              <div className="flex flex-wrap gap-2">
                {canUpdate && <button className="button button-secondary" aria-label={`Editar ${schedule.name}`}
                  disabled={preview || schedule.isProcessing || schedule.waitingForBackup || changes.isPending || editing !== null}
                  onClick={() => startEdit(schedule)}>Editar</button>}
                {can('schedule.update') && <button className="button button-secondary"
                  disabled={preview || schedule.isProcessing || changes.isPending || editing?.id === schedule.id || (!schedule.isActive && !schedule.tasks.every(canRunTask))}
                  onClick={() => changes.mutate({ type: 'toggle', schedule })}>{schedule.isActive ? 'Pausar' : 'Reativar'}</button>}
                {can('schedule.delete') && <button className="button button-secondary" disabled={preview || schedule.isProcessing || changes.isPending || editing?.id === schedule.id}
                  onClick={() => { changes.reset(); setSelected(schedule) }}>Excluir</button>}
              </div>
              <details className="w-full text-sm">
                <summary className="cursor-pointer text-gray-300">Ver tarefas</summary>
                {schedule.onlyWhenOnline && <p className="my-2 text-gray-400">Executa apenas com o servidor online.</p>}
                <ol className="mt-2 list-decimal space-y-2 pl-6">
                  {schedule.tasks.map((task, index) => (
                    <li key={index}>
                      <p>{task.action === 'Power' ? powerOptions.find((option) => option.value === task.payload)?.label
                        : taskOptions.find((option) => option.value === task.action)?.label}</p>
                      {task.action !== 'Power' && task.payload &&
                        <p className="whitespace-pre-wrap break-all font-mono text-gray-400">{task.payload}</p>}
                      <p className="text-gray-400">Atraso de {task.timeOffset} s{task.continueOnFailure && ' · Continua em caso de falha'}</p>
                    </li>
                  ))}
                </ol>
              </details>
            </li>
          ))}
        </ul>
        {changes.isError && !formError &&
          <p role="alert" className="mt-2 text-sm text-red-400">{getErrorMessage(changes.error, 'Falha ao alterar agendamento')}</p>}
      </section>}

      {!canCreate && !canRead && <p className="text-sm text-gray-400">Seu acesso não inclui tarefas para agendar.</p>}
      {(editing ? canUpdate : canCreate) && <form key={editing?.id ?? 'new'} onSubmit={submit} className="space-y-4 rounded-xl border border-panel-border bg-panel-card p-5">
        <h3 className="font-semibold">{editing ? `Editar ${editing.name}` : 'Novo agendamento'}</h3>
        <fieldset disabled={preview || changes.isPending} className="space-y-4">
          <div className="grid gap-3 sm:grid-cols-2">
            <label className="text-sm">Nome<input className={field} required maxLength={100} autoFocus={editing !== null} value={name} onChange={(e) => setName(e.target.value)} /></label>
            <label className="text-sm">Cron em UTC
              <input className={field} required maxLength={100} value={cronExpression} onChange={(e) => setCronExpression(e.target.value)} />
              <span className="text-gray-400">Minuto, hora, dia do mês, mês e dia da semana.</span>
            </label>
          </div>
          <label className="block text-sm"><input type="checkbox" checked={onlyWhenOnline} onChange={(e) => setOnlyWhenOnline(e.target.checked)} /> Executar apenas com servidor online</label>
          <label className="block text-sm"><input type="checkbox" checked={isActive} onChange={(e) => setIsActive(e.target.checked)} /> {editing ? 'Agendamento ativo' : 'Ativar ao criar'}</label>
          {tasks.map((task, index) => (
            <fieldset key={index} className="space-y-2 rounded-lg border border-panel-border p-3">
              <legend className="px-1 text-sm">Tarefa {index + 1}</legend>
              <div className="grid gap-2 sm:grid-cols-3">
                <label className="text-sm">Tipo
                  <select className={field} value={task.action} onChange={(e) => {
                    const action = e.target.value as Task['action']
                    updateTask(index, { action, payload: action === 'Power' ? (powers[0]?.value ?? '') : '' })
                  }}>
                    {!allowedActions.some((option) => option.value === task.action) && <option value={task.action} disabled>
                      {taskOptions.find((option) => option.value === task.action)?.label}
                    </option>}
                    {allowedActions.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
                  </select>
                </label>
                {task.action === 'Power' ? (
                  <label className="text-sm">Ação
                    <select className={field} value={task.payload} onChange={(e) => updateTask(index, { payload: e.target.value })}>
                      {!powers.some((option) => option.value === task.payload) && <option value={task.payload} disabled>
                        {powerOptions.find((option) => option.value === task.payload)?.label ?? task.payload}
                      </option>}
                      {powers.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
                    </select>
                  </label>
                ) : task.action === 'Command' ? (
                  <label className="text-sm">Comando<input className={field} required maxLength={512} value={task.payload}
                    onChange={(e) => updateTask(index, { payload: e.target.value })} /></label>
                ) : (
                  <label className="text-sm">Exclusões (opcional)
                    <textarea className={`${field} font-mono`} rows={3} maxLength={32768} value={task.payload}
                      placeholder={'logs/\n*.tmp'} onChange={(e) => updateTask(index, { payload: e.target.value })} />
                    <span className="text-gray-400">Uma regra por linha. Se ficar vazio, usa .pteroignore. O servidor precisa estar parado.</span>
                  </label>
                )}
                <label className="text-sm">Atraso (segundos)<input className={field} type="number" min={0} max={60} required
                  value={task.timeOffset} onChange={(e) => updateTask(index, { timeOffset: Number(e.target.value) })} /></label>
              </div>
              {task.action === 'Power' && task.payload === 'kill' &&
                <p className="text-sm text-amber-300">A parada forçada pode descartar alterações ainda não salvas.</p>}
              <label className="block text-sm"><input type="checkbox" checked={task.continueOnFailure}
                onChange={(e) => updateTask(index, { continueOnFailure: e.target.checked })} /> Continuar em caso de falha</label>
              {tasks.length > 1 && <div className="flex flex-wrap gap-2">
                <button type="button" className="button button-secondary" disabled={index === 0}
                  onClick={() => moveTask(index, -1)}>Mover acima</button>
                <button type="button" className="button button-secondary" disabled={index === tasks.length - 1}
                  onClick={() => moveTask(index, 1)}>Mover abaixo</button>
                <button type="button" className="button button-secondary"
                  onClick={() => setTasks((current) => current.filter((_, taskIndex) => taskIndex !== index))}>Remover tarefa</button>
              </div>}
            </fieldset>
          ))}
          <div className="flex flex-wrap gap-2">
            <button type="button" className="button button-secondary" disabled={tasks.length >= 5}
              onClick={() => setTasks((current) => [...current, emptyTask])}>Adicionar tarefa</button>
            <button type="submit" className="button button-primary" disabled={!tasks.every(canRunTask)}>{editing ? 'Salvar agendamento' : 'Criar agendamento'}</button>
            {editing && <button type="button" className="button button-secondary" onClick={() => { changes.reset(); resetEdit() }}>Cancelar edição</button>}
          </div>
        </fieldset>
        {formError && <p role="alert" className="text-sm text-red-400">{staleEdit
          ? 'O agendamento mudou durante a edição. Copie suas alterações e reabra a versão atual.'
          : getErrorMessage(changes.error, editing ? 'Falha ao salvar agendamento' : 'Falha ao criar agendamento')}</p>}
        {!canRead && changes.isSuccess && <p role="status" className="text-sm text-gray-300">Agendamento criado.</p>}
      </form>}
      <ConfirmDialog open={can('schedule.delete') && !!selected} pending={changes.isPending} title="Excluir agendamento?"
        description={`O agendamento ${selected?.name ?? ''} será excluído.`} onCancel={() => setSelected(null)}
        onConfirm={() => { if (selected) changes.mutate({ type: 'delete', schedule: selected }) }} />
    </div>
  )
}
