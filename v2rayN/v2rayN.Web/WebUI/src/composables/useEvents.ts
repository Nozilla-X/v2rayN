import { onUnmounted, type Ref } from 'vue'
import type { Dict, RequestApi, Notice, Translate } from './types'

export function useEvents(options: {
  token: Ref<string>
  activePage: Ref<string>
  status: Ref<Dict | null>
  logs: Ref<Dict[]>
  logTotal: Ref<number>
  logPage: Ref<number>
  logPageSize: number
  request: RequestApi
  t: Translate
  showNotice: Notice
  matchesLogFilter: (entry: Dict) => boolean
  loadGroups: () => Promise<void>
  loadProfiles: () => Promise<void>
  loadSubscriptions: () => Promise<void>
  loadStatus: () => Promise<void>
  loadOperations: () => Promise<void>
}) {
  const t = options.t
  let eventSource: EventSource | undefined
  let sessionValidationInFlight = false

  function openEvents() {
    closeEvents()
    if (!options.token.value) return
    eventSource = new EventSource(`/api/events?access_token=${encodeURIComponent(options.token.value)}`)
    eventSource.addEventListener('status', (event) => { options.status.value = JSON.parse((event as MessageEvent).data) })
    eventSource.addEventListener('traffic', (event) => {
      if (options.status.value) options.status.value.traffic = JSON.parse((event as MessageEvent).data)
    })
    eventSource.addEventListener('log', (event) => {
      const entry = JSON.parse((event as MessageEvent).data)
      if (options.activePage.value === 'logs' && options.matchesLogFilter(entry)) {
        options.logTotal.value += 1
        if (options.logPage.value === 1) options.logs.value = [...options.logs.value, entry].slice(-options.logPageSize)
      }
    })
    for (const eventName of ['profiles-changed', 'subscription-progress', 'speedtest-result', 'settings-changed', 'geo-update-progress', 'geo-update-completed', 'xray-update-completed']) {
      eventSource.addEventListener(eventName, () => {
        if (eventName === 'profiles-changed' || eventName === 'subscription-progress') {
          void options.loadGroups().then(options.loadProfiles).then(options.loadSubscriptions)
        }
        if (eventName === 'settings-changed') void options.loadStatus()
        if (eventName.includes('update')) void options.loadOperations()
      })
    }
    eventSource.onerror = () => {
      if (!options.token.value || sessionValidationInFlight) return
      sessionValidationInFlight = true
      void options.request('/api/status')
        .catch(() => {
          if (options.token.value && eventSource?.readyState === EventSource.CLOSED) {
            options.showNotice(t('common.unknownError'), 'error')
          }
        })
        .finally(() => { sessionValidationInFlight = false })
    }
  }

  function closeEvents() {
    eventSource?.close()
    eventSource = undefined
  }

  onUnmounted(closeEvents)

  return { openEvents, closeEvents }
}
