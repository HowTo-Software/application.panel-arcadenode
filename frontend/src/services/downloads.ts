import api from '@/lib/httpClient'

export function downloadServerFile(serverId: string, path: string) {
  return downloadAttachment(`/servers/${serverId}/files/download`, { path })
}

export function downloadServerBackup(serverId: string, backupId: string) {
  return downloadAttachment(`/servers/${serverId}/backups/${backupId}/download`)
}

async function downloadAttachment(route: string, payload?: { path: string }) {
  const url = new URL(api.getUri({ url: route }), window.location.origin)
  if (url.origin !== window.location.origin) throw new Error('Downloads require the panel origin.')
  const { data } = await api.post<{ ticket: string }>(`${route}-grant`, payload)

  // The browser writes the attachment to disk without buffering it in JavaScript.
  const form = document.createElement('form')
  const input = document.createElement('input')
  form.hidden = true
  form.action = url.href
  form.method = 'post'
  input.type = 'hidden'
  input.name = 'ticket'
  input.value = data.ticket
  form.append(input)
  document.body.append(form)
  try { form.requestSubmit() }
  finally { form.remove() }
}
