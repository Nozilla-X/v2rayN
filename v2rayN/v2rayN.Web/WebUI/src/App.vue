<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'

type Dict = Record<string, any>
type ApiError = Error & { messageKey?: string; code?: string }
type ApiInit = Omit<RequestInit, 'body'> & { body?: unknown }

const { t, locale } = useI18n()
const token = ref(localStorage.getItem('v2rayn-web-token') || '')
const managementKeyDraft = ref('')
const setupStatusReady = ref(false)
const setupRequired = ref(false)
const setupAllowedFromRequest = ref(false)
const setupKey = ref('')
const setupConfirmKey = ref('')
const setupSubmitting = ref(false)
const setupError = ref('')
const authenticated = ref(false)
const loading = ref(false)
const busy = ref(false)
const status = ref<Dict | null>(null)
const profiles = ref<Dict[]>([])
const groups = ref<Dict[]>([])
const subscriptions = ref<Dict[]>([])
const activePage = ref('nodes')
const selectedGroup = ref('')
const filter = ref('')
const selectedIds = ref<string[]>([])
const sorting = ref({ column: '', ascending: true })
const activeRoutingId = ref('')
const routes = ref<Dict[]>([])
const routingRules = ref<Dict[]>([])
const rulesRaw = ref('[]')
const ruleImportText = ref('')
const appendRules = ref(false)
const simpleDnsRaw = ref('{}')
const dnsProfiles = ref<Dict[]>([])
const templates = ref<Dict[]>([])
const settings = ref<Dict>({})
const inboundForm = ref<Dict>({})
const coreForm = ref<Dict>({})
const appForm = ref<Dict>({})
const speedForm = ref<Dict>({})
const routingForm = ref<Dict>({})
const webdavForm = ref<Dict>({ url: '', userName: '', password: '', dirName: '' })
const xrayUpdate = ref<Dict>({ preRelease: false, useProxy: true, result: null })
const operations = ref<string[]>([])
const logs = ref<Dict[]>([])
const logFilter = ref('')
const logPage = ref(1)
const logTotal = ref(0)
const logPageSize = 100
const importForm = ref<Dict>({ content: '', subscriptionId: '', isSubscription: false })
const profileForm = ref<Dict>({})
const profileAdvancedJson = ref('')
const exportOptions = ref({ includeShareUris: true, base64ShareUris: false, includeInnerUri: true, includeClientConfig: true })
const subscriptionForm = ref<Dict>({})
const routeForm = ref<Dict>({})
const exportContent = ref('')
const notice = ref('')
const noticeKind = ref<'success' | 'error'>('success')
const showProfileForm = ref(false)
const showImportForm = ref(false)
const showSubscriptionForm = ref(false)
const showRouteForm = ref(false)
const showExportDialog = ref(false)
const editingProfileId = ref('')
const editingSubscriptionId = ref('')
const editingRouteId = ref('')
const contextMenu = ref<Dict | null>(null)
const profileModalError = ref('')
const subscriptionUseProxy = ref(false)
const logPanel = ref<HTMLElement | null>(null)
let refreshTimer: ReturnType<typeof setInterval> | undefined
let noticeTimer: ReturnType<typeof setTimeout> | undefined
let eventSource: EventSource | undefined
let sessionValidationInFlight = false

const navItems = [
  { id: 'nodes', key: 'nav.nodes', icon: '▦' },
  { id: 'subscriptions', key: 'nav.subscriptions', icon: '↻' },
  { id: 'routing', key: 'nav.routing', icon: '⇄' },
  { id: 'dns', key: 'nav.dns', icon: '⌘' },
  { id: 'settings', key: 'nav.settings', icon: '⚙' },
  { id: 'templates', key: 'nav.templates', icon: '≡' },
  { id: 'maintenance', key: 'nav.maintenance', icon: '⇩' },
  { id: 'logs', key: 'nav.logs', icon: '▤' },
]
const protocolTypes = ['VMess', 'VLESS', 'Shadowsocks', 'SOCKS', 'Trojan', 'Hysteria2', 'TUIC', 'WireGuard', 'HTTP', 'Anytls', 'Naive']
const coreTypes = ['Xray', 'sing_box', 'v2fly', 'v2fly_v5', 'mihomo', 'hysteria', 'naiveproxy', 'tuic', 'juicity', 'brook', 'overtls', 'shadowquic', 'mieru']
const testActions = [
  { id: 'tcping', key: 'nodes.tcping' },
  { id: 'realping', key: 'nodes.realping' },
  { id: 'fastRealping', key: 'nodes.fastRealping' },
  { id: 'udpTest', key: 'nodes.udp' },
  { id: 'speedtest', key: 'nodes.speedtest' },
  { id: 'mixedtest', key: 'nodes.mixedtest' },
]

const currentProfile = computed(() => profiles.value.find((profile) => profile.isCurrent) || null)
// The backend owns filtering so its ServiceLib regex semantics are preserved.
const filteredProfiles = computed(() => profiles.value)
const selectedProfiles = computed(() => profiles.value.filter((profile) => selectedIds.value.includes(profile.indexId)))
const allVisibleSelected = computed(() => filteredProfiles.value.length > 0 && filteredProfiles.value.every((profile) => selectedIds.value.includes(profile.indexId)))
const listeners = computed(() => status.value?.listeners || [])
const currentRoute = computed(() => routes.value.find((route) => route.isActive) || null)
const logTotalPages = computed(() => Math.max(1, Math.ceil(logTotal.value / logPageSize)))
const runtimeVersion = computed(() => status.value?.runtime?.split('|')[0]?.trim() || '')
const brandIconMode = computed(() => status.value?.coreRunning ? 'proxy' : 'off')
const brandIconSrc = computed(() => ({ proxy: '/NotifyIcon2.ico', off: '/NotifyIcon1.ico' })[brandIconMode.value])
const brandIconTitle = computed(() => t(`brandState.${brandIconMode.value}`))
const pageTitle = computed(() => {
  const item = navItems.find((entry) => entry.id === activePage.value)
  return item ? t(item.key) : t('nav.nodes')
})
const traffic = computed(() => status.value?.traffic || {})
function translateKey(key?: string | null): string {
  if (!key) return t('common.operationDone')
  const translated = t(key)
  return translated === key ? key : translated
}

function showNotice(message: string, kind: 'success' | 'error' = 'success') {
  notice.value = message
  noticeKind.value = kind
  clearTimeout(noticeTimer)
  noticeTimer = setTimeout(() => { notice.value = '' }, 4000)
}

function showError(error: unknown) {
  const issue = error as ApiError
  const message = issue.code === 'profile_group_empty'
    ? t('nodes.groupGenerationEmpty')
    : issue.messageKey ? translateKey(issue.messageKey) : issue.message || t('common.unknownError')
  showNotice(message, 'error')
}

async function request(path: string, init: ApiInit = {}): Promise<Dict> {
  const headers = new Headers(init.headers)
  if (token.value) headers.set('Authorization', `Bearer ${token.value}`)
  let body: BodyInit | undefined
  if (init.body && typeof init.body === 'object' && !(init.body instanceof FormData) && !(init.body instanceof Blob)) {
    headers.set('Content-Type', 'application/json')
    body = JSON.stringify(init.body)
  } else if (init.body !== undefined && init.body !== null) {
    body = init.body as BodyInit
  }
  const response = await fetch(path, { ...init, headers, body })
  if (response.status === 401) {
    clearSession()
  }
  const payload = response.status === 204 ? null : await response.json().catch(() => null)
  if (!response.ok || payload?.success === false) {
    const error = new Error(translateKey(payload?.messageKey) || payload?.code || `${response.status}`) as ApiError
    error.messageKey = payload?.messageKey
    error.code = payload?.code
    throw error
  }
  return payload || {}
}

async function data(path: string, init: ApiInit = {}): Promise<any> {
  const payload = await request(path, init)
  return payload && typeof payload === 'object' && 'data' in payload ? payload.data : payload
}

function operationMessage(payload: Dict, fallback = 'common.operationDone') {
  return translateKey(payload?.messageKey || fallback)
}

function queryPath(path: string, values: Dict): string {
  const query = new URLSearchParams()
  Object.entries(values).forEach(([key, value]) => {
    if (value !== undefined && value !== null && value !== '') query.set(key, String(value))
  })
  return query.size ? `${path}?${query}` : path
}

function canonicalCode(value: unknown, codes: string[]): string {
  const match = codes.find((code) => code.toLocaleLowerCase() === String(value ?? '').toLocaleLowerCase())
  return match || String(value ?? '')
}

function coreTypeRoute(value: string): string {
  return value.toLocaleLowerCase() === 'xray' ? 'Xray' : value
}

async function loadStatus() {
  status.value = await data('/api/status')
  const operationRows = await data('/api/operations')
  operations.value = Array.isArray(operationRows) ? operationRows : []
}

async function loadGroups() {
  groups.value = await data('/api/profile-groups') || []
  if (!groups.value.some((group) => group.id === selectedGroup.value)) {
    selectedGroup.value = groups.value.find((group) => group.isCurrent)?.id || ''
  }
}

async function loadProfiles() {
  const path = queryPath('/api/profiles', { subscriptionId: selectedGroup.value, filter: filter.value.trim() })
  profiles.value = await data(path) || []
  selectedIds.value = selectedIds.value.filter((id) => profiles.value.some((profile) => profile.indexId === id))
}

async function loadSubscriptions() {
  subscriptions.value = await data('/api/subscriptions') || []
}

async function refreshBase() {
  if (!token.value || loading.value) return
  loading.value = true
  try {
    await loadGroups()
    await Promise.all([loadStatus(), loadProfiles(), loadSubscriptions()])
    authenticated.value = true
  } catch (error) {
    if (authenticated.value) showError(error)
    else throw error
  } finally {
    loading.value = false
  }
}

async function login() {
  const managementKey = managementKeyDraft.value
  if (!managementKey) {
    showNotice(t('auth.tokenRequired'), 'error')
    return
  }

  try {
    const response = await fetch('/api/auth/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ key: managementKey }),
    })
    const payload = await response.json().catch(() => ({}))
    if (response.status === 429) {
      showNotice(t('auth.rateLimited'), 'error')
      return
    }
    if (!response.ok || payload?.success !== true || !payload?.data?.token) {
      showNotice(t('auth.loginFailed'), 'error')
      return
    }

    managementKeyDraft.value = ''
    await connectWithSession(payload.data.token)
  } catch {
    showNotice(t('auth.connectFailed'), 'error')
  }
}

async function connectWithSession(sessionToken: string) {
  token.value = sessionToken
  try {
    await refreshBase()
    if (authenticated.value) {
      localStorage.setItem('v2rayn-web-token', sessionToken)
      openEvents()
      await Promise.all([loadSettings(), loadRouting(), loadPageData()])
      await loadLogs()
      showNotice(t('auth.connected'))
    }
  } catch (error) {
    clearSession()
    showError(error)
    if (!notice.value) showNotice(t('auth.connectFailed'), 'error')
  }
}

async function configureManagementKey() {
  setupError.value = ''
  if (setupKey.value.length < 12) {
    setupError.value = t('setup.keyTooShort')
    return
  }
  if (setupKey.value.length > 4096) {
    setupError.value = t('setup.keyTooLong')
    return
  }
  if (setupKey.value !== setupConfirmKey.value) {
    setupError.value = t('setup.keysDoNotMatch')
    return
  }

  setupSubmitting.value = true
  try {
    const response = await fetch('/api/setup', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ key: setupKey.value, confirmKey: setupConfirmKey.value }),
    })
    const payload = await response.json().catch(() => ({}))
    if (!response.ok) {
      setupError.value = payload.error === 'key_too_short'
        ? t('setup.keyTooShort')
        : payload.error === 'key_too_long'
          ? t('setup.keyTooLong')
        : payload.error === 'keys_do_not_match'
          ? t('setup.keysDoNotMatch')
          : payload.error === 'already_configured'
            ? t('setup.alreadyConfigured')
            : t('setup.setupFailed')
      return
    }

    setupRequired.value = false
    const sessionToken = payload.token
    setupKey.value = ''
    setupConfirmKey.value = ''
    if (!sessionToken) {
      setupError.value = t('setup.setupFailed')
      return
    }
    await connectWithSession(sessionToken)
  } catch {
    setupError.value = t('setup.setupFailed')
  } finally {
    setupSubmitting.value = false
  }
}

