import { reactive, ref, type Ref } from 'vue'
import type { ApiError, ApiServices, Dict, ErrorHandler, Notice, Translate } from './types'

export function useMaintenance(options: ApiServices & {
  t: Translate
  translateKey: (key?: string | null) => string
  showNotice: Notice
  showError: ErrorHandler
  confirm: (message: string) => Promise<boolean>
  token: Ref<string>
  status: Ref<Dict | null>
  operations: Ref<string[]>
  loadOperations: () => Promise<void>
  loadProfiles: () => Promise<void>
}) {
  const t = options.t
  const webdavForm = ref<Dict>({ url: '', userName: '', password: '', dirName: '' })
  const xrayUpdate = ref<Dict>({ selected: true, preRelease: false, useProxy: true, result: null })

  async function loadMaintenance() {
    const webdav = await options.data('/api/settings/webdav')
    webdavForm.value = { ...webdav, password: '' }
    await options.loadOperations()
  }

  async function checkXrayUpdate() {
    try {
      const result = await options.request(options.queryPath('/api/core/xray/check-update', { preRelease: xrayUpdate.value.preRelease, useProxy: xrayUpdate.value.useProxy }))
      xrayUpdate.value.result = { ...(result.data || {}), messageKey: result.messageKey }
      options.showNotice(options.translateKey(result.messageKey || (xrayUpdate.value.result.updateAvailable ? 'core.updateAvailable' : 'core.updateCurrent')))
    } catch (error) { options.showError(error) }
  }

  async function updateXray() {
    try {
      const result = await options.request(options.queryPath('/api/core/xray/update', { preRelease: xrayUpdate.value.preRelease, useProxy: xrayUpdate.value.useProxy }), { method: 'POST' })
      options.showNotice(options.operationMessage(result, 'core.updateStarted'))
      await options.loadOperations()
    } catch (error) { options.showError(error) }
  }

  async function updateGeo() {
    try {
      const result = await options.request(`/api/core/geo/update?useProxy=${xrayUpdate.value.useProxy}`, { method: 'POST' })
      options.showNotice(options.operationMessage(result, 'updates.geoStarted'))
      await options.loadOperations()
    } catch (error) { options.showError(error) }
  }

  async function saveWebdav() {
    try {
      const result = await options.request('/api/settings/webdav', { method: 'PUT', body: {
        url: webdavForm.value.url, userName: webdavForm.value.userName,
        password: webdavForm.value.password || null, dirName: webdavForm.value.dirName,
      } })
      webdavForm.value.password = ''
      options.showNotice(options.operationMessage(result))
    } catch (error) { options.showError(error) }
  }

  async function webdavAction(action: 'check' | 'backup' | 'restore') {
    try {
      if (action === 'restore' && !await options.confirm(t('maintenance.restoreConfirm'))) return
      const result = await options.request(`/api/backup/webdav${action === 'check' ? '/check' : action === 'restore' ? '/restore' : ''}`, { method: 'POST' })
      options.showNotice(options.operationMessage(result))
    } catch (error) { options.showError(error) }
  }

  async function downloadBackup() {
    try {
      const response = await fetch('/api/backup/download', { headers: { Authorization: `Bearer ${options.token.value}` } })
      if (!response.ok) {
        const payload = await response.json().catch(() => null)
        const issue = new Error(options.translateKey(payload?.messageKey)) as ApiError
        issue.messageKey = payload?.messageKey
        throw issue
      }
      const blob = await response.blob()
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = response.headers.get('Content-Disposition')?.match(/filename="?([^";]+)"?/i)?.[1] || 'v2rayN-backup.zip'
      link.click()
      URL.revokeObjectURL(url)
    } catch (error) { options.showError(error) }
  }

  async function uploadRestore(event: Event) {
    const input = event.target as HTMLInputElement
    const file = input.files?.[0]
    if (!file) return
    if (!await options.confirm(t('maintenance.restoreConfirm'))) return
    try {
      const formData = new FormData()
      formData.set('file', file)
      const result = await options.request('/api/backup/restore', { method: 'POST', body: formData })
      options.showNotice(options.operationMessage(result))
    } catch (error) { options.showError(error) } finally { input.value = '' }
  }

  async function clearStatistics() {
    if (!await options.confirm(t('maintenance.clearConfirm'))) return
    try {
      const result = await options.request('/api/statistics', { method: 'DELETE' })
      options.showNotice(options.operationMessage(result))
      await options.loadProfiles()
    } catch (error) { options.showError(error) }
  }

  const maintenancePageState = reactive({ xrayUpdate, operations: options.operations, status: options.status, webdavForm })

  return {
    webdavForm, xrayUpdate, loadMaintenance, maintenancePageState,
    maintenancePageActions: { checkXrayUpdate, updateXray, updateGeo, clearStatistics, saveWebdav, webdavAction, downloadBackup, uploadRestore, loadMaintenance, loadOperations: options.loadOperations },
  }
}
