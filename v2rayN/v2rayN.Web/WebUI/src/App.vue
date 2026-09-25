<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import AppHeader from './components/AppHeader.vue'
import ConnectionStrip from './components/ConnectionStrip.vue'
import NoticeBar from './components/NoticeBar.vue'
import RuntimeStrip from './components/RuntimeStrip.vue'
import DnsPage from './components/pages/DnsPage.vue'
import LogsPage from './components/pages/LogsPage.vue'
import MaintenancePage from './components/pages/MaintenancePage.vue'
import NodesPage from './components/pages/NodesPage.vue'
import RoutingPage from './components/pages/RoutingPage.vue'
import SettingsPage from './components/pages/SettingsPage.vue'
import SubscriptionsPage from './components/pages/SubscriptionsPage.vue'
import TemplatesPage from './components/pages/TemplatesPage.vue'
import ExportModal from './components/modals/ExportModal.vue'
import ImportProfilesModal from './components/modals/ImportProfilesModal.vue'
import ProfileModal from './components/modals/ProfileModal.vue'
import RouteModal from './components/modals/RouteModal.vue'
import SubscriptionModal from './components/modals/SubscriptionModal.vue'

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

const headerState = reactive({ navItems, brandIconSrc, brandIconTitle, activePage, subscriptions, authenticated, locale, loading })
const runtimeStripState = reactive({ status, currentProfile, activeRoutingId, routes, busy })
const connectionStripState = reactive({ listeners, traffic, status, runtimeVersion })
const noticeState = reactive({ notice, noticeKind })

const nodesPageState = reactive({ filteredProfiles, profiles, selectedGroup, groups, filter, selectedIds, allVisibleSelected, operations, testActions })
const subscriptionsPageState = reactive({ subscriptions, subscriptionUseProxy, selectedGroup })
const routingPageState = reactive({ routes, activeRoutingId, currentRoute, routingForm, routingRules, rulesRaw, ruleImportText, appendRules })
const dnsPageState = reactive({ simpleDnsRaw, dnsProfiles })
const settingsPageState = reactive({ inboundForm, coreForm, appForm, speedForm, settings, coreTypes })
const templatesPageState = reactive({ templates })
const maintenancePageState = reactive({ xrayUpdate, operations, status, webdavForm })
const logsPageState = reactive({ logTotal, logFilter, logs, logPage, logTotalPages })

const headerActions = { navigate, refreshBase, disconnect }
const runtimeStripActions = { activateRoute, coreAction }
const connectionStripActions = { listenerDescription, formatBytes }
const nodesPageActions = { openAddProfile, openImportProfiles, updateSubscriptions, subscriptionUpdateMessageKey, startSpeedTest, runProfileAction, stopSpeedTests, changeGroup, generateGroups, loadProfiles, toggleAllVisible, toggleProfile, sortProfiles, selectProfile, formatDelay, formatBytes, moveSelectedToGroup, moveSelected, moveSelectedPosition, exportSelected, openContext }
const subscriptionsPageActions = { formatDate, subscriptionUpdateMessageKey, updateSubscriptions, openAddSubscription, updateSubscription, shareSubscription, openEditSubscription, deleteSubscription }
const routingPageActions = { importRoutingProfiles, openAddRoute, loadRules, openEditRoute, deleteRoute, applyPreset, saveRoutingStrategies, addRoutingRule, copyRoutingRules, saveRoutingRules, moveRoutingRule, removeRoutingRule, importRoutingRules }
const dnsPageActions = { loadDns, saveSimpleDns, saveDnsProfile }
const settingsPageActions = { saveInbound, saveCoreSettings, saveAppSettings, saveSpeedSettings, saveCoreTypes }
const templatesPageActions = { loadTemplates, saveTemplate }
const maintenancePageActions = { checkXrayUpdate, updateXray, updateGeo, clearStatistics, saveWebdav, webdavAction, downloadBackup, uploadRestore, loadOperations, loadMaintenance }
const logsPageActions = { loadLogs, clearLogs, changeLogPage }

