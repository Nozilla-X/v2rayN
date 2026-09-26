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
  loadRouting: () => Promise<void>
  loadOperations: () => Promise<void>
  onCoreUpdateProgress: (progress: Dict) => void
}) {
  const t = options.t
  let eventSource: EventSource | undefined
  let connectionGeneration = 0
  let reconnectTimer: ReturnType<typeof setTimeout> | undefined
  let reconnectAttempts = 0
  let ticketRequestGeneration: number | undefined
  let reconnectNotified = false

  function openEvents() {
    closeEvents()
    if (!options.token.value) return
    void connectEvents(connectionGeneration)
  }

  async function connectEvents(generation: number) {
    if (generation !== connectionGeneration || !options.token.value || ticketRequestGeneration === generation) return
    ticketRequestGeneration = generation
    try {
      const response = await options.request('/api/auth/sse-ticket', { method: 'POST' })
      const ticket = response.data?.ticket
      if (typeof ticket !== 'string' || generation !== connectionGeneration || !options.token.value) return

      const source = new EventSource(`/api/events?sse_ticket=${encodeURIComponent(ticket)}`)
      eventSource = source
      source.onopen = () => {
        if (eventSource === source) {
          reconnectAttempts = 0
          reconnectNotified = false
        }
      }
      source.addEventListener('status', (event) => { options.status.value = JSON.parse((event as MessageEvent).data) })
      source.addEventListener('traffic', (event) => {
      if (options.status.value) options.status.value.traffic = JSON.parse((event as MessageEvent).data)
      })
      source.addEventListener('log', (event) => {
      const entry = JSON.parse((event as MessageEvent).data)
      if (options.activePage.value === 'logs' && options.matchesLogFilter(entry)) {
        options.logTotal.value += 1
        if (options.logPage.value === 1) options.logs.value = [...options.logs.value, entry].slice(-options.logPageSize)
      }
      })
      source.addEventListener('core-update-progress', (event) => {
        const progress = JSON.parse((event as MessageEvent).data) as Dict
        options.onCoreUpdateProgress(progress)
        if (progress.phase === 'checking' || progress.isComplete) {
          void options.loadOperations().catch(() => {})
        }
      })
      for (const eventName of ['profiles-changed', 'subscription-progress', 'speedtest-result', 'settings-changed', 'geo-update-progress', 'geo-update-completed', 'xray-update-completed']) {
        source.addEventListener(eventName, () => {
        if (eventName === 'profiles-changed' || eventName === 'subscription-progress') {
          void options.loadGroups().then(options.loadProfiles).then(options.loadSubscriptions).catch(() => {})
        }
        if (eventName === 'settings-changed') {
          void Promise.all([options.loadStatus(), options.loadRouting()]).catch(() => {})
        }
        if (eventName.includes('update')) void options.loadOperations().catch(() => {})
      })
      }
      source.onerror = () => {
        if (eventSource !== source) return
        source.close()
        eventSource = undefined
        scheduleReconnect(generation)
      }
    } catch {
      scheduleReconnect(generation)
    } finally {
      if (ticketRequestGeneration === generation) ticketRequestGeneration = undefined
    }
  }

  function scheduleReconnect(generation: number) {
    if (generation !== connectionGeneration || !options.token.value || reconnectTimer) return
    const delay = Math.min(1000 * 2 ** Math.min(reconnectAttempts, 5), 30000)
    reconnectAttempts += 1
    if (reconnectAttempts >= 5 && !reconnectNotified) {
      reconnectNotified = true
      options.showNotice(t('common.unknownError'), 'error')
    }
    reconnectTimer = setTimeout(() => {
      reconnectTimer = undefined
      void connectEvents(generation)
    }, delay)
  }

  function closeEvents() {
    connectionGeneration += 1
    clearTimeout(reconnectTimer)
    reconnectTimer = undefined
    reconnectAttempts = 0
    eventSource?.close()
    eventSource = undefined
    ticketRequestGeneration = undefined
    reconnectNotified = false
  }

  onUnmounted(closeEvents)

  return { openEvents, closeEvents }
}
