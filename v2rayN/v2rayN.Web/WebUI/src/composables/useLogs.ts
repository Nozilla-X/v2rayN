import { computed, reactive, ref } from 'vue'
import type { ApiServices, Dict, ErrorHandler, Notice, Translate } from './types'

export function useLogs(options: ApiServices & { t: Translate; showError: ErrorHandler; showNotice: Notice }) {
  const t = options.t
  const logs = ref<Dict[]>([])
  const logFilter = ref('')
  const logPage = ref(1)
  const logTotal = ref(0)
  const logPageSize = 100
  const logTotalPages = computed(() => Math.max(1, Math.ceil(logTotal.value / logPageSize)))

  async function loadLogs(page = 1) {
    try {
      const result = await options.data(options.queryPath('/api/logs/page', { page, pageSize: logPageSize, filter: logFilter.value.trim() }))
      logs.value = result?.items || []
      logPage.value = result?.page || 1
      logTotal.value = result?.total || 0
    }
    catch (error) { options.showError(error) }
  }

  function matchesLogFilter(entry: Dict): boolean {
    const query = logFilter.value.trim()
    if (!query) return true
    try { return new RegExp(query).test(`${entry.source || ''} ${entry.message || ''}`) }
    catch { return false }
  }

  function changeLogPage(page: number) {
    if (page < 1 || page > logTotalPages.value) return
    void loadLogs(page)
  }

  async function clearLogs() {
    if (!window.confirm(t('logs.clearConfirm'))) return
    try {
      const result = await options.request('/api/logs', { method: 'DELETE' })
      logs.value = []
      logPage.value = 1
      logTotal.value = 0
      options.showNotice(options.operationMessage(result))
    } catch (error) { options.showError(error) }
  }

  const logsPageState = reactive({ logTotal, logFilter, logs, logPage, logTotalPages })

  return { logs, logFilter, logPage, logTotal, logPageSize, logTotalPages, matchesLogFilter, loadLogs, logsPageState, logsPageActions: { loadLogs, clearLogs, changeLogPage } }
}