const profileModalState = reactive({ showProfileForm, profileForm, profileAdvancedJson, profileModalError, editingProfileId, protocolTypes, coreTypes })
const profileModalActions = { saveProfile }
const importProfilesModalState = reactive({ showImportForm, importForm, groups })
const importProfilesModalActions = { importProfiles, readImportFile, pasteImport }
const subscriptionModalState = reactive({ showSubscriptionForm, subscriptionForm, editingSubscriptionId, coreTypes })
const subscriptionModalActions = { saveSubscription }
const routeModalState = reactive({ showRouteForm, routeForm, editingRouteId })
const routeModalActions = { saveRoute }
const exportModalState = reactive({ showExportDialog, exportOptions, exportContent })
const exportModalActions = { exportSelected, copyExport, downloadExport }
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

function subscriptionUpdateMessageKey(subscriptionId: string | null, useProxy: boolean): string {
  const scope = subscriptionId ? 'Group' : 'All'
  return `subscriptions.update${scope}${useProxy ? 'ViaProxy' : ''}`
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
      <AppHeader :state="headerState" :actions="headerActions" />
    <section v-if="!authenticated" class="auth-wrap">
      <form class="auth-box" @submit.prevent="login()">
        <div class="auth-title"><img class="brand-glyph" src="/v2rayN.png" alt="" /><div><strong>{{ t('auth.title') }}</strong><small>{{ t('brand') }}</small></div></div>
        <p>{{ t('auth.hint') }}</p>
        <label class="field-label" for="management-key">{{ t('auth.token') }}</label>
        <div class="inline-field"><input id="management-key" v-model="managementKeyDraft" type="password" autocomplete="current-password" :placeholder="t('auth.placeholder')" /><button class="button primary" type="submit">{{ t('auth.connect') }}</button></div>
      </form>
    </section>
      <template v-else>
        <RuntimeStrip :state="runtimeStripState" :actions="runtimeStripActions" />
        <ConnectionStrip :state="connectionStripState" :actions="connectionStripActions" />
        <NoticeBar v-if="notice" :state="noticeState" />
        <main class="workspace">
          <NodesPage v-if="activePage === 'nodes'" :state="nodesPageState" :actions="nodesPageActions" />
          <SubscriptionsPage v-else-if="activePage === 'subscriptions'" :state="subscriptionsPageState" :actions="subscriptionsPageActions" />
          <RoutingPage v-else-if="activePage === 'routing'" :state="routingPageState" :actions="routingPageActions" />
          <DnsPage v-else-if="activePage === 'dns'" :state="dnsPageState" :actions="dnsPageActions" />
          <SettingsPage v-else-if="activePage === 'settings'" :state="settingsPageState" :actions="settingsPageActions" />
          <TemplatesPage v-else-if="activePage === 'templates'" :state="templatesPageState" :actions="templatesPageActions" />
          <MaintenancePage v-else-if="activePage === 'maintenance'" :state="maintenancePageState" :actions="maintenancePageActions" />
          <LogsPage v-else-if="activePage === 'logs'" :state="logsPageState" :actions="logsPageActions" />
        </main>
      </template>
    <div v-if="contextMenu" class="context-menu" :style="{ left: `${contextMenu.x}px`, top: `${contextMenu.y}px` }" @click.stop>
      <strong class="context-heading">{{ contextMenu.profile.remarks || contextMenu.profile.address }}</strong>
      <button @click="selectProfile(contextMenu.profile)">{{ t('nodes.switch') }}</button><button @click="startSpeedTest('tcping', [contextMenu.profile.indexId])">{{ t('nodes.tcping') }}</button><button @click="startSpeedTest('realping', [contextMenu.profile.indexId])">{{ t('nodes.realping') }}</button><button @click="openEditProfile(contextMenu.profile)">{{ t('common.edit') }}</button><button @click="runProfileAction('copy', [contextMenu.profile.indexId])">{{ t('common.copy') }}</button><button class="danger-text" @click="runProfileAction('delete', [contextMenu.profile.indexId])">{{ t('common.delete') }}</button>
    </div>
    <ProfileModal v-if="showProfileForm" :state="profileModalState" :actions="profileModalActions" />
    <ImportProfilesModal v-if="showImportForm" :state="importProfilesModalState" :actions="importProfilesModalActions" />
    <SubscriptionModal v-if="showSubscriptionForm" :state="subscriptionModalState" :actions="subscriptionModalActions" />
    <RouteModal v-if="showRouteForm" :state="routeModalState" :actions="routeModalActions" />
    <ExportModal v-if="showExportDialog" :state="exportModalState" :actions="exportModalActions" />
    </template>
  </div>
</template>