function clearSession() {
  closeEvents()
  token.value = ''
  localStorage.removeItem('v2rayn-web-token')
  authenticated.value = false
  status.value = null
  profiles.value = []
  groups.value = []
  subscriptions.value = []
}

async function disconnect() {
  try {
    await request('/api/auth/logout', { method: 'POST' })
  } catch {
    // Clear the local session even when the backend is already unavailable.
  }
  clearSession()
}

function openEvents() {
  closeEvents()
  if (!token.value) return
  eventSource = new EventSource(`/api/events?access_token=${encodeURIComponent(token.value)}`)
  eventSource.addEventListener('status', (event) => { status.value = JSON.parse((event as MessageEvent).data) })
  eventSource.addEventListener('traffic', (event) => {
    if (status.value) status.value.traffic = JSON.parse((event as MessageEvent).data)
  })
  eventSource.addEventListener('log', (event) => {
    const entry = JSON.parse((event as MessageEvent).data)
    if (activePage.value === 'logs' && matchesLogFilter(entry)) {
      logTotal.value += 1
      if (logPage.value === 1) logs.value = [...logs.value, entry].slice(-logPageSize)
    }
  })
  for (const eventName of ['profiles-changed', 'subscription-progress', 'speedtest-result', 'settings-changed', 'geo-update-progress', 'geo-update-completed', 'xray-update-completed']) {
    eventSource.addEventListener(eventName, () => {
      if (eventName === 'profiles-changed' || eventName === 'subscription-progress') {
        void loadGroups().then(loadProfiles).then(loadSubscriptions)
      }
      if (eventName === 'settings-changed') void loadStatus()
      if (eventName.includes('update')) void loadOperations()
    })
  }
  eventSource.onerror = () => {
    if (!token.value || sessionValidationInFlight) return
    sessionValidationInFlight = true
    void request('/api/status')
      .catch(() => {
        if (token.value && eventSource?.readyState === EventSource.CLOSED) {
          showNotice(t('common.unknownError'), 'error')
        }
      })
      .finally(() => { sessionValidationInFlight = false })
  }
}

function closeEvents() {
  eventSource?.close()
  eventSource = undefined
}

async function loadOperations() {
  operations.value = await data('/api/operations') || []
}

async function loadSettings() {
  settings.value = await data('/api/settings') || {}
  settings.value.coreTypes = (settings.value.coreTypes || []).map((mapping: Dict) => ({
    ...mapping,
    coreType: canonicalCode(mapping.coreType, coreTypes),
  }))
  const inb = settings.value.inbound || {}
  inboundForm.value = { ...inb, destOverrideText: (inb.destOverride || []).join('\n') }
  const core = settings.value.core || {}
  coreForm.value = {
    ...core,
    fragmentLengthsText: (core.fragmentLengths || []).join('\n'),
    fragmentDelaysText: (core.fragmentDelays || []).join('\n'),
  }
  appForm.value = { ...(settings.value.app || {}) }
  speedForm.value = { ...(settings.value.speedTest || {}) }
  routingForm.value = {
    domainStrategy: settings.value.domainStrategy || '',
    domainStrategy4Singbox: settings.value.domainStrategy4Singbox || '',
  }
}

async function loadRouting() {
  routes.value = await data('/api/settings/routing-profiles') || []
  activeRoutingId.value = routes.value.find((item) => item.isActive)?.id || ''
  if (routes.value.length && !routes.value.some((item) => item.id === activeRoutingId.value)) activeRoutingId.value = routes.value[0].id
  if (activeRoutingId.value) await loadRules(activeRoutingId.value)
}

async function loadRules(id = activeRoutingId.value) {
  if (!id) {
    routingRules.value = []
    rulesRaw.value = '[]'
    return
  }
  routingRules.value = await data(`/api/settings/routing-profiles/${encodeURIComponent(id)}/rules`) || []
  rulesRaw.value = JSON.stringify(routingRules.value, null, 2)
}

async function loadDns() {
  const [simple, profilesResult] = await Promise.all([
    data('/api/settings/dns/simple'),
    data('/api/settings/dns/profiles'),
  ])
  simpleDnsRaw.value = JSON.stringify(simple || {}, null, 2)
  dnsProfiles.value = profilesResult || []
}

async function loadTemplates() {
  templates.value = await data('/api/settings/core-templates') || []
}

async function loadMaintenance() {
  const webdav = await data('/api/settings/webdav')
  webdavForm.value = { ...webdav, password: '' }
  await loadOperations()
}

async function loadPageData() {
  if (activePage.value === 'routing') await loadRouting()
  if (activePage.value === 'dns') await loadDns()
  if (activePage.value === 'settings') await loadSettings()
  if (activePage.value === 'templates') await loadTemplates()
  if (activePage.value === 'maintenance') await loadMaintenance()
  if (activePage.value === 'logs') await loadLogs()
}

async function navigate(page: string) {
  activePage.value = page
  contextMenu.value = null
  selectedIds.value = []
}

async function changeGroup(groupId: string) {
  try {
    await request('/api/profile-groups/current', { method: 'PUT', body: { subscriptionId: groupId || null } })
    selectedGroup.value = groupId
    selectedIds.value = []
    await loadProfiles()
  } catch (error) { showError(error) }
}

async function coreAction(action: 'start' | 'stop' | 'restart') {
  busy.value = true
  try {
    const result = await request(`/api/core/${action}`, { method: 'POST' })
    showNotice(operationMessage(result, `core.${action === 'start' ? 'started' : action === 'stop' ? 'stopped' : 'restarted'}`))
    await loadStatus()
  } catch (error) { showError(error) } finally { busy.value = false }
}

async function selectProfile(profile: Dict) {
  if (profile.isCurrent) return
  busy.value = true
  try {
    const result = await request(`/api/profiles/${encodeURIComponent(profile.indexId)}/select`, { method: 'POST' })
    showNotice(operationMessage(result, 'core.started'))
    await Promise.all([loadStatus(), loadProfiles()])
  } catch (error) { showError(error) } finally { busy.value = false }
}

async function startSpeedTest(action: string, ids: string[] = selectedIds.value) {
  try {
    const result = await request('/api/speedtests', {
      method: 'POST',
      body: { action, ...(ids.length ? { profileIds: ids } : {}) },
    })
    showNotice(operationMessage(result, 'speedtest.started'))
    await loadOperations()
  } catch (error) { showError(error) }
}

async function stopSpeedTests() {
  try {
    const result = await request('/api/speedtests', { method: 'DELETE' })
    showNotice(operationMessage(result))
    await loadOperations()
  } catch (error) { showError(error) }
}

function toggleProfile(id: string) {
  selectedIds.value = selectedIds.value.includes(id) ? selectedIds.value.filter((item) => item !== id) : [...selectedIds.value, id]
}

function toggleAllVisible() {
  if (allVisibleSelected.value) {
    const visible = new Set(filteredProfiles.value.map((profile) => profile.indexId))
    selectedIds.value = selectedIds.value.filter((id) => !visible.has(id))
  } else {
    selectedIds.value = [...new Set([...selectedIds.value, ...filteredProfiles.value.map((profile) => profile.indexId)])]
  }
}

async function sortProfiles(column: string) {
  const ascending = sorting.value.column === column ? !sorting.value.ascending : true
  sorting.value = { column, ascending }
  try {
    await request('/api/profiles/sort', { method: 'POST', body: { subscriptionId: selectedGroup.value || null, column, ascending } })
    await loadProfiles()
  } catch (error) { showError(error) }
}

async function runProfileAction(action: string, profileIds = selectedIds.value) {
  if (!profileIds.length && !['test-group', 'deduplicate', 'remove-invalid'].includes(action)) {
    showNotice(t('nodes.selectionRequired'), 'error')
    return
  }
  try {
    let result: Dict
    if (action === 'delete') {
      if (!window.confirm(t('common.confirmDelete'))) return
      result = await request('/api/profiles', { method: 'DELETE', body: { profileIds } })
    } else if (action === 'copy') {
      result = await request('/api/profiles/copy', { method: 'POST', body: { profileIds } })
    } else if (action === 'deduplicate') {
      result = await request(queryPath('/api/profiles/deduplicate', { subscriptionId: selectedGroup.value }), { method: 'POST' })
    } else if (action === 'remove-invalid') {
      result = await request(queryPath('/api/profiles/invalid-test-results', { subscriptionId: selectedGroup.value }), { method: 'DELETE' })
    } else if (action === 'test-group') {
      await startSpeedTest('mixedtest', [])
      return
    } else {
      return
    }
    showNotice(operationMessage(result))
    selectedIds.value = []
    await Promise.all([loadGroups(), loadProfiles(), loadStatus()])
  } catch (error) { showError(error) }
}

async function moveSelectedToGroup(subscriptionId: string) {
  if (!selectedIds.value.length) return showNotice(t('nodes.selectionRequired'), 'error')
  try {
    const result = await request('/api/profiles/move-to-group', { method: 'POST', body: { profileIds: selectedIds.value, subscriptionId } })
    showNotice(operationMessage(result))
    await Promise.all([loadGroups(), loadProfiles()])
    selectedIds.value = []
  } catch (error) { showError(error) }
}

async function moveSelected(direction: string) {
  if (!selectedIds.value.length) return showNotice(t('nodes.selectionRequired'), 'error')
  const ordered = [...selectedProfiles.value]
  if (direction === 'up' || direction === 'top') ordered.reverse()
  try {
    for (const profile of ordered) {
      await request('/api/profiles/move', { method: 'POST', body: { profileId: profile.indexId, direction, position: -1 } })
    }
    await loadProfiles()
  } catch (error) { showError(error) }
}

async function moveSelectedPosition() {
  if (!selectedIds.value.length) return showNotice(t('nodes.selectionRequired'), 'error')
  const value = window.prompt(t('nodes.positionPrompt'))
  if (value === null) return
  const position = Number.parseInt(value, 10)
  if (!Number.isInteger(position) || position < 1) return showNotice(t('errors.invalidInput'), 'error')
  try {
    for (const profile of selectedProfiles.value) {
      await request('/api/profiles/move', { method: 'POST', body: { profileId: profile.indexId, direction: 'position', position: position - 1 } })
    }
    await loadProfiles()
  } catch (error) { showError(error) }
}

async function generateGroups(byRegion: boolean) {
  if (!selectedGroup.value) {
    showNotice(t('nodes.groupGenerationSelectSubscription'), 'error')
    return
  }
  try {
    const route = byRegion ? '/api/profile-groups/generate/regions' : '/api/profile-groups/generate/all'
    const result = await request(queryPath(route, { subscriptionId: selectedGroup.value || null }), { method: 'POST' })
    showNotice(operationMessage(result, 'profiles.grouped'))
    await Promise.all([loadGroups(), loadProfiles()])
  } catch (error) {
    const issue = error as ApiError
    if (byRegion && issue.code === 'profile_group_empty') showNotice(t('nodes.regionGroupsNoMatches'), 'error')
    else showError(error)
  }
}

async function exportSelected() {
  if (!selectedIds.value.length) return showNotice(t('nodes.selectionRequired'), 'error')
  try {
    const result = await data('/api/profiles/export', {
      method: 'POST',
      body: { profileIds: selectedIds.value, ...exportOptions.value },
    })
    exportContent.value = (result || []).map((item: Dict) => `# ${item.format}${item.remarks ? ` · ${item.remarks}` : ''}\n${item.content}`).join('\n\n')
    showExportDialog.value = true
  } catch (error) { showError(error) }
}

async function copyExport() {
  try {
    await navigator.clipboard.writeText(exportContent.value)
    showNotice(t('common.copySuccess'))
  } catch { showNotice(t('common.clipboardUnavailable'), 'error') }
}

