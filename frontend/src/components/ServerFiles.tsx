import { useEffect, useRef, useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AxiosError } from 'axios'
import { ConfirmDialog } from '@/components/ui/ConfirmDialog'
import { hasServerPermission } from '@/lib/serverPermissions'
import { serverService } from '@/services/api'
import { downloadServerFile } from '@/services/downloads'

const isPreview = import.meta.env.DEV && import.meta.env.VITE_DEMO_MODE === 'true'
const editableFile = /\.(cfg|conf|ini|json|lua|properties|toml|txt|xml|ya?ml)$/i
const archiveFile = /\.(?:tar\.gz|tgz|tar|zip)$/i
const permissionMode = /^0?[0-7]{3}$/
const maximumSelection = 40
const maximumUploadBytes = 512 * 1024 * 1024
const maximumTextBytes = 8192
const filenameBytes = new TextEncoder()
type EntryChange = { kind: 'directory' | 'move' | 'copy' | 'extract'; source: string; destination: string }
type FileSelection = { directory: string; entries: string[] }
type PermissionDraft = { directory: string; names: string; mode: string }
type FileDraft = { path: string; text: string; savedText: string; digest: string; crlf: boolean }
const entryActions = {
  directory: { label: 'Criar pasta', notice: 'Pasta criada.' },
  move: { label: 'Mover item', notice: 'Item movido.' },
  copy: { label: 'Copiar arquivo', notice: 'Arquivo copiado.' },
  extract: { label: 'Extrair arquivo', notice: 'Arquivos extraídos em' },
}

function suggestCopyPath(directory: string, name: string) {
  const dot = name.lastIndexOf('.')
  const extension = dot > 0 ? name.slice(dot) : ''
  const basename = dot > 0 ? name.slice(0, dot) : name
  return `${directory ? `${directory}/` : ''}${basename}-copia${extension}`
}

function validEntryName(name: string) {
  return !!name.trim() && name !== '.' && name !== '..' && !/[\\/]/.test(name) && filenameBytes.encode(name).length <= 255
}

function validSelection(entries: string[]) {
  return entries.length >= 1 && entries.length <= maximumSelection && new Set(entries).size === entries.length &&
    entries.every(validEntryName)
}

function suggestExtractionPath(directory: string, name: string) {
  const folder = name.replace(archiveFile, '') || 'arquivos-extraidos'
  return directory ? `${directory}/${folder}` : folder
}

function validGamePath(path: string) {
  return !!path.trim() && !path.includes('\\') && path.split('/').every((part) => !!part && part !== '.' && part !== '..')
}

async function fileDigest(bytes: BufferSource) {
  const checksum = await crypto.subtle.digest('SHA-256', bytes)
  return Array.from(new Uint8Array(checksum), (byte) => byte.toString(16).padStart(2, '0')).join('')
}

