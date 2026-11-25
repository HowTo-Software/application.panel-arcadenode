import { useState, useEffect, useMemo } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { useNavigate, useSearchParams } from 'react-router-dom'
import toast from 'react-hot-toast'
import { adminService, nodeService, cartridgeService } from '@/services/api'
import { getErrorMessage } from '@/lib/errorHandler'
import type { CartridgeListItem } from '@/types'
import { CartridgeSelector } from '@/components/cartridges/CartridgeSelector'
import { DynamicVariableForm } from '@/components/cartridges/DynamicVariableForm'
import { usePageTitle } from '@/lib/usePageTitle'
import {
  ServerIcon,
  ArrowLeftIcon,
  ArrowRightIcon,
  CheckIcon,
  ExclamationTriangleIcon,
  CpuChipIcon,
  CircleStackIcon,
  GlobeAltIcon,
} from '@heroicons/react/24/outline'
import clsx from 'clsx'

type WizardStep = 'cartridge' | 'configure' | 'resources' | 'review'

export default function DeployWizard() {
  const [params] = useSearchParams()
  const eggId = params.get('egg') ?? undefined
  return <ServerWizard key={eggId ?? 'standard'} eggId={eggId} />
}

function ServerWizard({ eggId }: { eggId?: string }) {
  usePageTitle('Criar servidor')
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  const [currentStep, setCurrentStep] = useState<WizardStep>('cartridge')
  const [selectedCartridge, setSelectedCartridge] =
    useState<CartridgeListItem | null>(null)
  const [selectedNode, setSelectedNode] = useState<string>('')
  const [cpuCores, setCpuCores] = useState<number[]>([])
  const [serverName, setServerName] = useState('')
  const [description, setDescription] = useState('')
  const [variables, setVariables] = useState<Record<string, string>>({})
  const [resources, setResources] = useState({
    memory: 2048,
    cpu: 100,
    disk: 10240,
  })
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [showAdvanced, setShowAdvanced] = useState(false)
  const runtime = useQuery({
    queryKey: ['egg-runtime', eggId],
    queryFn: ({ signal }) => adminService.getEggRuntime(eggId!, signal),
    enabled: !!eggId,
    retry: false,
  })

  // Fetch cartridge detail when selected
  const { data: cartridgeDetail, isLoading: cartridgeDetailLoading } = useQuery(
    {
      queryKey: ['cartridge', selectedCartridge?.id],
      queryFn: () => cartridgeService.getById(selectedCartridge!.id),
      enabled: !!selectedCartridge?.id,
    }
  )

  const eggVariables = useMemo(() => new Map(runtime.data?.variables?.map((variable) => [variable.envVariable, variable])),
    [runtime.data?.variables])
  const runtimeVariables = useMemo(() => cartridgeDetail?.variables?.flatMap((variable) => {
    const rule = eggVariables.get(variable.envVariable)
    if (!rule) return [variable]
    if (!rule.userViewable) return []
    return [{
      ...variable, name: rule.name ?? variable.name, description: rule.description ?? variable.description,
      default: rule.defaultValue, userViewable: true, userEditable: rule.userEditable,
    }]
  }) ?? [], [cartridgeDetail?.variables, eggVariables])
  const cpu = cartridgeDetail?.resources?.cpu
  const cpuScale = cpu?.unit === 'cores' ? 100 : 1
  const minimumCpu = cpu?.minimum ? cpu.minimum * cpuScale : 25
  const maximumCpu = cpu?.maximum ? cpu.maximum * cpuScale : 400
  const recommendedCpu = cpu?.recommended ? cpu.recommended * cpuScale : 100

  const { data: nodes } = useQuery({
    queryKey: ['nodes'],
    queryFn: nodeService.getAll,
  })
  const hostingNode = nodes?.find((node) => node.id === selectedNode)
  const remaining = hostingNode ? {
    memory: Math.max(0, (hostingNode.memoryCapacity ?? hostingNode.totalMemory) - hostingNode.allocatedMemory),
    disk: Math.max(0, (hostingNode.diskCapacity ?? hostingNode.totalDisk) - hostingNode.allocatedDisk),
    cpu: hostingNode.cpuCapacityPercent == null ? null :
      Math.max(0, hostingNode.cpuCapacityPercent - (hostingNode.allocatedCpuPercent ?? 0)),
  } : null
  const { data: availableCores } = useQuery({
    queryKey: ['node-cpu-cores', selectedNode],
    queryFn: ({ signal }) => nodeService.getCpuCores(selectedNode, signal),
    enabled: !!selectedNode && currentStep === 'resources',
    retry: false,
  })

  // Initialize defaults when cartridge detail loads
  useEffect(() => {
    if (cartridgeDetail) {
      setServerName(`Meu servidor de ${cartridgeDetail.name}`)
      setResources({
        memory: cartridgeDetail.resources?.memory?.recommended || 2048,
        cpu: recommendedCpu,
        disk: 10240,
      })

      // Initialize variables with defaults
      const defaultVars: Record<string, string> = {}
      runtimeVariables.forEach((v) => {
        if (v.default !== undefined && v.default !== null) {
          defaultVars[v.envVariable] = String(v.default)
        }
      })
      setVariables(defaultVars)
    }
  }, [cartridgeDetail, recommendedCpu, runtimeVariables])

  const deployMutation = useMutation({
    mutationFn: () =>
      cartridgeService.createServer({
        cartridgeId: selectedCartridge!.id,
        name: serverName,
        description,
        nodeId: selectedNode,
        variables: Object.fromEntries(Object.entries(variables).filter(([name]) => eggVariables.get(name)?.userEditable !== false)),
        memoryLimit: resources.memory,
        cpuLimit: resources.cpu,
        cpuCores: cpuCores.length ? cpuCores : undefined,
        diskLimit: resources.disk,
      }, eggId),
    onSuccess: (server) => {
      queryClient.invalidateQueries({ queryKey: ['servers'] })
      queryClient.invalidateQueries({ queryKey: ['nodes'] })
      navigate(`/servers/${server.id}`)
    },
    onError: (error: unknown) => {
      toast.error(getErrorMessage(error, 'Não foi possível criar o servidor'))
    },
  })

  const steps: { id: WizardStep; name: string }[] = [
    { id: 'cartridge', name: 'Jogo' },
    { id: 'configure', name: 'Configuração' },
    { id: 'resources', name: 'Recursos' },
    { id: 'review', name: 'Revisão' },
  ]

  const currentStepIndex = steps.findIndex((s) => s.id === currentStep)

  const handleCartridgeSelect = (cartridge: CartridgeListItem | null) => {
    setSelectedCartridge(cartridge)
    // Reset form when cartridge changes
    setVariables({})
    setErrors({})
  }

  const validateStep = (): boolean => {
    const newErrors: Record<string, string> = {}

    if (currentStep === 'cartridge') {
      if (!selectedCartridge || (eggId && selectedCartridge.id !== runtime.data?.cartridgeId)) {
        newErrors.cartridge = 'Selecione um jogo'
      }
    }

    if (currentStep === 'configure') {
      if (!serverName.trim()) {
        newErrors.serverName = 'Informe o nome do servidor'
      }
      if (!selectedNode) {
        newErrors.node = 'Selecione um nó'
      }

      // Validate required variables
      runtimeVariables.forEach((v) => {
        if (v.required && !variables[v.envVariable]?.trim()) {
          newErrors[v.envVariable] = `Informe ${v.name}`
        }
        if (v.validation?.pattern && variables[v.envVariable]) {
          const regex = new RegExp(v.validation.pattern)
          if (!regex.test(variables[v.envVariable])) {
            newErrors[v.envVariable] = `Formato inválido para ${v.name}`
          }
        }
      })
    }

    if (cpuCores.length > 0 && resources.cpu > cpuCores.length * 100)
      newErrors.cpuCores = 'Selecione mais núcleos ou reduza a quota de CPU.'

    if ((currentStep === 'resources' || currentStep === 'review') && remaining) {
      const shortages = [
        resources.memory > remaining.memory && 'memória',
        remaining.cpu !== null && resources.cpu > remaining.cpu && 'CPU',
        resources.disk > remaining.disk && 'disco',
      ].filter(Boolean)
      if (shortages.length) {
        newErrors.resources = `O nó tem saldo insuficiente de ${shortages.join(', ')}. Reduza a reserva ou selecione outro nó.`
        void queryClient.invalidateQueries({ queryKey: ['nodes'] })
      }
    }
    setErrors(newErrors)
    return Object.keys(newErrors).length === 0
  }

  const handleNext = () => {
    if (!validateStep()) return

    const nextIndex = currentStepIndex + 1
    if (nextIndex < steps.length) {
      setCurrentStep(steps[nextIndex].id)
    }
  }

  const handleBack = () => {
    const prevIndex = currentStepIndex - 1
    if (prevIndex >= 0) {
      setCurrentStep(steps[prevIndex].id)
    }
  }

  const handleDeploy = () => {
    if (!validateStep()) return
    deployMutation.mutate()
  }

  return (
    <div className="deploy-page max-w-5xl mx-auto">
      {/* Header */}
      <div className="mb-8">
        <h1 className="text-2xl font-bold text-white mb-2">Criar servidor</h1>
        <p className="text-gray-400">
          Escolha o jogo, ajuste os recursos e prepare seu novo servidor.
        </p>
      </div>

      {/* Progress Steps */}
      <div className="mb-8">
        <div className="flex items-center justify-between">
          {steps.map((step, index) => (
            <div key={step.id} className="flex items-center">
              <div
                className={clsx(
                  'flex items-center justify-center w-10 h-10 rounded-full font-medium',
                  index < currentStepIndex
                    ? 'bg-primary-600 text-white'
                    : index === currentStepIndex
                      ? 'bg-primary-600 text-white ring-4 ring-primary-600/30'
                      : 'bg-panel-dark text-gray-500'
                )}
              >
                {index < currentStepIndex ? (
                  <CheckIcon className="w-5 h-5" />
                ) : (
                  index + 1
                )}
              </div>
              <span
                className={clsx(
                  'ml-3 font-medium hidden sm:block',
                  index <= currentStepIndex ? 'text-white' : 'text-gray-500'
                )}
              >
                {step.name}
              </span>
              {index < steps.length - 1 && (
                <div
                  className={clsx(
                    'w-2 sm:w-16 h-px mx-1 sm:mx-4 rounded',
                    index < currentStepIndex
                      ? 'bg-primary-600'
                      : 'bg-panel-dark'
                  )}
                />
              )}
            </div>
          ))}
        </div>
      </div>

      {/* Step Content */}
      <div className="bg-panel-card rounded-xl border border-panel-border p-6 mb-6">
        {/* Step 1: Select Cartridge */}
        {currentStep === 'cartridge' && (
          <div>
            <h2 className="text-xl font-semibold text-white mb-4">
              Qual será o próximo jogo?
            </h2>
            {eggId && !runtime.data?.cartridgeId ? (
              <p role={runtime.isPending ? 'status' : 'alert'}>
                {runtime.isPending ? 'Consultando o perfil da egg…' : runtime.isError
                  ? getErrorMessage(runtime.error, 'Não foi possível carregar o perfil da egg.')
                  : 'A egg está sem perfil de execução aprovado.'}
              </p>
            ) : (
              <CartridgeSelector selectedCartridge={selectedCartridge} onSelect={handleCartridgeSelect}
                runtimeCartridgeId={runtime.data?.cartridgeId ?? undefined} />
            )}
            {errors.cartridge && (
              <p className="mt-4 text-red-400 text-sm">{errors.cartridge}</p>
            )}
          </div>
        )}

        {/* Step 2: Configure */}
        {currentStep === 'configure' && cartridgeDetail && (
          <div>
            <h2 className="text-xl font-semibold text-white mb-4">
              Configure seu servidor de {cartridgeDetail.name}
            </h2>

            {cartridgeDetailLoading ? (
              <div className="flex items-center justify-center py-12">
                <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-primary-500" />
              </div>
            ) : (
              <div className="space-y-6">
                {/* Basic Settings */}
                <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                  <div>
                    <label className="block text-sm font-medium text-gray-300 mb-2">
                      Nome do servidor *
                    </label>
                    <input
                      type="text"
                      aria-label="Nome do servidor"
                      value={serverName}
                      onChange={(e) => setServerName(e.target.value)}
                      className={clsx(
                        'w-full bg-panel-dark border rounded-lg px-4 py-2 text-white focus:outline-none focus:ring-2 focus:ring-primary-500',
                        errors.serverName
                          ? 'border-red-500'
                          : 'border-panel-border'
                      )}
                    />
                    {errors.serverName && (
                      <p className="mt-1 text-red-400 text-sm">
                        {errors.serverName}
                      </p>
                    )}
                  </div>

                  <div>
                    <label className="block text-sm font-medium text-gray-300 mb-2">
                      Nó de hospedagem *
                    </label>
                    <select
                      aria-label="Nó de hospedagem"
                      value={selectedNode}
                      onChange={(e) => { setSelectedNode(e.target.value); setCpuCores([]) }}
                      className={clsx(
                        'w-full bg-panel-dark border rounded-lg px-4 py-2 text-white focus:outline-none focus:ring-2 focus:ring-primary-500',
                        errors.node ? 'border-red-500' : 'border-panel-border'
                      )}
                    >
                      <option value="">Selecione um nó</option>
                      {nodes
                        ?.filter((n) => n.isOnline && !n.isMaintenanceMode)
                        .map((node) => (
                          <option key={node.id} value={node.id}>
                            {node.name} ({node.fqdn})
                          </option>
                        ))}
                    </select>
                    {errors.node && (
                      <p className="mt-1 text-red-400 text-sm">{errors.node}</p>
                    )}
                  </div>
                </div>

                <div>
                  <label className="block text-sm font-medium text-gray-300 mb-2">
                    Descrição
                  </label>
                  <textarea
                    aria-label="Descrição do servidor"
                    value={description}
                    onChange={(e) => setDescription(e.target.value)}
                    rows={2}
                    className="w-full bg-panel-dark border border-panel-border rounded-lg px-4 py-2 text-white focus:outline-none focus:ring-2 focus:ring-primary-500"
                    placeholder="Uma descrição para identificar o servidor"
                  />
                </div>

                {/* Cartridge Variables */}
                {runtimeVariables.length > 0 && (
                    <div>
                      <div className="flex items-center justify-between mb-4">
                        <h3 className="text-lg font-medium text-white">
                          Configurações do jogo
                        </h3>
                        <button
                          type="button"
                          onClick={() => setShowAdvanced(!showAdvanced)}
                          className="text-sm text-primary-400 hover:text-primary-300"
                        >
                          {showAdvanced
                            ? 'Ocultar opções avançadas'
                            : 'Mostrar opções avançadas'}
                        </button>
                      </div>
                      <DynamicVariableForm
                        variables={runtimeVariables}
                        groups={cartridgeDetail.variableGroups || []}
                        values={variables}
                        onChange={setVariables}
                        errors={errors}
                        showAdvanced={showAdvanced}
                      />
                    </div>
                  )}
              </div>
            )}
          </div>
        )}

        {/* Step 3: Resources */}
        {currentStep === 'resources' && cartridgeDetail && (
          <div>
            <h2 className="text-xl font-semibold text-white mb-4">
              Alocação de recursos
            </h2>

            <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
              <div className="bg-panel-dark rounded-xl p-6">
                <div className="flex items-center gap-3 mb-4">
                  <CpuChipIcon className="w-8 h-8 text-primary-400" />
                  <div>
                    <h3 className="font-semibold text-white">Memória</h3>
                    <p className="text-sm text-gray-400">
                      {remaining ? `${remaining.memory.toLocaleString('pt-BR')} MiB disponíveis no nó` : 'Memória alocada'}
                    </p>
                  </div>
                </div>
                <div className="space-y-2">
                  <input
                    type="range"
                    aria-label="Memória do servidor"
                    min={cartridgeDetail.resources?.memory?.minimum || 512}
                    max={cartridgeDetail.resources?.memory?.maximum || 16384}
                    step="256"
                    value={resources.memory}
                    onChange={(e) =>
                      setResources((r) => ({
                        ...r,
                        memory: parseInt(e.target.value),
                      }))
                    }
                    className="w-full accent-primary-500"
                  />
                  <div className="flex justify-between text-sm">
                    <span className="text-gray-400">
                      {cartridgeDetail.resources?.memory?.minimum || 512} MB
                    </span>
                    <span className="text-white font-medium">
                      {resources.memory} MB
                    </span>
                    <span className="text-gray-400">
                      {cartridgeDetail.resources?.memory?.maximum || 16384} MB
                    </span>
                  </div>
                </div>
              </div>

              <div className="bg-panel-dark rounded-xl p-6">
                <div className="flex items-center gap-3 mb-4">
                  <ServerIcon className="w-8 h-8 text-green-400" />
                  <div>
                    <h3 className="font-semibold text-white">CPU</h3>
                    <p className="text-sm text-gray-400">
                      {remaining?.cpu != null ? `${remaining.cpu}% disponíveis no nó` : 'Capacidade de processamento'}
                    </p>
                  </div>
                </div>
                <div className="space-y-2">
                  <input
                    type="range"
                    aria-label="CPU do servidor"
                    min={minimumCpu}
                    max={maximumCpu}
                    step="25"
                    value={resources.cpu}
                    onChange={(e) =>
                      setResources((r) => ({
                        ...r,
                        cpu: parseInt(e.target.value),
                      }))
                    }
                    className="w-full accent-green-500"
                  />
                  <div className="flex justify-between text-sm">
                    <span className="text-gray-400">
                      {minimumCpu}%
                    </span>
                    <span className="text-white font-medium">
                      {resources.cpu}%
                    </span>
                    <span className="text-gray-400">
                      {maximumCpu}%
                    </span>
                  </div>
                </div>
                {availableCores && availableCores.length > 0 && (
                  <fieldset className="mt-4">
                    <legend className="mb-2 text-sm font-medium">Núcleos lógicos (opcional)</legend>
                    <div className="flex flex-wrap gap-3">
                      {availableCores.map((core) => (
                        <label key={core} className="flex items-center gap-1 text-sm">
                          <input type="checkbox" checked={cpuCores.includes(core)}
                            disabled={cpuCores.length >= 64 && !cpuCores.includes(core)}
                            onChange={(event) => {
                              const checked = event.target.checked
                              setCpuCores((current) => checked ? [...current, core] : current.filter((value) => value !== core))
                            }} />
                          {core}
                        </label>
                      ))}
                    </div>
                    {cpuCores.length > 0 && resources.cpu > cpuCores.length * 100 &&
                      <p role="alert" className="mt-2 text-sm text-red-400">Selecione mais núcleos ou reduza a quota de CPU.</p>}
                  </fieldset>
                )}
              </div>

              <div className="bg-panel-dark rounded-xl p-6">
                <div className="flex items-center gap-3 mb-4">
                  <CircleStackIcon className="w-8 h-8 text-blue-400" />
                  <div>
                    <h3 className="font-semibold text-white">Disco</h3>
                    <p className="text-sm text-gray-400">
                      {remaining ? `${remaining.disk.toLocaleString('pt-BR')} MiB disponíveis no nó` : 'Espaço em disco'}
                    </p>
                  </div>
                </div>
                <div className="space-y-2">
                  <input
                    type="range"
                    aria-label="Disco do servidor"
                    min={cartridgeDetail.resources?.disk?.minimum || 1024}
                    max={
                      (cartridgeDetail.resources?.disk?.minimum || 1024) * 10
                    }
                    step="1024"
                    value={resources.disk}
                    onChange={(e) =>
                      setResources((r) => ({
                        ...r,
                        disk: parseInt(e.target.value),
                      }))
                    }
                    className="w-full accent-blue-500"
                  />
                  <div className="flex justify-between text-sm">
                    <span className="text-gray-400">
                      {(
                        (cartridgeDetail.resources?.disk?.minimum || 1024) /
                        1024
                      ).toFixed(0)}{' '}
                      GB
                    </span>
                    <span className="text-white font-medium">
                      {(resources.disk / 1024).toFixed(1)} GB
                    </span>
                    <span className="text-gray-400">
                      {(
                        ((cartridgeDetail.resources?.disk?.minimum || 1024) *
                          10) /
                        1024
                      ).toFixed(0)}{' '}
                      GB
                    </span>
                  </div>
                </div>
              </div>
            </div>

            {/* Port Information */}
            {cartridgeDetail.ports && cartridgeDetail.ports.length > 0 && (
              <div className="mt-6">
                <h3 className="text-lg font-medium text-white mb-4">
                  Portas de rede
                </h3>
                <div className="bg-panel-dark rounded-xl p-4">
                  <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                    {cartridgeDetail.ports.map((port) => (
                      <div
                        key={port.name}
                        className="flex items-center justify-between p-3 bg-panel-border rounded-lg"
                      >
                        <div className="flex items-center gap-3">
                          <GlobeAltIcon className="w-5 h-5 text-gray-400" />
                          <div>
                            <p className="text-white font-medium">
                              {port.name}
                            </p>
                            <p className="text-xs text-gray-400">
                              {port.description}
                            </p>
                          </div>
                        </div>
                        <div className="text-right">
                          <p className="text-white font-mono">{port.default}</p>
                          <p className="text-xs text-gray-400">
                            {port.protocol}
                          </p>
                        </div>
                      </div>
                    ))}
                  </div>
                </div>
              </div>
            )}
          </div>
        )}

        {/* Step 4: Review */}
        {currentStep === 'review' && cartridgeDetail && (
          <div>
            <h2 className="text-xl font-semibold text-white mb-4">Revisão</h2>

            <div className="space-y-6">
              {/* Summary Card */}
              <div className="bg-panel-dark rounded-xl p-6">
                <div className="flex items-start gap-4">
                  {cartridgeDetail.icon &&
                  /^(https?:|\/)/.test(cartridgeDetail.icon) ? (
                    <img
                      src={cartridgeDetail.icon}
                      alt={cartridgeDetail.name}
                      className="w-16 h-16 rounded-lg object-cover"
                    />
                  ) : (
                    <span className="text-5xl">
                      {cartridgeDetail.icon || '🎮'}
                    </span>
                  )}
                  <div className="flex-1">
                    <h3 className="text-xl font-bold text-white">
                      {serverName}
                    </h3>
                    <p className="text-gray-400">{cartridgeDetail.name}</p>
                    {description && (
                      <p className="text-sm text-gray-500 mt-2">
                        {description}
                      </p>
                    )}
                    <div className="flex flex-wrap gap-2 mt-3">
                      {cartridgeDetail.tags?.map((tag) => (
                        <span
                          key={tag}
                          className="px-2 py-0.5 bg-panel-border rounded text-xs text-gray-400"
                        >
                          {tag}
                        </span>
                      ))}
                    </div>
                  </div>
                </div>
              </div>

              {/* Configuration Summary */}
              <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
                <div className="bg-panel-dark rounded-xl p-4">
                  <h4 className="font-medium text-white mb-3">Recursos</h4>
                  <div className="space-y-2 text-sm">
                    <div className="flex justify-between">
                      <span className="text-gray-400">Memória</span>
                      <span className="text-white">{resources.memory} MB</span>
                    </div>
                    <div className="flex justify-between">
                      <span className="text-gray-400">CPU</span>
                      <span className="text-white">{resources.cpu}%</span>
                    </div>
                    <div className="flex justify-between">
                      <span className="text-gray-400">Disco</span>
                      <span className="text-white">
                        {(resources.disk / 1024).toFixed(1)} GB
                      </span>
                    </div>
                  </div>
                </div>

                <div className="bg-panel-dark rounded-xl p-4">
                  <h4 className="font-medium text-white mb-3">Rede</h4>
                  <div className="space-y-2 text-sm">
                    {cartridgeDetail.ports?.map((port) => (
                      <div key={port.name} className="flex justify-between">
                        <span className="text-gray-400">{port.name}</span>
                        <span className="text-white font-mono">
                          {port.default}/{port.protocol}
                        </span>
                      </div>
                    ))}
                  </div>
                </div>
              </div>

              {/* Key Settings */}
              <div className="bg-panel-dark rounded-xl p-4">
                <h4 className="font-medium text-white mb-3">
                  Configurações principais
                </h4>
                <div className="grid grid-cols-2 md:grid-cols-3 gap-4 text-sm">
                  {runtimeVariables
                    .filter((v) => v.required || v.userEditable)
                    .slice(0, 6)
                    .map((v) => (
                      <div key={v.envVariable}>
                        <span className="text-gray-400 block">{v.name}</span>
                        <span className="text-white">
                          {v.ui?.component === 'password'
                            ? '••••••••'
                            : (variables[v.envVariable] ??
                              String(v.default ?? '-'))}
                        </span>
                      </div>
                    ))}
                </div>
              </div>

              {/* Node Info */}
              <div className="bg-panel-dark rounded-xl p-4">
                <h4 className="font-medium text-white mb-3">
                  Destino de hospedagem
                </h4>
                <div className="text-sm">
                  <span className="text-gray-400">Nó: </span>
                  <span className="text-white">
                    {hostingNode?.name || selectedNode}
                  </span>
                </div>
              </div>

              {deployMutation.isError && (
                <div className="p-4 bg-red-500/10 border border-red-500/30 rounded-lg">
                  <div className="flex gap-3">
                    <ExclamationTriangleIcon className="w-5 h-5 text-red-500 flex-shrink-0" />
                    <div>
                      <p className="text-red-400">
                        Não foi possível criar o servidor. Tente novamente.
                      </p>
                      <p className="text-red-400/70 text-sm mt-1">
                        {getErrorMessage(
                          deployMutation.error,
                          'Erro desconhecido'
                        )}
                      </p>
                    </div>
                  </div>
                </div>
              )}
            </div>
          </div>
        )}
      </div>

      {errors.resources && (currentStep === 'resources' || currentStep === 'review') && (
        <p role="alert" className="text-sm text-red-400">{errors.resources}</p>
      )}

      {/* Navigation Buttons */}
      <div className="flex justify-between">
        <button
          onClick={
            currentStepIndex === 0 ? () => navigate('/servers') : handleBack
          }
          className="flex items-center gap-2 px-4 py-2 bg-panel-dark hover:bg-panel-border text-white font-medium rounded-lg transition-colors"
        >
          <ArrowLeftIcon className="w-5 h-5" />
          {currentStepIndex === 0 ? 'Cancelar' : 'Voltar'}
        </button>

        {currentStep === 'review' ? (
          <button
            onClick={handleDeploy}
            disabled={deployMutation.isPending}
            className="flex items-center gap-2 px-6 py-2 bg-primary-600 hover:bg-primary-700 disabled:opacity-50 disabled:cursor-not-allowed text-white font-medium rounded-lg transition-colors"
          >
            {deployMutation.isPending ? (
              <>
                <div className="animate-spin rounded-full h-5 w-5 border-b-2 border-white" />
                Criando servidor...
              </>
            ) : (
              <>
                <ServerIcon className="w-5 h-5" />
                Criar servidor
              </>
            )}
          </button>
        ) : (
          <button
            onClick={handleNext}
            disabled={currentStep === 'configure' && cartridgeDetailLoading}
            className="flex items-center gap-2 px-4 py-2 bg-primary-600 hover:bg-primary-700 disabled:opacity-50 text-white font-medium rounded-lg transition-colors"
          >
            Continuar
            <ArrowRightIcon className="w-5 h-5" />
          </button>
        )}
      </div>
    </div>
  )
}