function downloadExport() {
  const blob = new Blob([exportContent.value], { type: 'text/plain;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = 'v2rayN-profiles.txt'
  link.click()
  URL.revokeObjectURL(url)
}

function openContext(event: MouseEvent, profile: Dict) {
  event.preventDefault()
  if (!selectedIds.value.includes(profile.indexId)) selectedIds.value = [profile.indexId]
  contextMenu.value = { x: Math.min(event.clientX, window.innerWidth - 250), y: Math.min(event.clientY, window.innerHeight - 280), profile }
}

async function openAddProfile() {
  editingProfileId.value = ''
  profileModalError.value = ''
  profileForm.value = {
    configType: 'VMess', coreType: 'Xray', configVersion: 4, remarks: '', address: '', port: 443,
    password: '', username: '', network: 'tcp', streamSecurity: 'tls', allowInsecure: '', sni: '',
    alpn: '', fingerprint: '', publicKey: '', shortId: '', spiderX: '', muxEnabled: null,
    protoExtra: '{}', transportExtra: '{}', protoExtraText: '{\n  \n}', transportExtraText: '{\n  \n}',
  }
  profileAdvancedJson.value = JSON.stringify({
    indexId: '', configType: 'VMess', coreType: 'Xray', configVersion: 4, subid: '', isSub: false,
    remarks: '', address: '', port: 443, password: '', username: '', network: 'tcp', streamSecurity: 'tls',
    allowInsecure: '', sni: '', alpn: '', fingerprint: '', publicKey: '', shortId: '', spiderX: '',
    protoExtra: '{}', transportExtra: '{}',
  }, null, 2)
  showProfileForm.value = true
}

async function openEditProfile(profile: Dict) {
  editingProfileId.value = profile.indexId
  profileModalError.value = ''
  try {
    const details = await data(`/api/profiles/${encodeURIComponent(profile.indexId)}`)
    profileForm.value = {
      ...details,
      configType: canonicalCode(details.configType, [...protocolTypes, 'PolicyGroup', 'ProxyChain']),
      coreType: canonicalCode(details.coreType || profile.coreType, coreTypes),
      allowInsecure: details.allowInsecure === 'true',
      protoExtraText: details.protoExtra || '{}',
      transportExtraText: details.transportExtra || '{}',
    }
    profileAdvancedJson.value = JSON.stringify(details, null, 2)
    showProfileForm.value = true
  } catch (error) { showError(error) }
}

async function saveProfile() {
  try {
    const protoExtra = JSON.parse(profileForm.value.protoExtraText || '{}')
    const transportExtra = JSON.parse(profileForm.value.transportExtraText || '{}')
    const advanced = JSON.parse(profileAdvancedJson.value || '{}')
    const body = {
      ...advanced,
      configType: profileForm.value.configType,
      coreType: profileForm.value.coreType || null,
      configVersion: Number(profileForm.value.configVersion || 4),
      remarks: profileForm.value.remarks,
      address: profileForm.value.address,
      port: Number(profileForm.value.port || 0),
      password: profileForm.value.password || '',
      username: profileForm.value.username || '',
      network: profileForm.value.network || '',
      streamSecurity: profileForm.value.streamSecurity || '',
      allowInsecure: profileForm.value.allowInsecure ? 'true' : '',
      sni: profileForm.value.sni || '',
      alpn: profileForm.value.alpn || '',
      fingerprint: profileForm.value.fingerprint || '',
      publicKey: profileForm.value.publicKey || '',
      shortId: profileForm.value.shortId || '',
      spiderX: profileForm.value.spiderX || '',
      muxEnabled: profileForm.value.muxEnabled,
      protoExtra: JSON.stringify(protoExtra),
      transportExtra: JSON.stringify(transportExtra),
    }
    const result = await request(editingProfileId.value ? `/api/profiles/${encodeURIComponent(editingProfileId.value)}` : '/api/profiles', {
      method: editingProfileId.value ? 'PUT' : 'POST', body,
    })
    showProfileForm.value = false
    showNotice(operationMessage(result, 'profiles.saved'))
    await Promise.all([loadGroups(), loadProfiles(), loadStatus()])
  } catch (error) {
    if (error instanceof SyntaxError) profileModalError.value = t('common.invalidJson')
    else showError(error)
  }
}

function openImportProfiles() {
  importForm.value = { content: '', subscriptionId: selectedGroup.value || '', isSubscription: false }
  showImportForm.value = true
}

async function pasteImport() {
  try { importForm.value.content = await navigator.clipboard.readText() } catch { showNotice(t('common.clipboardUnavailable'), 'error') }
}

async function readImportFile(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (file) importForm.value.content = await file.text()
  input.value = ''
}

async function importProfiles() {
  try {
    const result = await request('/api/profiles/import', { method: 'POST', body: importForm.value })
    const count = result.data?.imported ?? 0
    showImportForm.value = false
    showNotice(t('nodes.profilesImported', { count }))
    await Promise.all([loadGroups(), loadProfiles()])
  } catch (error) { showError(error) }
}

function openAddSubscription() {
  editingSubscriptionId.value = ''
  subscriptionForm.value = {
    remarks: '', url: '', moreUrl: '', enabled: true, userAgent: '', requestHeaders: '', filter: '',
    autoUpdateInterval: 0, convertTarget: '', memo: '', sort: 0, prevProfile: '', nextProfile: '', preSocksPort: null, customCoreType: null,
  }
  showSubscriptionForm.value = true
}

function openEditSubscription(item: Dict) {
  editingSubscriptionId.value = item.id
  subscriptionForm.value = { ...item, customCoreType: item.customCoreType ? canonicalCode(item.customCoreType, coreTypes) : null }
  showSubscriptionForm.value = true
}

async function saveSubscription() {
  try {
    const body = { ...subscriptionForm.value, autoUpdateInterval: Number(subscriptionForm.value.autoUpdateInterval || 0), sort: Number(subscriptionForm.value.sort || 0) }
    const result = await request(editingSubscriptionId.value ? `/api/subscriptions/${encodeURIComponent(editingSubscriptionId.value)}` : '/api/subscriptions', {
      method: editingSubscriptionId.value ? 'PUT' : 'POST', body,
    })
    showSubscriptionForm.value = false
    showNotice(operationMessage(result, editingSubscriptionId.value ? 'subscriptions.saved' : 'subscriptions.added'))
    await Promise.all([loadSubscriptions(), loadGroups()])
    if (!editingSubscriptionId.value && result.data?.id) await updateSubscription(result.data.id, subscriptionUseProxy.value)
  } catch (error) { showError(error) }
}

async function deleteSubscription(item: Dict) {
  if (!window.confirm(t('subscriptions.deleteConfirm', { name: item.remarks }))) return
  try {
    const result = await request(`/api/subscriptions/${encodeURIComponent(item.id)}`, { method: 'DELETE' })
    showNotice(operationMessage(result, 'subscriptions.deleted'))
    await loadSubscriptions()
    await loadGroups()
    await loadProfiles()
  } catch (error) { showError(error) }
}

async function updateSubscription(id: string, useProxy = subscriptionUseProxy.value) {
  try {
    const result = await request(`/api/subscriptions/${encodeURIComponent(id)}/update?useProxy=${useProxy}`, { method: 'POST' })
    showNotice(operationMessage(result, 'subscriptions.updateStarted'))
    await loadOperations()
  } catch (error) { showError(error) }
}

async function updateSubscriptions(subscriptionId: string | null, useProxy = subscriptionUseProxy.value) {
  try {
    const result = await request('/api/subscriptions/update', { method: 'POST', body: { subscriptionId, useProxy } })
    showNotice(operationMessage(result, 'subscriptions.updateStarted'))
    await loadOperations()
  } catch (error) { showError(error) }
}

async function shareSubscription(item: Dict) {
  try {
    const share = await data(`/api/subscriptions/${encodeURIComponent(item.id)}/share`)
    await navigator.clipboard.writeText(share.url)
    showNotice(t('subscriptions.shareCopied'))
  } catch (error) { showError(error) }
}

async function activateRoute(id: string) {
  if (!id) return
  try {
    const result = await request(`/api/settings/routing-profiles/${encodeURIComponent(id)}/activate`, { method: 'POST' })
    showNotice(operationMessage(result))
    await Promise.all([loadRouting(), loadStatus()])
  } catch (error) { showError(error) }
}

function openAddRoute() {
  editingRouteId.value = ''
  routeForm.value = { remarks: '', url: '', enabled: true, locked: false, customIcon: '', customRulesetPath4Singbox: '', domainStrategy: '', domainStrategy4Singbox: '', ruleSet: '[]', ruleNum: 0, sort: routes.value.length + 1 }
  showRouteForm.value = true
}

function openEditRoute(route: Dict) {
  editingRouteId.value = route.id
  routeForm.value = { ...route }
  showRouteForm.value = true
}

async function saveRoute() {
  try {
    const result = await request(editingRouteId.value ? `/api/settings/routing-profiles/${encodeURIComponent(editingRouteId.value)}` : '/api/settings/routing-profiles', {
      method: editingRouteId.value ? 'PUT' : 'POST', body: routeForm.value,
    })
    showRouteForm.value = false
    showNotice(operationMessage(result))
    await loadRouting()
  } catch (error) { showError(error) }
}

async function deleteRoute(route: Dict) {
  if (!window.confirm(t('common.confirmDelete'))) return
  try {
    const result = await request(`/api/settings/routing-profiles/${encodeURIComponent(route.id)}`, { method: 'DELETE' })
    showNotice(operationMessage(result))
    await loadRouting()
  } catch (error) { showError(error) }
}

async function importRoutingProfiles() {
  try {
    const result = await request('/api/settings/routing-profiles/import', { method: 'POST' })
    showNotice(operationMessage(result))
    await loadRouting()
  } catch (error) { showError(error) }
}

async function saveRoutingRules() {
  try {
    const parsed = JSON.parse(rulesRaw.value)
    const result = await request(`/api/settings/routing-profiles/${encodeURIComponent(activeRoutingId.value)}/rules`, { method: 'PUT', body: parsed })
    showNotice(operationMessage(result))
    await loadRules()
  } catch (error) {
    if (error instanceof SyntaxError) showNotice(t('common.invalidJson'), 'error')
    else showError(error)
  }
}

async function importRoutingRules() {
  try {
    const result = await request(`/api/settings/routing-profiles/${encodeURIComponent(activeRoutingId.value)}/rules/import`, {
      method: 'POST', body: { content: ruleImportText.value, append: appendRules.value },
    })
    ruleImportText.value = ''
    showNotice(operationMessage(result))
    await loadRules()
  } catch (error) { showError(error) }
}

async function addRoutingRule() {
  const rules = [...routingRules.value, { id: crypto.randomUUID(), enabled: true, type: 'field', remarks: '', outboundTag: 'proxy' }]
  try {
    const result = await request(`/api/settings/routing-profiles/${encodeURIComponent(activeRoutingId.value)}/rules`, { method: 'PUT', body: rules })
    showNotice(operationMessage(result))
    await loadRules()
  } catch (error) { showError(error) }
}

async function removeRoutingRule(rule: Dict) {
  try {
    const result = await request(`/api/settings/routing-profiles/${encodeURIComponent(activeRoutingId.value)}/rules/${encodeURIComponent(rule.id)}`, { method: 'DELETE' })
    showNotice(operationMessage(result))
    await loadRules()
  } catch (error) { showError(error) }
}

async function moveRoutingRule(rule: Dict, direction: string) {
  try {
    const result = await request(`/api/settings/routing-profiles/${encodeURIComponent(activeRoutingId.value)}/rules/move`, {
      method: 'POST', body: { ruleId: rule.id, direction, position: -1 },
    })
    showNotice(operationMessage(result))
    await loadRules()
  } catch (error) { showError(error) }
}

async function copyRoutingRules() {
  try {
    await navigator.clipboard.writeText(rulesRaw.value)
    showNotice(t('common.copySuccess'))
  } catch { showNotice(t('common.clipboardUnavailable'), 'error') }
}

async function saveSimpleDns() {
  try {
    const payload = JSON.parse(simpleDnsRaw.value)
    const result = await request('/api/settings/dns/simple', { method: 'PUT', body: payload })
    showNotice(operationMessage(result))
  } catch (error) {
    if (error instanceof SyntaxError) showNotice(t('common.invalidJson'), 'error')
    else showError(error)
  }
}

async function saveDnsProfile(profile: Dict) {
  try {
    const result = await request(`/api/settings/dns/profiles/${encodeURIComponent(coreTypeRoute(profile.coreType))}`, {
      method: 'PUT',
      body: {
        remarks: profile.remarks, enabled: profile.enabled, useSystemHosts: profile.useSystemHosts,
        normalDNS: profile.normalDNS, domainStrategy4Freedom: profile.domainStrategy4Freedom,
        domainDNSAddress: profile.domainDNSAddress,
      },
    })
    showNotice(operationMessage(result))
  } catch (error) { showError(error) }
}

async function saveTemplate(template: Dict) {
  try {
    const result = await request(`/api/settings/core-templates/${encodeURIComponent(coreTypeRoute(template.coreType))}`, {
      method: 'PUT', body: {
        remarks: template.remarks, enabled: template.enabled, config: template.config,
        addProxyOnly: template.addProxyOnly, proxyDetour: template.proxyDetour,
      },
    })
    showNotice(operationMessage(result))
  } catch (error) { showError(error) }
}

function parseLines(value: string): string[] {
  return value.split(/\r?\n/).map((line) => line.trim()).filter(Boolean)
}

async function saveInbound() {
  try {
    const result = await request('/api/settings/inbound', {
      method: 'PUT', body: {
        localPort: Number(inboundForm.value.localPort), secondLocalPortEnabled: inboundForm.value.secondLocalPortEnabled,
        udpEnabled: inboundForm.value.udpEnabled, sniffingEnabled: inboundForm.value.sniffingEnabled,
        destOverride: parseLines(inboundForm.value.destOverrideText || ''), routeOnly: inboundForm.value.routeOnly,
        allowLANConn: inboundForm.value.allowLANConn, newPort4LAN: inboundForm.value.newPort4LAN,
        user: inboundForm.value.user, pass: inboundForm.value.pass,
      },
    })
    showNotice(operationMessage(result))
    await loadStatus()
  } catch (error) { showError(error) }
}

async function saveCoreSettings() {
  try {
    const { fragmentLengthsText, fragmentDelaysText, ...core } = coreForm.value
    const result = await request('/api/settings/core', {
      method: 'PUT', body: {
        ...core,
        fragmentLengths: parseLines(fragmentLengthsText || ''), fragmentDelays: parseLines(fragmentDelaysText || ''),
        mux4RayConcurrency: core.mux4RayConcurrency === '' ? null : Number(core.mux4RayConcurrency),
        mux4RayXudpConcurrency: core.mux4RayXudpConcurrency === '' ? null : Number(core.mux4RayXudpConcurrency),
        mux4SboxMaxConnections: Number(core.mux4SboxMaxConnections || 0), hy2UpMbps: Number(core.hy2UpMbps || 0), hy2DownMbps: Number(core.hy2DownMbps || 0),
      },
    })
    showNotice(operationMessage(result))
  } catch (error) { showError(error) }
}

async function saveAppSettings() {
  try {
    const result = await request('/api/settings/application', { method: 'PUT', body: { ...appForm.value, geoAutoUpdateInterval: Number(appForm.value.geoAutoUpdateInterval || 0) } })
    showNotice(operationMessage(result))
  } catch (error) { showError(error) }
}

async function saveSpeedSettings() {
  try {
    const result = await request('/api/settings/speedtest', { method: 'PUT', body: {
      ...speedForm.value,
      speedTestTimeout: Number(speedForm.value.speedTestTimeout), mixedConcurrencyCount: Number(speedForm.value.mixedConcurrencyCount),
      speedTestPageSize: speedForm.value.speedTestPageSize === '' ? null : Number(speedForm.value.speedTestPageSize),
      speedTestDelayInterval: speedForm.value.speedTestDelayInterval === '' ? null : Number(speedForm.value.speedTestDelayInterval),
    } })
    showNotice(operationMessage(result))
  } catch (error) { showError(error) }
}

async function saveCoreTypes() {
  try {
    const result = await request('/api/settings/core-types', { method: 'PUT', body: { mappings: settings.value.coreTypes || [] } })
    showNotice(operationMessage(result))
  } catch (error) { showError(error) }
}

async function saveRoutingStrategies() {
  try {
    const result = await request('/api/settings/routing', { method: 'PUT', body: routingForm.value })
    showNotice(operationMessage(result))
  } catch (error) { showError(error) }
}

async function applyPreset(preset: string) {
  try {
    const result = await request(`/api/settings/regional-presets/${preset}`, { method: 'POST' })
    showNotice(operationMessage(result))
    await loadRouting()
  } catch (error) { showError(error) }
}

async function checkXrayUpdate() {
  try {
    const result = await request(queryPath('/api/core/xray/check-update', xrayUpdate.value))
    xrayUpdate.value.result = { ...(result.data || {}), messageKey: result.messageKey }
    showNotice(translateKey(result.messageKey || (xrayUpdate.value.result.updateAvailable ? 'core.updateAvailable' : 'core.updateCurrent')))
  } catch (error) { showError(error) }
}

async function updateXray() {
  try {
    const result = await request(queryPath('/api/core/xray/update', xrayUpdate.value), { method: 'POST' })
    showNotice(operationMessage(result, 'core.updateStarted'))
    await loadOperations()
  } catch (error) { showError(error) }
}

async function updateGeo() {
  try {
    const result = await request(`/api/core/geo/update?useProxy=${xrayUpdate.value.useProxy}`, { method: 'POST' })
    showNotice(operationMessage(result, 'updates.geoStarted'))
    await loadOperations()
  } catch (error) { showError(error) }
}

async function saveWebdav() {
  try {
    const result = await request('/api/settings/webdav', { method: 'PUT', body: {
      url: webdavForm.value.url, userName: webdavForm.value.userName,
      password: webdavForm.value.password || null, dirName: webdavForm.value.dirName,
    } })
    webdavForm.value.password = ''
    showNotice(operationMessage(result))
  } catch (error) { showError(error) }
}

async function webdavAction(action: 'check' | 'backup' | 'restore') {
  try {
    if (action === 'restore' && !window.confirm(t('maintenance.restoreConfirm'))) return
    const result = await request(`/api/backup/webdav${action === 'check' ? '/check' : action === 'restore' ? '/restore' : ''}`, { method: 'POST' })
    showNotice(operationMessage(result))
  } catch (error) { showError(error) }
}

async function downloadBackup() {
  try {
    const response = await fetch('/api/backup/download', { headers: { Authorization: `Bearer ${token.value}` } })
    if (!response.ok) {
      const payload = await response.json().catch(() => null)
      const issue = new Error(translateKey(payload?.messageKey)) as ApiError
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
  } catch (error) { showError(error) }
}

async function uploadRestore(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) return
  if (!window.confirm(t('maintenance.restoreConfirm'))) return
  try {
    const formData = new FormData()
    formData.set('file', file)
    const result = await request('/api/backup/restore', { method: 'POST', body: formData })
    showNotice(operationMessage(result))
  } catch (error) { showError(error) } finally { input.value = '' }
}

async function clearStatistics() {
  if (!window.confirm(t('maintenance.clearConfirm'))) return
  try {
    const result = await request('/api/statistics', { method: 'DELETE' })
    showNotice(operationMessage(result))
    await loadProfiles()
  } catch (error) { showError(error) }
}

async function loadLogs(page = 1) {
  try {
    const result = await data(queryPath('/api/logs/page', { page, pageSize: logPageSize, filter: logFilter.value.trim() }))
    logs.value = result?.items || []
    logPage.value = result?.page || 1
    logTotal.value = result?.total || 0
  }
  catch (error) { showError(error) }
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
    const result = await request('/api/logs', { method: 'DELETE' })
    logs.value = []
    logPage.value = 1
    logTotal.value = 0
    showNotice(operationMessage(result))
  } catch (error) { showError(error) }
}

function formatDelay(value: number) {
  if (value < 0) return t('nodes.timeout')
  if (!value) return t('nodes.delayUntested')
  return `${value} ms`
}

function formatBytes(value: number | null | undefined) {
  let amount = Number(value || 0)
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let unit = 0
  while (amount >= 1024 && unit < units.length - 1) { amount /= 1024; unit += 1 }
  return `${amount.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`
}

function formatDate(epochSeconds: number) {
  if (!epochSeconds) return '—'
  return new Intl.DateTimeFormat(locale.value, { dateStyle: 'short', timeStyle: 'short' }).format(new Date(epochSeconds * 1000))
}

function listenerDescription(listener: Dict) {
  const protocols = Array.isArray(listener.protocols) ? listener.protocols.join('/') : ''
  return `${protocols ? `${protocols} ` : ''}${listener.listenAddress}:${listener.port}`
}

watch(activePage, async () => {
  if (authenticated.value) {
    try { await loadPageData() } catch (error) { showError(error) }
  }
})

watch(locale, (value) => {
  localStorage.setItem('v2rayn-web-locale', value)
  document.documentElement.lang = value
}, { immediate: true })
watch(authenticated, (connected) => {
  clearInterval(refreshTimer)
  if (connected) {
    refreshTimer = setInterval(() => {
      void Promise.all([loadStatus(), loadProfiles()]).catch(showError)
    }, 8000)
  } else {
    closeEvents()
  }
})

onMounted(async () => {
  try {
    const response = await fetch('/api/setup/status')
    if (!response.ok) throw new Error(`${response.status}`)
    const payload = await response.json()
    const setupStatus = payload?.data ?? payload
    setupRequired.value = Boolean(setupStatus?.setupRequired)
    setupAllowedFromRequest.value = Boolean(setupStatus?.setupAllowedFromThisRequest)
  } catch (error) {
    showError(error)
  } finally {
    setupStatusReady.value = true
  }
  if (setupRequired.value || !token.value) return
  try {
    await refreshBase()
    if (authenticated.value) {
      openEvents()
      await Promise.all([loadSettings(), loadRouting(), loadPageData()])
      await loadLogs()
    }
  } catch (error) { showError(error) }
})

onUnmounted(() => {
  clearInterval(refreshTimer)
  clearTimeout(noticeTimer)
  closeEvents()
})
</script>

<template>
  <div class="app-shell" @click="contextMenu = null">
    <section v-if="!setupStatusReady" class="auth-wrap"><p class="muted">{{ t('common.loading') }}</p></section>

    <section v-else-if="setupRequired && !setupAllowedFromRequest" class="auth-wrap">
      <div class="auth-box setup-box">
        <div class="auth-title"><img class="brand-glyph" src="/v2rayN.png" alt="" /><div><strong>{{ t('setup.title') }}</strong><small>{{ t('brand') }}</small></div></div>
        <p>{{ t('setup.localOnly') }}</p>
      </div>
    </section>

    <section v-else-if="setupRequired" class="auth-wrap">
      <form class="auth-box setup-box" @submit.prevent="configureManagementKey">
        <div class="auth-title"><img class="brand-glyph" src="/v2rayN.png" alt="" /><div><strong>{{ t('setup.title') }}</strong><small>{{ t('brand') }}</small></div></div>
        <p>{{ t('setup.description') }}</p>
        <div class="form-grid">
          <label>{{ t('setup.managementKey') }}<input v-model="setupKey" type="password" autocomplete="new-password" minlength="12" maxlength="4096" required /></label>
          <label>{{ t('setup.confirmKey') }}<input v-model="setupConfirmKey" type="password" autocomplete="new-password" minlength="12" maxlength="4096" required /></label>
        </div>
        <p v-if="setupError" class="setup-error" role="alert">{{ setupError }}</p>
        <button class="button primary" type="submit" :disabled="setupSubmitting">{{ setupSubmitting ? t('common.working') : t('setup.submit') }}</button>
      </form>
    </section>

    <template v-else>
    <header class="app-header">
      <div class="brand"><img class="brand-glyph" :src="brandIconSrc" :title="brandIconTitle" alt="" /><strong>{{ t('brand') }}</strong></div>
      <nav class="main-nav" :aria-label="t('brand')">
        <button v-for="item in navItems" :key="item.id" :class="['nav-tab', { selected: activePage === item.id }]" @click="navigate(item.id)">
          <span class="nav-icon">{{ item.icon }}</span>{{ t(item.key) }}
          <span v-if="item.id === 'subscriptions'" class="nav-badge">{{ subscriptions.length }}</span>
        </button>
      </nav>
      <div class="header-right">
        <span :class="['connection-tag', { online: authenticated }]">{{ authenticated ? t('auth.connected') : t('auth.waiting') }}</span>
        <select v-model="locale" class="locale-select" :aria-label="t('brand')">
          <option value="zh-CN">{{ t('localeNames.zhCN') }}</option><option value="zh-TW">{{ t('localeNames.zhTW') }}</option><option value="en-US">{{ t('localeNames.enUS') }}</option>
        </select>
        <button v-if="authenticated" class="tool-button" :title="t('common.refresh')" :disabled="loading" @click="refreshBase">⟳</button>
        <button v-if="authenticated" class="tool-button" @click="disconnect">{{ t('auth.disconnect') }}</button>
      </div>
    </header>

    <section v-if="!authenticated" class="auth-wrap">
      <form class="auth-box" @submit.prevent="login()">
        <div class="auth-title"><img class="brand-glyph" src="/v2rayN.png" alt="" /><div><strong>{{ t('auth.title') }}</strong><small>{{ t('brand') }}</small></div></div>
        <p>{{ t('auth.hint') }}</p>
        <label class="field-label" for="management-key">{{ t('auth.token') }}</label>
        <div class="inline-field"><input id="management-key" v-model="managementKeyDraft" type="password" autocomplete="current-password" :placeholder="t('auth.placeholder')" /><button class="button primary" type="submit">{{ t('auth.connect') }}</button></div>
      </form>
    </section>

    <template v-else>
      <section class="runtime-strip">
        <div class="runtime-main">
          <span :class="['status-led', { on: status?.coreRunning }]"></span>
          <strong>{{ status?.coreRunning ? (status.coreType || t('nodes.core')) : t('nodes.stopped') }}</strong>
          <span class="runtime-separator"></span>
          <span>{{ t('nodes.current') }}:</span><b class="current-runtime-name">{{ currentProfile?.remarks || status?.currentProfileName || t('nodes.noneCurrent') }}</b>
          <span class="runtime-separator"></span>
          <label class="compact-select-label">{{ t('coreToolbar.route') }}</label>
          <select v-model="activeRoutingId" class="compact-select route-select" @change="activateRoute(activeRoutingId)">
            <option value="">{{ t('common.none') }}</option>
            <option v-for="route in routes" :key="route.id" :value="route.id">{{ route.remarks }}</option>
          </select>
        </div>
        <div class="core-actions">
          <button class="button compact primary" :disabled="busy || status?.coreRunning" @click="coreAction('start')">▶ {{ t('nodes.start') }}</button>
          <button class="button compact" :disabled="busy || !status?.coreRunning" @click="coreAction('restart')">↻ {{ t('nodes.restart') }}</button>
          <button class="button compact danger" :disabled="busy || !status?.coreRunning" @click="coreAction('stop')">■ {{ t('nodes.stop') }}</button>
        </div>
      </section>

      <section class="connection-strip">
        <div class="listener-list">
          <strong>{{ t('nodes.listener') }}</strong>
          <span v-for="listener in listeners" :key="listener.name" class="listener-item">
            <i :class="['status-led', { on: listener.listening }]"></i>{{ listener.name === 'lan' ? t('nodes.lan') : t('nodes.local') }} {{ listenerDescription(listener) }}
          </span>
          <span v-if="!listeners.length" class="muted">{{ t('coreToolbar.noListener') }}</span>
        </div>
        <div class="traffic-list">
          <strong>{{ t('nodes.traffic') }}</strong>
          <span>↑ {{ t('nodes.proxyUp') }} <b>{{ formatBytes(traffic.proxyUp) }}/s</b></span>
          <span>↓ {{ t('nodes.proxyDown') }} <b>{{ formatBytes(traffic.proxyDown) }}/s</b></span>
          <span class="muted">{{ t('nodes.directUp') }} {{ formatBytes(traffic.directUp) }}/s · {{ t('nodes.directDown') }} {{ formatBytes(traffic.directDown) }}/s</span>
        </div>
        <span v-if="status && !status.statisticsEnabled" class="stats-hint">{{ t('coreToolbar.statsDisabled') }}</span>
        <span v-if="runtimeVersion" class="runtime-version">{{ runtimeVersion }}</span>
      </section>

      <div v-if="notice" :class="['notice-bar', noticeKind]" role="status">{{ notice }}<button class="tool-button" @click="notice = ''">×</button></div>

      <main class="workspace">
        <section v-if="activePage === 'nodes'" class="page nodes-page">
          <div class="page-toolbar">
            <div class="page-title"><h1>{{ t('nodes.title') }}</h1><span class="count-tag">{{ filteredProfiles.length }}</span></div>
            <div class="toolbar-main">
              <button class="button primary" @click="openAddProfile">＋ {{ t('nodes.addNode') }}</button>
              <button class="button" @click="openImportProfiles">{{ t('common.import') }}</button>
              <button class="button" @click="updateSubscriptions(selectedGroup || null)">{{ t('subscriptions.quickUpdate') }}</button>
              <select class="compact-select test-select" @change="($event.target as HTMLSelectElement).value && startSpeedTest(($event.target as HTMLSelectElement).value)">
                <option value="">{{ t('nodes.test') }}…</option>
                <option v-for="action in testActions" :key="action.id" :value="action.id">{{ t(action.key) }}</option>
              </select>
              <button class="button" @click="runProfileAction('test-group')">{{ t('nodes.testGroup') }}</button>
              <button class="button" :disabled="!operations.includes('speedtest')" @click="stopSpeedTests">{{ t('nodes.stopTest') }}</button>
            </div>
          </div>

          <div class="group-toolbar">
            <span class="toolbar-label">{{ t('nodes.group') }}</span>
            <div class="group-chips">
              <button v-for="group in groups" :key="group.id || 'all'" :class="['group-chip', { selected: selectedGroup === group.id }]" @click="changeGroup(group.id)">
                {{ group.name || t('common.allGroups') }}<small>{{ group.profileCount }}</small>
              </button>
            </div>
            <div class="group-generation"><button class="link-button" :disabled="!selectedGroup" :title="!selectedGroup ? t('nodes.groupGenerationSelectSubscription') : ''" @click="generateGroups(false)">{{ t('nodes.generateAllGroups') }}</button><button class="link-button" :disabled="!selectedGroup || !profiles.length" :title="!selectedGroup ? t('nodes.groupGenerationSelectSubscription') : !profiles.length ? t('nodes.noProfile') : ''" @click="generateGroups(true)">{{ t('nodes.generateRegionGroups') }}</button></div>
            <label class="search-box"><span>⌕</span><input v-model="filter" :placeholder="t('nodes.filterPlaceholder')" @keyup.enter="loadProfiles" /><button v-if="filter" class="clear-search" :aria-label="t('common.close')" @click="filter = ''; loadProfiles()">×</button></label>
          </div>

          <div v-if="selectedIds.length" class="bulk-toolbar">
            <strong>{{ t('common.selected', { count: selectedIds.length }) }}</strong>
            <button class="button compact" @click="runProfileAction('copy')">{{ t('nodes.copySelected') }}</button>
            <button class="button compact" @click="exportSelected">{{ t('nodes.exportSelected') }}</button>
            <select class="compact-select" @change="($event.target as HTMLSelectElement).value !== '' && moveSelectedToGroup(($event.target as HTMLSelectElement).value)">
              <option value="">{{ t('nodes.moveGroup') }}…</option>
              <option v-for="group in groups" :key="group.id || 'all-target'" :value="group.id">{{ group.name || t('common.allGroups') }}</option>
            </select>
            <div class="button-group">
              <button class="button compact" :title="t('nodes.top')" @click="moveSelected('top')">⇈</button><button class="button compact" :title="t('nodes.up')" @click="moveSelected('up')">↑</button><button class="button compact" :title="t('nodes.down')" @click="moveSelected('down')">↓</button><button class="button compact" :title="t('nodes.bottom')" @click="moveSelected('bottom')">⇊</button><button class="button compact" :title="t('nodes.position')" @click="moveSelectedPosition">#</button>
            </div>
            <button class="button compact danger" @click="runProfileAction('delete')">{{ t('nodes.deleteSelected') }}</button>
            <button class="tool-button" @click="selectedIds = []">×</button>
          </div>

          <div class="table-wrap">
            <table class="profile-table">
              <thead><tr>
                <th class="check-cell"><input type="checkbox" :checked="allVisibleSelected" :aria-label="t('common.selected', { count: filteredProfiles.length })" @change="toggleAllVisible" /></th>
                <th><button class="sort-button" @click="sortProfiles('ConfigType')">{{ t('nodes.type') }}</button></th>
                <th><button class="sort-button" @click="sortProfiles('Remarks')">{{ t('nodes.remarks') }}</button></th>
                <th><button class="sort-button" @click="sortProfiles('Address')">{{ t('nodes.address') }}</button></th>
                <th><button class="sort-button" @click="sortProfiles('Port')">{{ t('nodes.port') }}</button></th>
                <th><button class="sort-button" @click="sortProfiles('Network')">{{ t('nodes.network') }}</button></th>
                <th><button class="sort-button" @click="sortProfiles('StreamSecurity')">{{ t('nodes.tls') }}</button></th>
                <th><button class="sort-button" @click="sortProfiles('SubRemarks')">{{ t('nodes.groupColumn') }}</button></th>
                <th><button class="sort-button" @click="sortProfiles('DelayVal')">{{ t('nodes.delay') }}</button></th>
                <th><button class="sort-button" @click="sortProfiles('SpeedVal')">{{ t('nodes.speed') }}</button></th>
                <th>{{ t('nodes.ip') }}</th>
                <th><button class="sort-button" @click="sortProfiles('TodayUp')">↑ {{ t('nodes.todayUp') }}</button></th>
                <th><button class="sort-button" @click="sortProfiles('TodayDown')">↓ {{ t('nodes.todayDown') }}</button></th>
                <th><button class="sort-button" @click="sortProfiles('TotalUp')">↑ {{ t('nodes.totalUp') }}</button></th>
                <th><button class="sort-button" @click="sortProfiles('TotalDown')">↓ {{ t('nodes.totalDown') }}</button></th>
                <th class="actions-cell">{{ t('nodes.actions') }}</th>
              </tr></thead>
              <tbody>
                <tr v-for="profile in filteredProfiles" :key="profile.indexId" :class="{ current: profile.isCurrent, selected: selectedIds.includes(profile.indexId) }" @dblclick="selectProfile(profile)" @contextmenu="openContext($event, profile)">
                  <td class="check-cell"><input type="checkbox" :checked="selectedIds.includes(profile.indexId)" :aria-label="profile.remarks || profile.address" @change="toggleProfile(profile.indexId)" @click.stop /></td>
                  <td :data-label="t('nodes.type')"><span class="protocol-code">{{ profile.protocol }}</span></td>
                  <td class="remark-cell" :data-label="t('nodes.remarks')"><span v-if="profile.isCurrent" class="current-marker" :title="t('nodes.current')">●</span><span class="remark-text" :title="profile.remarks">{{ profile.remarks || '—' }}</span></td>
                  <td class="address-cell" :data-label="t('nodes.address')" :title="profile.address">{{ profile.address }}</td>
                  <td class="number-cell" :data-label="t('nodes.port')">{{ profile.port }}</td>
                  <td :data-label="t('nodes.network')">{{ profile.network || '—' }}</td>
                  <td :data-label="t('nodes.tls')">{{ profile.streamSecurity || '—' }}</td>
                  <td class="group-cell" :data-label="t('nodes.groupColumn')" :title="profile.subscriptionName">{{ profile.subscriptionName || t('common.none') }}</td>
                  <td :data-label="t('nodes.delay')" :class="['number-cell', 'delay-cell', { bad: profile.delay < 0 }]">{{ formatDelay(profile.delay) }}</td>
                  <td class="number-cell" :data-label="t('nodes.speed')">{{ profile.speed ? `${profile.speed} MB/s` : '—' }}</td>
                  <td class="ip-cell" :data-label="t('nodes.ip')" :title="profile.ipInfo">{{ profile.ipInfo || '—' }}</td>
                  <td class="number-cell" :data-label="t('nodes.todayUp')">{{ formatBytes(profile.todayUp) }}</td><td class="number-cell" :data-label="t('nodes.todayDown')">{{ formatBytes(profile.todayDown) }}</td>
                  <td class="number-cell" :data-label="t('nodes.totalUp')">{{ formatBytes(profile.totalUp) }}</td><td class="number-cell" :data-label="t('nodes.totalDown')">{{ formatBytes(profile.totalDown) }}</td>
                  <td class="row-actions" :data-label="t('nodes.actions')"><button class="link-button" :disabled="profile.isCurrent" @click="selectProfile(profile)">{{ profile.isCurrent ? t('nodes.current') : t('nodes.switch') }}</button><button class="link-button" @click="startSpeedTest('tcping', [profile.indexId])">{{ t('nodes.test') }}</button><button class="tool-button row-more" :title="t('nodes.actions')" @click.stop="openContext($event, profile)">⋯</button></td>
                </tr>
                <tr v-if="!filteredProfiles.length"><td colspan="16" class="empty-row">{{ profiles.length ? t('common.noResults') : t('nodes.noProfile') }}</td></tr>
              </tbody>
            </table>
          </div>
          <div class="table-footer"><span>{{ t('nodes.regexHint') }}</span><div class="quick-maintenance"><button class="link-button" @click="runProfileAction('deduplicate')">{{ t('nodes.deduplicate') }}</button><button class="link-button" @click="runProfileAction('remove-invalid')">{{ t('nodes.removeInvalid') }}</button><button class="link-button" @click="loadProfiles">{{ t('common.refresh') }}</button></div></div>
        </section>

        <section v-else-if="activePage === 'subscriptions'" class="page">
          <div class="page-toolbar"><div class="page-title"><h1>{{ t('subscriptions.title') }}</h1><span class="count-tag">{{ subscriptions.length }}</span></div><div class="toolbar-main"><label class="check-inline"><input v-model="subscriptionUseProxy" type="checkbox" />{{ t('subscriptions.useProxy') }}</label><button class="button" @click="updateSubscriptions(null)">{{ t('subscriptions.updateAll') }}</button><button class="button" @click="updateSubscriptions(selectedGroup || null)">{{ t('subscriptions.updateGroup') }}</button><button class="button primary" @click="openAddSubscription">＋ {{ t('subscriptions.addSubscription') }}</button></div></div>
          <div class="subscription-table-wrap"><table class="data-table subscription-table"><thead><tr><th>{{ t('subscriptions.name') }}</th><th>{{ t('subscriptions.url') }}</th><th>{{ t('common.enabled') }}</th><th>{{ t('subscriptions.interval') }}</th><th>{{ t('subscriptions.updated') }}</th><th>{{ t('subscriptions.userAgent') }}</th><th>{{ t('subscriptions.filter') }}</th><th>{{ t('nodes.actions') }}</th></tr></thead><tbody>
            <tr v-for="item in subscriptions" :key="item.id"><td class="strong-cell">{{ item.remarks }}</td><td class="url-cell" :title="item.url">{{ item.url }}</td><td>{{ item.enabled ? t('common.enabled') : t('common.disabled') }}</td><td>{{ item.autoUpdateInterval || t('common.none') }}</td><td>{{ item.updateTime ? formatDate(item.updateTime) : t('subscriptions.neverUpdated') }}</td><td>{{ item.userAgent || '—' }}</td><td>{{ item.filter || '—' }}</td><td class="row-actions"><button class="link-button" @click="updateSubscription(item.id)">{{ t('subscriptions.update') }}</button><button class="link-button" @click="shareSubscription(item)">{{ t('subscriptions.share') }}</button><button class="link-button" @click="openEditSubscription(item)">{{ t('common.edit') }}</button><button class="link-button danger-text" @click="deleteSubscription(item)">{{ t('common.delete') }}</button></td></tr>
            <tr v-if="!subscriptions.length"><td colspan="8" class="empty-row">{{ t('subscriptions.noSubscriptions') }}</td></tr>
          </tbody></table></div>
        </section>

        <section v-else-if="activePage === 'routing'" class="page">
          <div class="page-toolbar"><div class="page-title"><h1>{{ t('routing.title') }}</h1></div><div class="toolbar-main"><button class="button" @click="importRoutingProfiles">{{ t('routing.importProfiles') }}</button><button class="button primary" @click="openAddRoute">＋ {{ t('routing.create') }}</button></div></div>
          <div class="split-workspace">
            <section class="subpanel route-list-panel"><div class="subpanel-heading"><h2>{{ t('routing.profiles') }}</h2><span class="count-tag">{{ routes.length }}</span></div>
              <div v-for="route in routes" :key="route.id" :class="['route-row', { selected: route.id === activeRoutingId, current: route.isActive }]" @click="activeRoutingId = route.id; loadRules(route.id)">
                <div class="route-info"><strong>{{ route.remarks }}</strong><small>{{ route.ruleNum }} · {{ route.enabled ? t('common.enabled') : t('common.disabled') }}</small></div><span v-if="route.isActive" class="current-label">{{ t('routing.default') }}</span>
                <div class="row-actions"><button class="tool-button" :title="t('common.edit')" @click.stop="openEditRoute(route)">✎</button><button class="tool-button danger-text" :title="t('common.delete')" @click.stop="deleteRoute(route)">×</button></div>
              </div>
              <p v-if="!routes.length" class="muted empty-inline">{{ t('routing.noRouting') }}</p>
              <div class="preset-bar"><label>{{ t('routing.regionalPreset') }}</label><button class="link-button" @click="applyPreset('Default')">{{ t('routing.presetDefault') }}</button><button class="link-button" @click="applyPreset('Russia')">{{ t('routing.presetRussia') }}</button><button class="link-button" @click="applyPreset('Iran')">{{ t('routing.presetIran') }}</button></div>
              <div class="form-grid route-strategies"><label>{{ t('routing.domainStrategy') }}<input v-model="routingForm.domainStrategy" /></label><label>{{ t('routing.domainStrategySingbox') }}<input v-model="routingForm.domainStrategy4Singbox" /></label><button class="button compact primary" @click="saveRoutingStrategies">{{ t('settings.saveRouting') }}</button></div>
            </section>
            <section class="subpanel rules-panel"><div class="subpanel-heading"><div><h2>{{ t('routing.rules') }}</h2><small>{{ currentRoute?.remarks || t('common.none') }}</small></div><div class="toolbar-main"><button class="button compact" :disabled="!activeRoutingId" @click="addRoutingRule">＋ {{ t('routing.addRule') }}</button><button class="button compact" :disabled="!activeRoutingId" @click="copyRoutingRules">{{ t('common.copy') }}</button><button class="button compact primary" :disabled="!activeRoutingId" @click="saveRoutingRules">{{ t('routing.saveRules') }}</button></div></div>
              <div v-if="routingRules.length" class="rules-mini-table"><div v-for="rule in routingRules" :key="rule.id" class="rule-row"><span :class="['rule-state', { off: !rule.enabled }]">{{ rule.enabled ? '●' : '○' }}</span><strong>{{ rule.remarks || rule.type || rule.ruleType || '—' }}</strong><span class="rule-details">{{ rule.domain?.join(', ') || rule.ip?.join(', ') || rule.port || rule.network || '—' }}</span><span class="rule-outbound">{{ rule.outboundTag || '—' }}</span><div class="row-actions"><button class="tool-button" :title="t('routing.moveUp')" @click="moveRoutingRule(rule, 'up')">↑</button><button class="tool-button" :title="t('routing.moveDown')" @click="moveRoutingRule(rule, 'down')">↓</button><button class="tool-button danger-text" :title="t('common.delete')" @click="removeRoutingRule(rule)">×</button></div></div></div>
              <p v-else class="muted empty-inline">{{ t('common.empty') }}</p>
              <label class="field-label raw-json-label">{{ t('common.rawJson') }}<small>{{ t('routing.ruleJsonHint') }}</small></label><textarea v-model="rulesRaw" class="code-area rules-json" spellcheck="false"></textarea>
              <div class="import-rule-row"><textarea v-model="ruleImportText" class="code-area import-rule-input" :placeholder="t('routing.importRules')"></textarea><div class="import-rule-controls"><label class="check-inline"><input v-model="appendRules" type="checkbox" />{{ t('routing.append') }}</label><button class="button" :disabled="!ruleImportText" @click="importRoutingRules">{{ t('common.import') }}</button></div></div>
            </section>
          </div>
        </section>

        <section v-else-if="activePage === 'dns'" class="page">
          <div class="page-toolbar"><div class="page-title"><h1>{{ t('dns.title') }}</h1></div><button class="button" @click="loadDns">{{ t('common.refresh') }}</button></div>
          <div class="dns-layout"><section class="subpanel"><div class="subpanel-heading"><h2>{{ t('dns.simple') }}</h2><button class="button compact primary" @click="saveSimpleDns">{{ t('dns.saveSimple') }}</button></div><label class="field-label raw-json-label">{{ t('dns.jsonEditor') }}</label><textarea v-model="simpleDnsRaw" class="code-area dns-code" spellcheck="false"></textarea></section>
            <section class="subpanel"><div class="subpanel-heading"><h2>{{ t('dns.profiles') }}</h2></div><div v-for="profile in dnsProfiles" :key="profile.id" class="dns-profile-block"><div class="dns-profile-head"><strong>{{ profile.coreType }}</strong><label class="check-inline"><input v-model="profile.enabled" type="checkbox" />{{ t('dns.enabled') }}</label></div>
              <div class="form-grid"><label>{{ t('dns.remarks') }}<input v-model="profile.remarks" /></label><label>{{ t('dns.normalDns') }}<textarea v-model="profile.normalDNS"></textarea></label><label>{{ t('dns.domainStrategy') }}<input v-model="profile.domainStrategy4Freedom" /></label><label>{{ t('dns.domainDnsAddress') }}<input v-model="profile.domainDNSAddress" /></label><label class="check-inline"><input v-model="profile.useSystemHosts" type="checkbox" />{{ t('dns.useSystemHosts') }}</label></div><button class="button compact primary" @click="saveDnsProfile(profile)">{{ t('dns.saveCoreDns', { core: profile.coreType }) }}</button>
            </div><p v-if="!dnsProfiles.length" class="muted empty-inline">{{ t('common.empty') }}</p></section>
          </div>
        </section>

        <section v-else-if="activePage === 'settings'" class="page settings-page">
          <div class="page-toolbar"><div class="page-title"><h1>{{ t('settings.title') }}</h1></div><span class="muted">{{ t('settings.restartHint') }}</span></div>
          <div class="settings-grid">
            <section class="subpanel"><div class="subpanel-heading"><h2>{{ t('settings.inbound') }}</h2></div><div class="form-grid two-col">
              <label>{{ t('settings.localPort') }}<input v-model.number="inboundForm.localPort" type="number" min="1" max="65535" /></label>
              <label class="check-inline"><input v-model="inboundForm.secondLocalPortEnabled" type="checkbox" />{{ t('settings.secondPort') }}</label>
              <label class="check-inline"><input v-model="inboundForm.udpEnabled" type="checkbox" />{{ t('settings.udp') }}</label>
              <label class="check-inline"><input v-model="inboundForm.sniffingEnabled" type="checkbox" />{{ t('settings.sniffing') }}</label>
              <label>{{ t('settings.destOverride') }}<textarea v-model="inboundForm.destOverrideText"></textarea></label>
              <label class="check-inline"><input v-model="inboundForm.routeOnly" type="checkbox" />{{ t('settings.routeOnly') }}</label>
              <label class="check-inline"><input v-model="inboundForm.allowLANConn" type="checkbox" />{{ t('settings.allowLan') }}</label>
              <label class="check-inline"><input v-model="inboundForm.newPort4LAN" type="checkbox" />{{ t('settings.newLanPort') }}</label>
              <label>{{ t('settings.user') }}<input v-model="inboundForm.user" /></label><label>{{ t('settings.pass') }}<input v-model="inboundForm.pass" type="password" /></label>
            </div><button class="button compact primary" @click="saveInbound">{{ t('settings.saveInbound') }}</button></section>

            <section class="subpanel"><div class="subpanel-heading"><h2>{{ t('settings.core') }}</h2></div><div class="form-grid two-col">
              <label class="check-inline"><input v-model="coreForm.logEnabled" type="checkbox" />{{ t('settings.logEnabled') }}</label><label>{{ t('settings.loglevel') }}<select v-model="coreForm.loglevel"><option v-for="level in ['debug', 'info', 'warning', 'error', 'none']" :key="level">{{ level }}</option></select></label>
              <label>{{ t('settings.fingerprint') }}<input v-model="coreForm.defFingerprint" /></label><label>{{ t('settings.userAgent') }}<input v-model="coreForm.defUserAgent" /></label><label>{{ t('settings.sendThrough') }}<input v-model="coreForm.sendThrough" /></label><label>{{ t('settings.bindInterface') }}<input v-model="coreForm.bindInterface" /></label>
              <label>{{ t('settings.muxRay') }}<input v-model.number="coreForm.mux4RayConcurrency" type="number" min="0" /></label><label>{{ t('settings.muxXudp') }}<input v-model.number="coreForm.mux4RayXudpConcurrency" type="number" min="0" /></label><label>{{ t('settings.muxXudp443') }}<input v-model="coreForm.mux4RayXudpProxyUDP443" /></label><label>{{ t('settings.muxSboxProtocol') }}<input v-model="coreForm.mux4SboxProtocol" /></label><label>{{ t('settings.muxSboxConnections') }}<input v-model.number="coreForm.mux4SboxMaxConnections" type="number" min="0" /></label><label class="check-inline"><input v-model="coreForm.mux4SboxPadding" type="checkbox" />{{ t('settings.muxSboxPadding') }}</label><label class="check-inline"><input v-model="coreForm.enableCacheFile4Sbox" type="checkbox" />{{ t('settings.cacheSbox') }}</label>
              <label>{{ t('settings.hy2Up') }}<input v-model.number="coreForm.hy2UpMbps" type="number" min="0" /></label><label>{{ t('settings.hy2Down') }}<input v-model.number="coreForm.hy2DownMbps" type="number" min="0" /></label><label class="check-inline"><input v-model="coreForm.enableFragment" type="checkbox" />{{ t('settings.fragment') }}</label><label class="check-inline"><input v-model="coreForm.enableFinalFragment" type="checkbox" />{{ t('settings.finalFragment') }}</label><label>{{ t('settings.fragmentPackets') }}<input v-model="coreForm.fragmentPackets" /></label><label>{{ t('settings.fragmentMaxSplit') }}<input v-model="coreForm.fragmentMaxSplit" /></label><label>{{ t('settings.fragmentLengths') }}<textarea v-model="coreForm.fragmentLengthsText"></textarea></label><label>{{ t('settings.fragmentDelays') }}<textarea v-model="coreForm.fragmentDelaysText"></textarea></label>
            </div><button class="button compact primary" @click="saveCoreSettings">{{ t('settings.saveCore') }}</button></section>

            <section class="subpanel"><div class="subpanel-heading"><h2>{{ t('settings.application') }}</h2></div><div class="form-grid two-col"><label class="check-inline"><input v-model="appForm.enableStatistics" type="checkbox" />{{ t('settings.statistics') }}</label><label class="check-inline"><input v-model="appForm.displayRealTimeSpeed" type="checkbox" />{{ t('settings.realtimeSpeed') }}</label><label class="check-inline"><input v-model="appForm.keepOlderDedupl" type="checkbox" />{{ t('settings.keepOlderDedupl') }}</label><label>{{ t('settings.geoAutoUpdate') }}<input v-model.number="appForm.geoAutoUpdateInterval" type="number" min="0" /></label><label>{{ t('settings.rootCertProvider') }}<input v-model="appForm.rootCertProvider" /></label><label>{{ t('settings.geoSourceUrl') }}<input v-model="appForm.geoSourceUrl" /></label><label>{{ t('settings.srsSourceUrl') }}<input v-model="appForm.srsSourceUrl" /></label><label>{{ t('settings.routeRulesSourceUrl') }}<input v-model="appForm.routeRulesTemplateSourceUrl" /></label><label>{{ t('settings.subConvertUrl') }}<input v-model="appForm.subConvertUrl" /></label></div><button class="button compact primary" @click="saveAppSettings">{{ t('settings.saveApplication') }}</button></section>

            <section class="subpanel"><div class="subpanel-heading"><h2>{{ t('settings.speedtest') }}</h2></div><div class="form-grid two-col"><label>{{ t('settings.speedTimeout') }}<input v-model.number="speedForm.speedTestTimeout" type="number" min="1" /></label><label>{{ t('settings.mixedConcurrency') }}<input v-model.number="speedForm.mixedConcurrencyCount" type="number" min="1" /></label><label>{{ t('settings.speedUrl') }}<input v-model="speedForm.speedTestUrl" /></label><label>{{ t('settings.pingUrl') }}<input v-model="speedForm.speedPingTestUrl" /></label><label>{{ t('settings.ipApiUrl') }}<input v-model="speedForm.ipapiUrl" /></label><label>{{ t('settings.udpTarget') }}<input v-model="speedForm.udpTestTarget" /></label><label>{{ t('settings.pageSize') }}<input v-model.number="speedForm.speedTestPageSize" type="number" min="1" /></label><label>{{ t('settings.delayInterval') }}<input v-model.number="speedForm.speedTestDelayInterval" type="number" min="0" /></label></div><button class="button compact primary" @click="saveSpeedSettings">{{ t('settings.saveSpeedtest') }}</button></section>

            <section class="subpanel"><div class="subpanel-heading"><h2>{{ t('settings.coreTypes') }}</h2><button class="button compact primary" @click="saveCoreTypes">{{ t('settings.saveCoreTypes') }}</button></div><div class="mapping-list"><div v-for="mapping in settings.coreTypes || []" :key="mapping.configType" class="mapping-row"><span>{{ mapping.configType }}</span><select v-model="mapping.coreType"><option v-for="core in coreTypes" :key="core" :value="core">{{ core }}</option></select></div></div></section>
          </div>
        </section>

        <section v-else-if="activePage === 'templates'" class="page">
          <div class="page-toolbar"><div class="page-title"><h1>{{ t('templates.title') }}</h1></div><button class="button" @click="loadTemplates">{{ t('common.refresh') }}</button></div>
          <div class="template-grid"><section v-for="template in templates" :key="template.id" class="subpanel template-panel"><div class="subpanel-heading"><h2>{{ template.coreType }}</h2><label class="check-inline"><input v-model="template.enabled" type="checkbox" />{{ t('templates.enabled') }}</label></div><div class="form-grid"><label>{{ t('templates.remarks') }}<input v-model="template.remarks" /></label><label>{{ t('templates.config') }}<textarea v-model="template.config" class="code-area" spellcheck="false"></textarea></label><label class="check-inline"><input v-model="template.addProxyOnly" type="checkbox" />{{ t('templates.addProxyOnly') }}</label><label>{{ t('templates.proxyDetour') }}<input v-model="template.proxyDetour" /></label></div><button class="button compact primary" @click="saveTemplate(template)">{{ t('templates.save') }}</button></section><p v-if="!templates.length" class="muted empty-inline">{{ t('common.empty') }}</p></div>
        </section>

        <section v-else-if="activePage === 'maintenance'" class="page">
          <div class="page-toolbar"><div class="page-title"><h1>{{ t('maintenance.title') }}</h1></div><button class="button" @click="loadMaintenance">{{ t('common.refresh') }}</button></div>
          <div class="maintenance-grid"><section class="subpanel"><div class="subpanel-heading"><h2>{{ t('maintenance.updates') }}</h2></div><div class="form-grid two-col"><label class="check-inline"><input v-model="xrayUpdate.preRelease" type="checkbox" />{{ t('maintenance.preRelease') }}</label><label class="check-inline"><input v-model="xrayUpdate.useProxy" type="checkbox" />{{ t('maintenance.useProxy') }}</label></div><div class="button-row"><button class="button" @click="checkXrayUpdate">{{ t('maintenance.checkXray') }}</button><button class="button primary" :disabled="operations.includes('xray-update')" @click="updateXray">{{ t('maintenance.updateXray') }}</button><button class="button" :disabled="operations.includes('geo-update')" @click="updateGeo">{{ t('maintenance.updateGeo') }}</button></div><p v-if="xrayUpdate.result" class="operation-result">{{ xrayUpdate.result.updateAvailable ? t('maintenance.updateAvailable', { version: xrayUpdate.result.version }) : t('maintenance.upToDate') }}</p></section>
            <section class="subpanel"><div class="subpanel-heading"><h2>{{ t('maintenance.statistics') }}</h2></div><p class="muted">{{ t('maintenance.statistics') }} · {{ status?.statisticsEnabled ? t('common.enabled') : t('status.statisticsOff') }}</p><button class="button danger" @click="clearStatistics">{{ t('maintenance.clearStatistics') }}</button></section>
            <section class="subpanel webdav-panel"><div class="subpanel-heading"><h2>{{ t('maintenance.webdav') }}</h2><span v-if="webdavForm.hasPassword" class="muted">{{ t('maintenance.passwordStored') }}</span></div><div class="form-grid two-col"><label>{{ t('maintenance.webdavUrl') }}<input v-model="webdavForm.url" /></label><label>{{ t('maintenance.webdavDir') }}<input v-model="webdavForm.dirName" /></label><label>{{ t('maintenance.webdavUser') }}<input v-model="webdavForm.userName" /></label><label>{{ t('maintenance.webdavPassword') }}<input v-model="webdavForm.password" type="password" /></label></div><div class="button-row"><button class="button primary" @click="saveWebdav">{{ t('common.save') }}</button><button class="button" @click="webdavAction('check')">{{ t('maintenance.checkWebdav') }}</button><button class="button" @click="webdavAction('backup')">{{ t('maintenance.backupWebdav') }}</button><button class="button danger" @click="webdavAction('restore')">{{ t('maintenance.restoreWebdav') }}</button></div></section>
            <section class="subpanel"><div class="subpanel-heading"><h2>{{ t('maintenance.backup') }}</h2></div><div class="button-row"><button class="button primary" @click="downloadBackup">{{ t('maintenance.downloadBackup') }}</button><label class="button file-button">{{ t('maintenance.restoreUpload') }}<input type="file" accept=".zip,application/zip" @change="uploadRestore" /></label></div></section>
            <section class="subpanel"><div class="subpanel-heading"><h2>{{ t('maintenance.operationList') }}</h2><button class="tool-button" @click="loadOperations">⟳</button></div><div v-if="operations.length" class="operation-list"><span v-for="operation in operations" :key="operation" class="operation-pill"><i class="status-led on"></i>{{ operation }}</span></div><p v-else class="muted">{{ t('maintenance.noOperations') }}</p></section>
          </div>
        </section>

        <section v-else-if="activePage === 'logs'" class="page logs-page">
          <div class="page-toolbar"><div class="page-title"><h1>{{ t('logs.title') }}</h1><span class="count-tag">{{ logTotal }}</span></div><div class="toolbar-main"><label class="search-box log-search"><span>⌕</span><input v-model="logFilter" :placeholder="t('logs.filter')" @keyup.enter="loadLogs(1)" /></label><button class="button" @click="loadLogs(1)">{{ t('logs.load') }}</button><button class="button danger" @click="clearLogs">{{ t('logs.clear') }}</button></div></div>
          <div ref="logPanel" class="log-table-wrap"><table class="data-table log-table"><thead><tr><th>{{ t('logs.time') }}</th><th>{{ t('logs.source') }}</th><th>{{ t('logs.message') }}</th></tr></thead><tbody><tr v-for="(log, index) in logs" :key="`${log.timestamp}-${index}`"><td class="log-time">{{ new Date(log.timestamp).toLocaleTimeString(locale, { hour12: false }) }}</td><td><span class="source-tag">{{ log.source }}</span></td><td class="log-message">{{ log.message }}</td></tr><tr v-if="!logs.length"><td colspan="3" class="empty-row">{{ t('logs.noLogs') }}</td></tr></tbody></table></div>
          <div class="logs-pagination"><span>{{ t('logs.totalRows', { total: logTotal }) }}</span><div class="pagination-controls"><button class="button compact" :disabled="logPage <= 1" @click="changeLogPage(1)">{{ t('logs.firstPage') }}</button><button class="button compact" :disabled="logPage <= 1" @click="changeLogPage(logPage - 1)">{{ t('logs.previousPage') }}</button><strong>{{ t('logs.pageIndicator', { page: logPage, pages: logTotalPages }) }}</strong><button class="button compact" :disabled="logPage >= logTotalPages" @click="changeLogPage(logPage + 1)">{{ t('logs.nextPage') }}</button><button class="button compact" :disabled="logPage >= logTotalPages" @click="changeLogPage(logTotalPages)">{{ t('logs.lastPage') }}</button></div></div>
        </section>
      </main>
    </template>

    <div v-if="contextMenu" class="context-menu" :style="{ left: `${contextMenu.x}px`, top: `${contextMenu.y}px` }" @click.stop>
      <strong class="context-heading">{{ contextMenu.profile.remarks || contextMenu.profile.address }}</strong>
      <button @click="selectProfile(contextMenu.profile)">{{ t('nodes.switch') }}</button><button @click="startSpeedTest('tcping', [contextMenu.profile.indexId])">{{ t('nodes.tcping') }}</button><button @click="startSpeedTest('realping', [contextMenu.profile.indexId])">{{ t('nodes.realping') }}</button><button @click="openEditProfile(contextMenu.profile)">{{ t('common.edit') }}</button><button @click="runProfileAction('copy', [contextMenu.profile.indexId])">{{ t('common.copy') }}</button><button class="danger-text" @click="runProfileAction('delete', [contextMenu.profile.indexId])">{{ t('common.delete') }}</button>
    </div>

    <div v-if="showProfileForm" class="modal-shade" @click.self="showProfileForm = false"><form class="modal-panel wide-modal" @submit.prevent="saveProfile"><div class="modal-head"><h2>{{ t(editingProfileId ? 'nodes.editNode' : 'nodes.addNode') }}</h2><button class="tool-button" type="button" @click="showProfileForm = false">×</button></div><div class="form-grid three-col"><label>{{ t('nodes.type') }}<select v-model="profileForm.configType"><option v-for="kind in protocolTypes" :key="kind">{{ kind }}</option><option value="PolicyGroup">PolicyGroup</option><option value="ProxyChain">ProxyChain</option></select></label><label>{{ t('nodes.coreType') }}<select v-model="profileForm.coreType"><option v-for="core in coreTypes" :key="core">{{ core }}</option></select></label><label>{{ t('nodes.remarks') }}<input v-model="profileForm.remarks" required /></label><label>{{ t('nodes.address') }}<input v-model="profileForm.address" /></label><label>{{ t('nodes.port') }}<input v-model.number="profileForm.port" type="number" min="0" max="65535" /></label><label>{{ t('nodes.network') }}<input v-model="profileForm.network" /></label><label>{{ t('nodes.password') }}<input v-model="profileForm.password" /></label><label>{{ t('nodes.username') }}<input v-model="profileForm.username" /></label><label>{{ t('nodes.streamSecurity') }}<input v-model="profileForm.streamSecurity" /></label><label>{{ t('nodes.sni') }}<input v-model="profileForm.sni" /></label><label>{{ t('nodes.tls') }}<input v-model="profileForm.alpn" /></label><label>{{ t('nodes.fingerprint') }}<input v-model="profileForm.fingerprint" /></label><label>{{ t('nodes.publicKey') }}<input v-model="profileForm.publicKey" /></label><label>{{ t('nodes.shortId') }}<input v-model="profileForm.shortId" /></label><label class="check-inline"><input v-model="profileForm.allowInsecure" type="checkbox" />{{ t('nodes.allowInsecure') }}</label></div><div class="form-grid two-col extra-json-grid"><label>{{ t('nodes.protoExtra') }}<textarea v-model="profileForm.protoExtraText" class="code-area" spellcheck="false"></textarea></label><label>{{ t('nodes.transportExtra') }}<textarea v-model="profileForm.transportExtraText" class="code-area" spellcheck="false"></textarea></label></div><label class="advanced-profile-label">{{ t('nodes.advancedProfileFields') }}<textarea v-model="profileAdvancedJson" class="code-area advanced-profile-json" spellcheck="false"></textarea></label><p v-if="profileModalError" class="inline-error">{{ profileModalError }}</p><div class="modal-actions"><button class="button" type="button" @click="showProfileForm = false">{{ t('common.cancel') }}</button><button class="button primary" type="submit">{{ t('common.save') }}</button></div></form></div>

    <div v-if="showImportForm" class="modal-shade" @click.self="showImportForm = false"><form class="modal-panel" @submit.prevent="importProfiles"><div class="modal-head"><h2>{{ t('nodes.importNodes') }}</h2><button class="tool-button" type="button" @click="showImportForm = false">×</button></div><label>{{ t('subscriptions.source') }}<select v-model="importForm.subscriptionId"><option value="">{{ t('common.allGroups') }}</option><option v-for="group in groups.filter((item) => item.id)" :key="group.id" :value="group.id">{{ group.name }}</option></select></label><label>{{ t('nodes.importContent') }}<textarea v-model="importForm.content" class="code-area import-content" required :placeholder="t('nodes.importHint')"></textarea></label><label class="check-inline"><input v-model="importForm.isSubscription" type="checkbox" />{{ t('nodes.isSubscription') }}</label><div class="modal-actions"><label class="button file-button">{{ t('common.openFile') }}<input type="file" accept=".txt,.json,.conf" @change="readImportFile" /></label><button class="button" type="button" @click="pasteImport">{{ t('common.paste') }}</button><button class="button" type="button" @click="showImportForm = false">{{ t('common.cancel') }}</button><button class="button primary" type="submit">{{ t('common.import') }}</button></div></form></div>

    <div v-if="showSubscriptionForm" class="modal-shade" @click.self="showSubscriptionForm = false"><form class="modal-panel wide-modal" @submit.prevent="saveSubscription"><div class="modal-head"><h2>{{ t(editingSubscriptionId ? 'subscriptions.editSubscription' : 'subscriptions.addSubscription') }}</h2><button class="tool-button" type="button" @click="showSubscriptionForm = false">×</button></div><div class="form-grid two-col"><label>{{ t('subscriptions.name') }}<input v-model="subscriptionForm.remarks" required /></label><label>{{ t('subscriptions.url') }}<input v-model="subscriptionForm.url" required /></label><label>{{ t('subscriptions.moreUrl') }}<input v-model="subscriptionForm.moreUrl" /></label><label>{{ t('subscriptions.interval') }}<input v-model.number="subscriptionForm.autoUpdateInterval" type="number" min="0" /></label><label>{{ t('subscriptions.userAgent') }}<input v-model="subscriptionForm.userAgent" /></label><label>{{ t('subscriptions.convertTarget') }}<input v-model="subscriptionForm.convertTarget" /></label><label>{{ t('subscriptions.filter') }}<input v-model="subscriptionForm.filter" /></label><label>{{ t('subscriptions.sort') }}<input v-model.number="subscriptionForm.sort" type="number" /></label><label>{{ t('subscriptions.prevProfile') }}<input v-model="subscriptionForm.prevProfile" /></label><label>{{ t('subscriptions.nextProfile') }}<input v-model="subscriptionForm.nextProfile" /></label><label>{{ t('subscriptions.preSocksPort') }}<input v-model.number="subscriptionForm.preSocksPort" type="number" /></label><label>{{ t('subscriptions.customCoreType') }}<select v-model="subscriptionForm.customCoreType"><option :value="null">{{ t('common.none') }}</option><option v-for="core in coreTypes" :key="core" :value="core">{{ core }}</option></select></label><label class="wide-field">{{ t('subscriptions.requestHeaders') }}<textarea v-model="subscriptionForm.requestHeaders"></textarea></label><label class="wide-field">{{ t('subscriptions.memo') }}<textarea v-model="subscriptionForm.memo"></textarea></label><label class="check-inline"><input v-model="subscriptionForm.enabled" type="checkbox" />{{ t('subscriptions.enabled') }}</label></div><div class="modal-actions"><button class="button" type="button" @click="showSubscriptionForm = false">{{ t('common.cancel') }}</button><button class="button primary" type="submit">{{ t('common.save') }}</button></div></form></div>

    <div v-if="showRouteForm" class="modal-shade" @click.self="showRouteForm = false"><form class="modal-panel" @submit.prevent="saveRoute"><div class="modal-head"><h2>{{ t(editingRouteId ? 'routing.edit' : 'routing.create') }}</h2><button class="tool-button" type="button" @click="showRouteForm = false">×</button></div><div class="form-grid"><label>{{ t('routing.name') }}<input v-model="routeForm.remarks" required /></label><label>{{ t('routing.url') }}<input v-model="routeForm.url" /></label><label>{{ t('routing.domainStrategy') }}<input v-model="routeForm.domainStrategy" /></label><label>{{ t('routing.domainStrategySingbox') }}<input v-model="routeForm.domainStrategy4Singbox" /></label><label>{{ t('routing.ruleCount') }}<input v-model.number="routeForm.ruleNum" type="number" min="0" /></label><label>{{ t('routing.url') }}<input v-model="routeForm.customRulesetPath4Singbox" /></label><label class="check-inline"><input v-model="routeForm.enabled" type="checkbox" />{{ t('common.enabled') }}</label><label class="check-inline"><input v-model="routeForm.locked" type="checkbox" />{{ t('common.enabled') }}</label><label class="wide-field">{{ t('common.rawJson') }}<textarea v-model="routeForm.ruleSet" class="code-area"></textarea></label></div><div class="modal-actions"><button class="button" type="button" @click="showRouteForm = false">{{ t('common.cancel') }}</button><button class="button primary" type="submit">{{ t('common.save') }}</button></div></form></div>

    <div v-if="showExportDialog" class="modal-shade" @click.self="showExportDialog = false"><section class="modal-panel wide-modal"><div class="modal-head"><h2>{{ t('nodes.exportSelected') }}</h2><button class="tool-button" @click="showExportDialog = false">×</button></div><div class="export-options"><label class="check-inline"><input v-model="exportOptions.includeShareUris" type="checkbox" />{{ t('nodes.includeShareUris') }}</label><label class="check-inline"><input v-model="exportOptions.base64ShareUris" type="checkbox" />{{ t('nodes.base64ShareUris') }}</label><label class="check-inline"><input v-model="exportOptions.includeInnerUri" type="checkbox" />{{ t('nodes.includeInnerUri') }}</label><label class="check-inline"><input v-model="exportOptions.includeClientConfig" type="checkbox" />{{ t('nodes.includeClientConfig') }}</label><button class="button compact" @click="exportSelected">{{ t('common.refresh') }}</button></div><textarea v-model="exportContent" class="code-area export-area" spellcheck="false"></textarea><div class="modal-actions"><button class="button" @click="copyExport">{{ t('common.copy') }}</button><button class="button" @click="downloadExport">{{ t('common.download') }}</button><button class="button primary" @click="showExportDialog = false">{{ t('common.close') }}</button></div></section></div>
    </template>
  </div>
</template>