export default function ServerFiles({ serverId, suspended, stopped, visible, permissions }: {
  serverId: string; suspended: boolean; stopped: boolean; visible: boolean; permissions?: string[] | null
}) {
  const canList = hasServerPermission(permissions, 'file.read')
  const canDownload = hasServerPermission(permissions, 'file.read-content')
  const canCreate = hasServerPermission(permissions, 'file.create')
  const canMove = hasServerPermission(permissions, 'file.update')
  const canArchive = hasServerPermission(permissions, 'file.archive')
  const canDelete = hasServerPermission(permissions, 'file.delete')
  const client = useQueryClient()
  const [path, setPath] = useState('')
  const [directory, setDirectory] = useState('')
  const [searchDraft, setSearchDraft] = useState('')
  const [filter, setFilter] = useState('')
  const [pageCursors, setPageCursors] = useState<(string | null)[]>([null])
  const cursor = pageCursors[pageCursors.length - 1]
  const [selectedFile, setSelectedFile] = useState<File | null>(null)
  const [uploadProgress, setUploadProgress] = useState(0)
  const [entryChange, setEntryChange] = useState<EntryChange | null>(null)
  const [permissionDraft, setPermissionDraft] = useState<PermissionDraft | null>(null)
  const [changeNotice, setChangeNotice] = useState('')
  const [selectedEntries, setSelectedEntries] = useState<string[]>([])
  const [archiveName, setArchiveName] = useState('arquivos.tar.gz')
  const [archiveDirectory, setArchiveDirectory] = useState('')
  const [archiveNames, setArchiveNames] = useState('')
  const [deleteDirectory, setDeleteDirectory] = useState('')
  const [deleteNames, setDeleteNames] = useState('')
  const [deleteTarget, setDeleteTarget] = useState<FileSelection | null>(null)
  const [drafts, setDrafts] = useState<{ files: FileDraft[]; activePath: string | null }>({ files: [], activePath: null })
  const [discardPath, setDiscardPath] = useState<string | null>(null)
  const editor = drafts.files.find((file) => file.path === drafts.activePath)
  const editorContent = editor?.crlf ? editor.text.replace(/\n/g, '\r\n') : editor?.text ?? ''
  const editorBytes = filenameBytes.encode(editorContent).length
  const unsavedFiles = drafts.files.some((file) => file.text !== file.savedText)
  const uploadInput = useRef<HTMLInputElement>(null)
  const uploadAbort = useRef<AbortController | null>(null)
  useEffect(() => () => uploadAbort.current?.abort(), [])
  useEffect(() => {
    if (!unsavedFiles) return
    function warnBeforeLeaving(event: BeforeUnloadEvent) { event.preventDefault(); event.returnValue = true }
    window.addEventListener('beforeunload', warnBeforeLeaving)
    return () => window.removeEventListener('beforeunload', warnBeforeLeaving)
  }, [unsavedFiles])
  const listing = useQuery({
    queryKey: ['server-files', serverId, directory, filter, cursor],
    queryFn: ({ signal }) => serverService.listFiles(serverId, directory, signal, cursor, filter),
    enabled: visible && !suspended && canList,
    staleTime: 0,
    retry: false,
  })
  const showDirectoryPage = !listing.isError && (cursor === null || !listing.isFetching)
  const nextDirectoryPage = showDirectoryPage ? listing.data?.nextCursor : undefined
  const canSearchDirectory = cursor !== null || filter !== '' || (listing.data && 'nextCursor' in listing.data)
  const directoryChanged = listing.error instanceof AxiosError && listing.error.response?.status === 409

  function refreshListing() {
    setPageCursors([null])
    setSelectedEntries([])
    return client.invalidateQueries({ queryKey: ['server-files', serverId], refetchType: cursor === null ? 'active' : 'none' })
  }

  function searchDirectory(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    const text = searchDraft.trim()
    if (text === filter && cursor === null) {
      void listing.refetch()
      return
    }
    setFilter(text)
    setPageCursors([null])
  }
  const download = useMutation({
    mutationFn: (relativePath: string) => downloadServerFile(serverId, relativePath),
  })
  const upload = useMutation({
    mutationFn: ({ file, destination }: { file: File; destination: string }) => {
      const controller = new AbortController()
      uploadAbort.current = controller
      setUploadProgress(0)
      return serverService.uploadFile(serverId, destination ? `${destination}/${file.name}` : file.name, file,
        controller.signal, (percent) => { if (uploadAbort.current === controller) setUploadProgress(percent) })
    },
    onSuccess: () => {
      setSelectedFile(null)
      if (uploadInput.current) uploadInput.current.value = ''
      if (canList) return refreshListing()
    },
    onSettled: () => { uploadAbort.current = null },
  })
  const openEditor = useMutation({
    mutationFn: async (filePath: string): Promise<FileDraft> => {
      const blob = await serverService.downloadFile(serverId, filePath)
      if (blob.size > maximumTextBytes) throw new Error('Arquivo acima de 8 KiB.')
      const bytes = await blob.arrayBuffer()
      const content = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes)
      if (content.includes('\0')) throw new Error('Arquivo fora do formato UTF-8.')
      const crlf = content.includes('\r\n')
      const text = content.replace(/\r\n?/g, '\n')
      return { path: filePath, text, savedText: text, digest: await fileDigest(bytes), crlf }
    },
    onSuccess: (file) => setDrafts((current) => ({
      files: current.files.some((draft) => draft.path === file.path) ? current.files : [...current.files, file],
      activePath: file.path,
    })),
  })
  const saveEditor = useMutation({
    mutationFn: async ({ file, content }: { file: FileDraft; content: string }) => {
      const nextDigest = await fileDigest(filenameBytes.encode(content))
      await serverService.editTextFile(serverId, file.path, content, file.digest)
      return nextDigest
    },
    onSuccess: (digest, { file }) => {
      setDrafts((current) => ({ ...current, files: current.files.map((draft) =>
        draft.path === file.path ? { ...draft, digest, savedText: file.text } : draft) }))
      if (canList) return refreshListing()
    },
  })
  const changeEntry = useMutation({
    mutationFn: ({ kind, source, destination }: EntryChange) => {
      switch (kind) {
        case 'directory': return serverService.createDirectory(serverId, source ? `${source}/${destination}` : destination)
        case 'move': return serverService.moveFile(serverId, source, destination)
        case 'copy': return serverService.copyFile(serverId, source, destination)
        case 'extract': return serverService.extractArchive(serverId, source, destination)
      }
    },
    onSuccess: (_, change) => {
      const notice = entryActions[change.kind].notice
      setChangeNotice(change.kind === 'extract' ? `${notice} ${change.destination}.` : notice)
      setEntryChange(null)
      if (canList) return refreshListing()
    },
  })
  const archive = useMutation({
    mutationFn: ({ directory, entries, name }: { directory: string; entries: string[]; name: string }) =>
      serverService.compressFiles(serverId, directory, entries, name),
    onSuccess: (_, input) => {
      setSelectedEntries([])
      setArchiveNames('')
      setChangeNotice(`Arquivo ${input.name} criado.`)
      if (canList) return refreshListing()
    },
  })
  const remove = useMutation({
    mutationFn: (selection: FileSelection) => serverService.deleteFileEntries(serverId, selection.directory, selection.entries),
    onSuccess: (_, { entries }) => {
      setDeleteNames('')
      setChangeNotice(entries.length === 1 ? 'Item excluído.' : `${entries.length} itens excluídos.`)
    },
    onSettled: () => {
      setDeleteTarget(null)
      setSelectedEntries([])
      if (canList) return refreshListing()
    },
  })
  const changePermissions = useMutation({
    mutationFn: ({ directory, entries, mode }: FileSelection & { mode: string }) =>
      serverService.changeFilePermissions(serverId, directory, entries, mode),
    onSuccess: () => {
      setPermissionDraft(null)
      setSelectedEntries([])
      setChangeNotice('Permissões atualizadas.')
    },
    onSettled: () => {
      if (canList) return refreshListing()
    },
  })
  const archiveEntries = canList ? selectedEntries : archiveNames.split(/\r?\n/).filter(Boolean)
  const validArchive = validSelection(archiveEntries) && !archiveEntries.includes(archiveName) && validEntryName(archiveName) && /\.tar\.gz$/i.test(archiveName)
  const deleteEntries = canList ? selectedEntries : deleteNames.split(/\r?\n/).filter(Boolean)
  const validDeletion = validSelection(deleteEntries) && (canList || !deleteDirectory || validGamePath(deleteDirectory))
  const permissionEntries = permissionDraft?.names.split(/\r?\n/).filter(Boolean) ?? []
  const canApplyPermissions = permissionDraft !== null && validSelection(permissionEntries) &&
    (!permissionDraft.directory || validGamePath(permissionDraft.directory)) && permissionMode.test(permissionDraft.mode)
  const permissionStatus = changePermissions.error instanceof AxiosError ? changePermissions.error.response?.status : undefined
  const permissionError = permissionStatus === 409 ? 'Pare o servidor antes de alterar as permissões.'
    : permissionStatus === 405 ? 'A gravação está desativada neste node.'
      : permissionStatus === 404 ? 'Confira os itens selecionados e se o node oferece esse recurso.'
        : 'Não foi possível concluir a alteração. Confira as permissões na pasta.'
  const archiveStatus = archive.error instanceof AxiosError ? archive.error.response?.status : undefined
  const archiveError = archiveStatus === 409 ? 'O nome já existe ou o servidor voltou a rodar.'
    : archiveStatus === 413 ? 'A seleção excede 512 MiB ou 20 mil entradas.'
      : archiveStatus === 404 ? 'Confira se a pasta e os itens selecionados ainda existem.'
        : archiveStatus === 405 ? 'A gravação está desativada neste node.'
          : archiveStatus === 504 ? 'A compactação excedeu o tempo limite. Tente com menos arquivos.'
            : 'Não foi possível compactar. Confira o nome de destino e os itens selecionados.'
  const entryStatus = changeEntry.error instanceof AxiosError ? changeEntry.error.response?.status : undefined
  const deleteStatus = remove.error instanceof AxiosError ? remove.error.response?.status : undefined
  const deleteError = deleteStatus === 409 ? 'Pare o servidor antes de excluir arquivos.'
    : deleteStatus === 404 ? 'Confira se a pasta e os itens selecionados ainda existem.'
      : deleteStatus === 405 ? 'A gravação está desativada neste node.'
        : deleteStatus === 413 ? 'A seleção excede o limite de itens ou de profundidade.'
          : deleteStatus === 403 ? 'Você não tem permissão para excluir arquivos deste servidor.'
            : 'A exclusão não foi concluída. Confira o conteúdo da pasta.'
  const extraction = entryChange?.kind === 'extract'
  const entryError = entryStatus === 409 ? 'O destino já existe ou o servidor voltou a rodar.'
    : entryStatus === 404 ? 'A origem ou a pasta de destino está indisponível.'
      : entryStatus === 405 ? 'A gravação está desativada neste node.'
        : entryStatus === 413 ? (extraction ? 'O arquivo excede os limites de extração.' : 'O arquivo excede o limite de cópia de 512 MiB.')
          : extraction && entryStatus === 422 ? 'O arquivo está inválido ou contém uma entrada não permitida.'
            : entryStatus === 400 ? 'Confira o nome ou o caminho informado.'
              : 'Não foi possível concluir a operação no volume do jogo.'
  const filesChanging = upload.isPending || changeEntry.isPending || archive.isPending || remove.isPending || saveEditor.isPending || changePermissions.isPending
  const canChangeEntries = !isPreview && stopped && !suspended && !directoryChanged &&
    drafts.files.length === 0 && !openEditor.isPending && !filesChanging
  const validDestination = entryChange && validGamePath(entryChange.destination) && (entryChange.kind === 'directory'
    ? !entryChange.destination.includes('/') : entryChange.source !== entryChange.destination) &&
    (!extraction || (validGamePath(entryChange.source) && archiveFile.test(entryChange.source)))
  const saveStatus = saveEditor.error instanceof AxiosError ? saveEditor.error.response?.status : undefined
  const uploadStatus = upload.error instanceof AxiosError ? upload.error.response?.status : undefined
  const uploadCanceled = upload.error instanceof AxiosError && upload.error.code === 'ERR_CANCELED'
  const uploadError = uploadStatus === 409 ? 'Já existe um arquivo com esse nome.'
    : uploadStatus === 405 ? 'O upload está desativado neste node.'
      : uploadStatus === 413 ? 'O arquivo excede o limite de envio para este servidor.'
        : uploadStatus === 429 ? 'O node está recebendo outros arquivos. Tente novamente.'
          : uploadStatus === 504 ? 'O envio excedeu o tempo limite.'
            : 'Não foi possível enviar o arquivo.'
  const status = download.error instanceof AxiosError ? download.error.response?.status : undefined
  const errorMessage = status === 404
    ? 'Arquivo indisponível. Confira o caminho.'
    : status === 413
      ? 'O arquivo excede o limite de 512 MiB.'
      : status === 503
        ? 'O node deste servidor ainda não oferece acesso a arquivos.'
        : status === 429 ? 'Há outros downloads em andamento. Tente novamente.' : 'Não foi possível baixar o arquivo.'

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    if (canDownload && path.trim() && !isPreview && !suspended && !download.isPending) download.mutate(path)
  }

  function navigateDirectory(destination: string) {
    setEntryChange(null)
    changeEntry.reset()
    changePermissions.reset()
    setPermissionDraft(null)
    setSelectedEntries([])
    archive.reset()
    remove.reset()
    setChangeNotice('')
    setSearchDraft('')
    setFilter('')
    setPageCursors([null])
    setDirectory(destination)
  }

  function selectEntryChange(change: EntryChange) {
    changeEntry.reset()
    setPermissionDraft(null)
    setChangeNotice('')
    setEntryChange(change)
  }

  function editPermissions(entries: string[], mode = '') {
    changePermissions.reset()
    setEntryChange(null)
    setChangeNotice('')
    setPermissionDraft({ directory, names: entries.join('\n'), mode })
  }

  function selectForDeletion(entries: string[], source: string) {
    remove.reset()
    setChangeNotice('')
    setDeleteTarget({ directory: source, entries })
  }

  function selectEditor(filePath: string) {
    saveEditor.reset()
    openEditor.reset()
    if (drafts.files.some((file) => file.path === filePath)) setDrafts((current) => ({ ...current, activePath: filePath }))
    else openEditor.mutate(filePath)
  }

  function closeEditor(filePath: string) {
    saveEditor.reset()
    setDrafts((current) => {
      const index = current.files.findIndex((file) => file.path === filePath)
      const files = current.files.filter((file) => file.path !== filePath)
      const nextFile = files[Math.min(index, files.length - 1)]
      return { files, activePath: current.activePath === filePath ? nextFile?.path ?? null : current.activePath }
    })
    setDiscardPath(null)
  }

  return (
    <section className="rounded-xl border border-panel-border bg-panel-card p-5" aria-labelledby="server-file-heading">
      <h3 id="server-file-heading" className="font-semibold">{canDownload ? 'Baixar arquivo' : 'Arquivos'}</h3>
      {canDownload && <>
      <p className="mt-1 text-sm text-gray-400">Use o caminho relativo à pasta do jogo. Limite de 512 MiB por arquivo.</p>
      <form className="mt-4" onSubmit={submit}>
        <label className="mb-1 block text-sm font-medium" htmlFor="server-file-path">Caminho do arquivo</label>
        <div className="flex flex-wrap gap-2">
          <input id="server-file-path" className="min-w-0 flex-1 rounded-lg border border-panel-border bg-panel-dark px-3 py-2"
            placeholder="server.properties" disabled={isPreview || suspended || download.isPending} maxLength={1024}
            value={path} onChange={(event) => { setPath(event.target.value); download.reset() }} required />
          <button className="button button-primary" disabled={isPreview || suspended || !path.trim() || download.isPending}>
            {download.isPending ? 'Preparando…' : 'Baixar'}
          </button>
        </div>
      </form>
      </>}
      {isPreview && <p className="mt-3 text-sm text-gray-400">Downloads ficam disponíveis no painel conectado.</p>}
      {suspended && <p className="mt-3 text-sm text-gray-400">O servidor está suspenso.</p>}
      {download.isError && <p className="mt-3 text-sm text-red-400" role="alert">{errorMessage}</p>}
      {openEditor.isError && <p className="mt-3 text-sm text-red-400" role="alert">Não foi possível abrir o arquivo UTF-8.</p>}
      {saveEditor.isSuccess && <p className="mt-3 text-sm text-gray-300" role="status">Alterações salvas.</p>}
      {editor && canCreate && canDownload && (
        <form className="mt-4 space-y-2 border-t border-panel-border pt-4" onSubmit={(event) => {
          event.preventDefault()
          if (stopped && !suspended && !filesChanging && editor.text !== editor.savedText && editorBytes <= maximumTextBytes)
            saveEditor.mutate({ file: editor, content: editorContent })
        }}>
          <div className="flex flex-wrap gap-2" role="group" aria-label="Arquivos abertos">
            {drafts.files.map((file) => (
              <button key={file.path} type="button" className={`button break-all ${file.path === editor.path ? 'button-primary' : 'button-secondary'}`}
                aria-pressed={file.path === editor.path} disabled={openEditor.isPending || saveEditor.isPending}
                onClick={() => selectEditor(file.path)}>
                {file.path}{file.text !== file.savedText && ' •'}
              </button>
            ))}
          </div>
          <label htmlFor="server-file-editor" className="block break-all text-sm font-medium">{editor.path}</label>
          <textarea id="server-file-editor" value={editor.text} onChange={(event) => {
            const text = event.target.value
            setDrafts((current) => ({ ...current, files: current.files.map((file) => file.path === editor.path ? { ...file, text } : file) }))
          }} rows={12} maxLength={maximumTextBytes} disabled={saveEditor.isPending}
            className="w-full resize-y rounded-lg border border-panel-border bg-panel-dark p-3 font-mono text-sm" />
          {editorBytes > maximumTextBytes && <p role="alert" className="text-sm text-red-400">O conteúdo excede 8 KiB em UTF-8.</p>}
          <div className="flex flex-wrap gap-2">
            <button className="button button-primary" disabled={!stopped || suspended || filesChanging || editor.text === editor.savedText || editorBytes > maximumTextBytes}>
              Salvar arquivo
            </button>
            <button type="button" className="button button-secondary" disabled={openEditor.isPending || saveEditor.isPending}
              onClick={() => editor.text === editor.savedText ? closeEditor(editor.path) : setDiscardPath(editor.path)}>Fechar arquivo</button>
          </div>
          {saveEditor.isError && <p role="alert" className="text-sm text-red-400">
            {saveStatus === 412 ? 'O arquivo mudou no node. Copie o rascunho antes de reabrir o arquivo.'
              : saveStatus === 409 ? 'Pare o servidor antes de salvar.'
                : saveStatus === 405 ? 'A gravação está desativada no node.' : 'Não foi possível salvar o arquivo.'}
          </p>}
        </form>
      )}
      {!suspended && (
        <section className="mt-5 border-t border-panel-border pt-4" aria-labelledby="server-directory-heading">
          <div className="flex flex-wrap items-center gap-3">
            <h4 id="server-directory-heading" className="min-w-0 break-all font-medium">{directory || 'Pasta do jogo'}</h4>
            {directory && (
              <button type="button" className="button button-secondary" disabled={filesChanging}
                onClick={() => navigateDirectory(directory.includes('/') ? directory.slice(0, directory.lastIndexOf('/')) : '')}>
                Pasta anterior
              </button>
            )}
            {canList && <button type="button" className="button button-secondary" onClick={() => void refreshListing()} disabled={listing.isFetching || filesChanging}>
              Atualizar
            </button>}
            {canCreate && (listing.isSuccess || !canList) && <button type="button" className="button button-secondary" disabled={!canChangeEntries}
              onClick={() => selectEntryChange({ kind: 'directory', source: directory, destination: '' })}>Nova pasta</button>}
            {canCreate && !canList && <button type="button" className="button button-secondary" disabled={!canChangeEntries}
              onClick={() => selectEntryChange({ kind: 'extract', source: '', destination: '' })}>Extrair arquivo</button>}
          </div>
          {canList && canSearchDirectory && (
            <form className="mt-3 flex flex-wrap items-end gap-2" onSubmit={searchDirectory}>
              <label className="min-w-0 flex-1 text-sm font-medium">
                Buscar nesta pasta
                <input type="search" value={searchDraft} maxLength={128} onChange={(event) => setSearchDraft(event.target.value)}
                  className="mt-1 block w-full rounded-lg border border-panel-border bg-panel-card px-3 py-2" />
              </label>
              <button className="button button-secondary" disabled={listing.isFetching || filesChanging}>Buscar</button>
              {filter && <button type="button" className="button button-secondary" disabled={filesChanging} onClick={() => {
                setSearchDraft('')
                setFilter('')
                setPageCursors([null])
              }}>Limpar busca</button>}
            </form>
          )}
          {!isPreview && !stopped && (canCreate || canMove) && <p className="mt-2 text-sm text-gray-400">Pare o servidor para alterar arquivos e permissões.</p>}
          {!isPreview && !stopped && canArchive && <p className="mt-2 text-sm text-gray-400">Pare o servidor para compactar arquivos.</p>}
          {!isPreview && !stopped && canDelete && <p className="mt-2 text-sm text-gray-400">Pare o servidor para excluir arquivos.</p>}
          {changeNotice && <p className="mt-2 text-sm text-gray-300" role="status">{changeNotice}</p>}
          {remove.isError && <p className="mt-2 text-sm text-red-400" role="alert">{deleteError}</p>}
          {canMove && (!canList || selectedEntries.length > 0) && !permissionDraft && (
            <div className="mt-3 flex flex-wrap gap-2">
              <button type="button" className="button button-secondary" disabled={!canChangeEntries}
                onClick={() => editPermissions(selectedEntries)}>{canList ? 'Permissões da seleção' : 'Alterar permissões'}</button>
              {canList && !canArchive && !canDelete && <button type="button" className="button button-secondary" disabled={filesChanging}
                onClick={() => setSelectedEntries([])}>Limpar seleção</button>}
            </div>
          )}
          {canMove && permissionDraft && (
            <form className="mt-3 space-y-2 rounded-lg border border-panel-border bg-panel-dark p-3" onSubmit={(event) => {
              event.preventDefault()
              if (canChangeEntries && canApplyPermissions)
                changePermissions.mutate({ directory: permissionDraft.directory, entries: permissionEntries, mode: permissionDraft.mode })
            }}>
              {canList ? <p className="break-all text-sm text-gray-400">Permissões de {permissionEntries.length === 1
                ? permissionEntries[0] : `${permissionEntries.length} itens em ${permissionDraft.directory || 'Pasta do jogo'}`}</p> : <>
                <label className="block text-sm">Pasta dos itens (opcional)
                  <input className="mt-1 block w-full rounded-lg border border-panel-border bg-panel-card px-3 py-2"
                    value={permissionDraft.directory} maxLength={1024} disabled={!canChangeEntries}
                    onChange={(event) => setPermissionDraft({ ...permissionDraft, directory: event.target.value })} />
                </label>
                <label className="block text-sm">Nomes dos itens, um por linha
                  <textarea className="mt-1 block w-full rounded-lg border border-panel-border bg-panel-card px-3 py-2"
                    value={permissionDraft.names} rows={3} maxLength={10240} disabled={!canChangeEntries} required
                    onChange={(event) => setPermissionDraft({ ...permissionDraft, names: event.target.value })} />
                </label>
              </>}
              <label className="block text-sm">Modo octal
                <input className="ml-2 w-24 rounded-lg border border-panel-border bg-panel-card px-3 py-2 font-mono"
                  value={permissionDraft.mode} placeholder="644" maxLength={4} inputMode="numeric" pattern="0?[0-7]{3}"
                  disabled={!canChangeEntries} autoFocus required onChange={(event) => {
                    changePermissions.reset()
                    setPermissionDraft({ ...permissionDraft, mode: event.target.value })
                  }} />
              </label>
              {changePermissions.isError && <p role="alert" className="text-sm text-red-400">{permissionError}</p>}
              <div className="flex flex-wrap gap-2">
                <button className="button button-primary" disabled={!canChangeEntries || !canApplyPermissions}>
                  {changePermissions.isPending ? 'Aplicando…' : 'Aplicar permissões'}
                </button>
                <button type="button" className="button button-secondary" disabled={changePermissions.isPending}
                  onClick={() => { changePermissions.reset(); setPermissionDraft(null) }}>Cancelar</button>
              </div>
            </form>
          )}
          {canDelete && (deleteEntries.length > 0 || !canList) && (
            <form className="mt-3 space-y-2 rounded-lg border border-panel-border bg-panel-dark p-3" onSubmit={(event) => {
              event.preventDefault()
              if (canChangeEntries && validDeletion) selectForDeletion(deleteEntries, canList ? directory : deleteDirectory)
            }}>
              {!canList && <>
                <label className="block text-sm">Pasta dos itens a excluir (opcional)
                  <input className="mt-1 block w-full rounded-lg border border-panel-border bg-panel-card px-3 py-2"
                    value={deleteDirectory} maxLength={1024} disabled={!canChangeEntries}
                    onChange={(event) => { remove.reset(); setDeleteDirectory(event.target.value) }} />
                </label>
                <label className="block text-sm">Nomes a excluir, um por linha
                  <textarea className="mt-1 block w-full rounded-lg border border-panel-border bg-panel-card px-3 py-2"
                    value={deleteNames} rows={3} maxLength={10240} disabled={!canChangeEntries} required
                    onChange={(event) => { remove.reset(); setDeleteNames(event.target.value) }} />
                </label>
              </>}
              <div className="flex flex-wrap items-center gap-2">
                <button className="button button-danger" disabled={!canChangeEntries || !validDeletion}>Excluir seleção</button>
                {canList && !canArchive && <button type="button" className="button button-secondary" disabled={filesChanging}
                  onClick={() => { remove.reset(); setSelectedEntries([]) }}>Limpar seleção</button>}
              </div>
            </form>
          )}
          {canArchive && (selectedEntries.length > 0 || !canList) && (
            <form className="mt-3 space-y-2 rounded-lg border border-panel-border bg-panel-dark p-3" onSubmit={(event) => {
              event.preventDefault()
              if (!canChangeEntries || !validArchive) return
              setChangeNotice('')
              archive.mutate({ directory: canList ? directory : archiveDirectory, entries: archiveEntries, name: archiveName })
            }}>
              {!canList && <>
                <label className="block text-sm">Pasta de origem (opcional)
                  <input className="mt-1 block w-full rounded-lg border border-panel-border bg-panel-card px-3 py-2"
                    value={archiveDirectory} maxLength={1024} disabled={!canChangeEntries}
                    onChange={(event) => { archive.reset(); setArchiveDirectory(event.target.value) }} />
                </label>
                <label className="block text-sm">Nomes a compactar, um por linha
                  <textarea className="mt-1 block w-full rounded-lg border border-panel-border bg-panel-card px-3 py-2"
                    value={archiveNames} rows={3} maxLength={10240} disabled={!canChangeEntries} required
                    onChange={(event) => { archive.reset(); setArchiveNames(event.target.value) }} />
                </label>
              </>}
              {canList && <p className="text-sm text-gray-400">{archiveEntries.length} {archiveEntries.length === 1 ? 'item selecionado' : 'itens selecionados'}</p>}
              <label className="block text-sm">Nome do arquivo compactado
                <input className="mt-1 block w-full rounded-lg border border-panel-border bg-panel-card px-3 py-2"
                  value={archiveName} maxLength={255} disabled={!canChangeEntries} required
                  onChange={(event) => { archive.reset(); setArchiveName(event.target.value) }} />
              </label>
              <p className="text-sm text-gray-400">Use um nome novo terminado em .tar.gz. Selecione até 40 itens na mesma pasta.</p>
              {archive.isError && <p role="alert" className="text-sm text-red-400">{archiveError}</p>}
              <div className="flex flex-wrap gap-2">
                <button className="button button-primary" disabled={!canChangeEntries || !validArchive}>
                  {archive.isPending ? 'Compactando…' : 'Compactar seleção'}
                </button>
                {canList && <button type="button" className="button button-secondary" disabled={archive.isPending}
                  onClick={() => { archive.reset(); setSelectedEntries([]) }}>Limpar seleção</button>}
              </div>
            </form>
          )}
          {entryChange && (entryChange.kind === 'move' ? canMove : canCreate) && (
            <form key={extraction && !canList ? 'extract-path' : `${entryChange.kind}-${entryChange.source}`}
              className="mt-3 space-y-2 rounded-lg border border-panel-border bg-panel-dark p-3" onSubmit={(event) => {
              event.preventDefault()
              if (canChangeEntries && validDestination) changeEntry.mutate(entryChange)
            }}>
              {extraction && !canList ? <label className="block text-sm">Caminho do arquivo compactado
                <input className="mt-1 block w-full rounded-lg border border-panel-border bg-panel-card px-3 py-2"
                  value={entryChange.source} maxLength={1024} disabled={!canChangeEntries} required
                  onChange={(event) => { changeEntry.reset(); setEntryChange({ ...entryChange, source: event.target.value }) }} />
              </label> : entryChange.kind !== 'directory' && <p className="break-all text-sm text-gray-400">Origem <strong>{entryChange.source}</strong></p>}
              <label className="block text-sm font-medium" htmlFor="file-change-destination">
                {entryChange.kind === 'directory' ? 'Nome da pasta' : extraction ? 'Pasta nova de destino' : 'Caminho de destino'}
              </label>
              <input id="file-change-destination" value={entryChange.destination} maxLength={entryChange.kind === 'directory' ? 255 : 1024}
                className="w-full rounded-lg border border-panel-border bg-panel-card px-3 py-2"
                disabled={!canChangeEntries} autoFocus required onChange={(event) => {
                  changeEntry.reset()
                  setEntryChange({ ...entryChange, destination: event.target.value })
                }} />
              {entryChange.kind !== 'directory' && <p className="text-sm text-gray-400">Use um caminho a partir da pasta principal do jogo.</p>}
              {extraction && <p className="text-sm text-gray-400">Aceita ZIP, TAR, TAR.GZ e TGZ. Escolha uma pasta que ainda não existe.</p>}
              {changeEntry.isError && <p role="alert" className="text-sm text-red-400">{entryError}</p>}
              <div className="flex flex-wrap gap-2">
                <button className="button button-primary" disabled={!canChangeEntries || !validDestination}>
                  {changeEntry.isPending ? 'Salvando…' : entryActions[entryChange.kind].label}
                </button>
                <button type="button" className="button button-secondary" disabled={changeEntry.isPending}
                  onClick={() => { changeEntry.reset(); setEntryChange(null) }}>Cancelar</button>
              </div>
            </form>
          )}
          {canList && (listing.isPending || (cursor !== null && listing.isFetching)) &&
            <p role="status" className="mt-2 text-sm text-gray-400">Carregando arquivos…</p>}
          {canList && listing.isError && <p role="alert" className="mt-2 text-sm text-red-400">
            {directoryChanged ? 'A pasta mudou. Atualize a lista para voltar à primeira página.' : 'Não foi possível listar esta pasta.'}
          </p>}
          {canList && listing.data && showDirectoryPage && (
            <>
              {listing.data.entries.length === 0 && <p className="mt-2 text-sm text-gray-400">
                {nextDirectoryPage ? 'Nenhuma entrada encontrada nesta página.' : filter ? 'Nenhuma entrada encontrada.' : 'Nenhum arquivo ou pasta disponível.'}
              </p>}
              <ul className="mt-2 divide-y divide-panel-border">
                {listing.data.entries.map((entry) => {
                  const relative = directory ? `${directory}/${entry.name}` : entry.name
                  return (
                    <li key={entry.name} className="flex flex-wrap items-center justify-between gap-3 py-2">
                      {canArchive || canDelete || canMove ? <label className="flex min-w-0 items-start gap-2 break-all">
                        <input type="checkbox" className="mt-1" aria-label={`Selecionar ${entry.name}`}
                          checked={selectedEntries.includes(entry.name)}
                          disabled={filesChanging || (!selectedEntries.includes(entry.name) && selectedEntries.length >= maximumSelection)}
                          onChange={(event) => {
                            const selected = event.target.checked
                            archive.reset()
                            remove.reset()
                            setChangeNotice('')
                            setSelectedEntries((previous) => selected ? [...previous, entry.name] : previous.filter((name) => name !== entry.name))
                          }} />{entry.name}
                      </label> : <span className="min-w-0 break-all">{entry.name}</span>}
                      {entry.mode && <code className="text-xs text-gray-400">{entry.mode}</code>}
                      <div className="flex flex-wrap gap-2">
                        {!isPreview && canDownload && canCreate && stopped && !entry.isDirectory && editableFile.test(entry.name) && (
                          <button type="button" className="button button-secondary" disabled={openEditor.isPending || entryChange !== null || filesChanging}
                            aria-label={`Editar ${entry.name}`} onClick={() => selectEditor(relative)}>
                            Editar
                          </button>
                        )}
                        {(entry.isDirectory || canDownload) && <button type="button" className="button button-secondary" aria-label={`${entry.isDirectory ? 'Abrir' : 'Baixar'} ${entry.name}`}
                          disabled={entry.isDirectory ? filesChanging : isPreview || download.isPending || changeEntry.isPending || remove.isPending}
                          onClick={() => entry.isDirectory ? navigateDirectory(relative) : download.mutate(relative)}>
                          {entry.isDirectory ? 'Abrir' : 'Baixar'}
                        </button>}
                        {canMove && <button type="button" className="button button-secondary" aria-label={`Renomear ou mover ${entry.name}`}
                          disabled={!canChangeEntries} onClick={() => selectEntryChange({ kind: 'move', source: relative, destination: relative })}>
                          Renomear ou mover
                        </button>}
                        {canMove && entry.mode !== null && <button type="button" className="button button-secondary" aria-label={`Permissões de ${entry.name}`}
                          disabled={!canChangeEntries || !validEntryName(entry.name)} onClick={() => editPermissions([entry.name], entry.mode ?? '')}>
                          Permissões
                        </button>}
                        {canCreate && !entry.isDirectory && <button type="button" className="button button-secondary" aria-label={`Copiar ${entry.name}`}
                          disabled={!canChangeEntries} onClick={() => selectEntryChange({ kind: 'copy', source: relative, destination: suggestCopyPath(directory, entry.name) })}>
                          Copiar
                        </button>}
                        {canCreate && !entry.isDirectory && archiveFile.test(entry.name) && <button type="button" className="button button-secondary"
                          aria-label={`Extrair ${entry.name}`} disabled={!canChangeEntries}
                          onClick={() => selectEntryChange({ kind: 'extract', source: relative, destination: suggestExtractionPath(directory, entry.name) })}>
                          Extrair
                        </button>}
                        {canDelete && <button type="button" className="button button-secondary" aria-label={`Excluir ${entry.name}`}
                          disabled={!canChangeEntries || !validEntryName(entry.name)} onClick={() => selectForDeletion([entry.name], directory)}>
                          Excluir
                        </button>}
                      </div>
                    </li>
                  )
                })}
              </ul>
              {listing.data.truncated && !nextDirectoryPage && <p className="mt-2 text-sm text-gray-400">Listagem parcial. Use o caminho completo para baixar outros arquivos.</p>}
            </>
          )}
          {canList && (cursor !== null || nextDirectoryPage) && (
            <nav className="mt-3 flex flex-wrap items-center gap-2" aria-label="Páginas da pasta">
              <button type="button" className="button button-secondary" disabled={cursor === null || listing.isFetching || filesChanging}
                onClick={() => setPageCursors((pages) => pages.length > 1 ? pages.slice(0, -1) : pages)}>Página anterior</button>
              <span className="text-sm text-gray-400" aria-live="polite">Página {pageCursors.length}</span>
              <button type="button" className="button button-secondary" disabled={!nextDirectoryPage || listing.isFetching || filesChanging}
                onClick={() => {
                  if (nextDirectoryPage) setPageCursors((pages) => pages[pages.length - 1] === cursor ? [...pages, nextDirectoryPage] : pages)
                }}>Próxima página</button>
            </nav>
          )}
          {!isPreview && canCreate && (
            <form className="mt-4 border-t border-panel-border pt-4" onSubmit={(event) => {
              event.preventDefault()
              if (selectedFile && selectedFile.size <= maximumUploadBytes && !filesChanging)
                upload.mutate({ file: selectedFile, destination: directory })
            }}>
              <label htmlFor="server-upload-file" className="mb-1 block text-sm font-medium">Adicionar arquivo nesta pasta</label>
              <div className="flex flex-wrap items-center gap-2">
                <input ref={uploadInput} type="file" id="server-upload-file" className="max-w-full text-sm"
                  disabled={upload.isPending} onChange={(event) => {
                    upload.reset()
                    setSelectedFile(event.target.files?.[0] ?? null)
                  }} />
                <button className="button button-secondary" disabled={!selectedFile || selectedFile.size > maximumUploadBytes || filesChanging}>
                  {upload.isPending ? 'Enviando…' : 'Enviar arquivo'}
                </button>
                {upload.isPending && <button type="button" className="button button-secondary" onClick={() => uploadAbort.current?.abort()}>
                  Cancelar envio
                </button>}
              </div>
              <p className="mt-1 text-sm text-gray-400">Até 512 MiB por arquivo. Use um nome novo na pasta.</p>
              {selectedFile && selectedFile.size > maximumUploadBytes && <p role="alert" className="text-sm text-red-400">O arquivo excede 512 MiB.</p>}
              {upload.isPending && <div className="mt-2 flex flex-wrap items-center gap-2 text-sm text-gray-300">
                <progress className="w-48 max-w-full" aria-label="Progresso do envio" value={uploadProgress} max={100} />
                <span>{uploadProgress === 100 ? 'Aguardando confirmação do node…' : `${uploadProgress}%`}</span>
              </div>}
              {uploadCanceled && <p role="status" className="text-sm text-gray-300">Envio cancelado.</p>}
              {upload.isError && !uploadCanceled && <p role="alert" className="text-sm text-red-400">{uploadError}</p>}
              {upload.isSuccess && <p role="status" className="text-sm text-gray-300">Arquivo enviado.</p>}
            </form>
          )}
        </section>
      )}
      <ConfirmDialog open={visible && discardPath !== null} pending={false} title="Descartar alterações?"
        description={`As alterações em ${discardPath ?? ''} serão perdidas.`} confirmLabel="Descartar alterações"
        onCancel={() => setDiscardPath(null)} onConfirm={() => { if (discardPath) closeEditor(discardPath) }} />
      <ConfirmDialog open={visible && deleteTarget !== null} pending={remove.isPending} title={deleteTarget?.entries.length === 1
        ? `Excluir ${deleteTarget.entries[0]}?` : `Excluir ${deleteTarget?.entries.length ?? 0} itens?`}
        description={`Os itens selecionados em “${deleteTarget?.directory || 'Pasta do jogo'}” serão excluídos permanentemente. Pastas selecionadas serão excluídas com todo o conteúdo.`}
        confirmLabel="Excluir itens" pendingLabel="Excluindo…" onCancel={() => setDeleteTarget(null)}
        onConfirm={() => { if (deleteTarget && canChangeEntries && canDelete) remove.mutate(deleteTarget); else setDeleteTarget(null) }} />
    </section>
  )
}
