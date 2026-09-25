<script setup lang="ts">
import { computed, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import AppHeader from './components/AppHeader.vue'
import ConnectionStrip from './components/ConnectionStrip.vue'
import FlyoutMenu from './components/FlyoutMenu.vue'
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
import RouteRuleModal from './components/modals/RouteRuleModal.vue'
import SubscriptionModal from './components/modals/SubscriptionModal.vue'
import { useApi } from './composables/useApi'
import { useDns } from './composables/useDns'
import { useEvents } from './composables/useEvents'
import { useLogs } from './composables/useLogs'
import { useMaintenance } from './composables/useMaintenance'
import { useProfiles } from './composables/useProfiles'
import { useRouting } from './composables/useRouting'
import { useRuntime } from './composables/useRuntime'
import { useSession } from './composables/useSession'
import { useSettings } from './composables/useSettings'
import { useSubscriptions } from './composables/useSubscriptions'
import { useTemplates } from './composables/useTemplates'
import type { ApiError, Dict } from './composables/types'

const { t, locale } = useI18n()
const sessionToken = ref(localStorage.getItem('v2rayn-web-token') || '')
const authenticated = ref(false)
const loading = ref(false)
const activePage = ref('nodes')
const contextMenu = ref<Dict | null>(null)
const notice = ref('')
const noticeKind = ref<'success' | 'error'>('success')
let noticeTimer: ReturnType<typeof setTimeout> | undefined

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

let clearSession = () => {}
const api = useApi({ getToken: () => sessionToken.value, onUnauthorized: () => clearSession(), translateKey })
const runtime = useRuntime({ ...api, showNotice, showError })
const profiles = useProfiles({
  ...api, t, showNotice, showError,
  loadStatus: runtime.loadStatus, loadOperations: runtime.loadOperations,
  busy: runtime.busy, operations: runtime.operations, contextMenu,
})
const subscriptions = useSubscriptions({
  ...api, t, locale, showNotice, showError,
  loadOperations: runtime.loadOperations, loadGroups: profiles.loadGroups, loadProfiles: profiles.loadProfiles,
  selectedGroup: profiles.selectedGroup, groups: profiles.groups, coreTypes: profiles.coreTypes,
})
const routing = useRouting({ ...api, t, showNotice, showError, loadStatus: runtime.loadStatus })
const dns = useDns({ ...api, t, showNotice, showError })
const settings = useSettings({ ...api, t, showNotice, showError, loadStatus: runtime.loadStatus, coreTypes: profiles.coreTypes, routingForm: routing.routingForm })
const templates = useTemplates({ ...api, showNotice, showError })
const maintenance = useMaintenance({
  ...api, t, translateKey, showNotice, showError, token: sessionToken,
  status: runtime.status, operations: runtime.operations, loadOperations: runtime.loadOperations, loadProfiles: profiles.loadProfiles,
})
const logs = useLogs({ ...api, t, showNotice, showError })
const events = useEvents({
  token: sessionToken, activePage, status: runtime.status,
  logs: logs.logs, logTotal: logs.logTotal, logPage: logs.logPage, logPageSize: logs.logPageSize,
  request: api.request, t, showNotice, matchesLogFilter: logs.matchesLogFilter,
  loadGroups: profiles.loadGroups, loadProfiles: profiles.loadProfiles, loadSubscriptions: subscriptions.loadSubscriptions,
  loadStatus: runtime.loadStatus, loadOperations: runtime.loadOperations,
})

async function loadPageData() {
  if (activePage.value === 'routing') await routing.loadRouting()
  if (activePage.value === 'dns') await dns.loadDns()
  if (activePage.value === 'settings') await settings.loadSettings()
  if (activePage.value === 'templates') await templates.loadTemplates()
  if (activePage.value === 'maintenance') await maintenance.loadMaintenance()
  if (activePage.value === 'logs') await logs.loadLogs()
}

async function loadConnectedData() {
  await Promise.all([settings.loadSettings(), routing.loadRouting(), loadPageData()])
  await logs.loadLogs()
}

const session = useSession({
  token: sessionToken, authenticated, loading, notice, request: api.request, t, showNotice, showError,
  closeEvents: events.closeEvents, openEvents: events.openEvents,
  refreshData: async () => {
    await profiles.loadGroups()
    await Promise.all([runtime.loadStatus(), profiles.loadProfiles(), subscriptions.loadSubscriptions()])
  },
  loadConnectedData,
  resetSessionData: () => {
    runtime.status.value = null
    profiles.profiles.value = []
    profiles.groups.value = []
    subscriptions.subscriptions.value = []
  },
  loadStatus: runtime.loadStatus,
  loadProfiles: profiles.loadProfiles,
})
clearSession = session.clearSession

const {
  managementKeyDraft, setupStatusReady, setupRequired, setupAllowedFromRequest,
  setupKey, setupConfirmKey, setupSubmitting, setupError,
  login, configureManagementKey, refreshBase, disconnect,
} = session
const {
  showProfileForm, showImportForm, showExportDialog, openEditProfile,
} = profiles
const { showSubscriptionForm } = subscriptions
const { showRouteForm, activateRoute } = routing

const currentProfile = computed(() => profiles.profiles.value.find((profile) => profile.isCurrent) || null)
const brandIconMode = computed(() => runtime.status.value?.coreRunning ? 'proxy' : 'off')
const brandIconSrc = computed(() => ({ proxy: '/NotifyIcon2.ico', off: '/NotifyIcon1.ico' })[brandIconMode.value])
const brandIconTitle = computed(() => t(`brandState.${brandIconMode.value}`))
const pageTitle = computed(() => {
  const item = navItems.find((entry) => entry.id === activePage.value)
  return item ? t(item.key) : t('nav.nodes')
})

function formatBytes(value: number | null | undefined) {
  let amount = Number(value || 0)
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let unit = 0
  while (amount >= 1024 && unit < units.length - 1) { amount /= 1024; unit += 1 }
  return `${amount.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`
}

const emptyProfileExportOptions = {
  includeShareUris: false,
  base64ShareUris: false,
  includeInnerUri: false,
  includeClientConfig: false,
}

type ProfileExportOptions = Partial<typeof emptyProfileExportOptions>

async function openProfileExport(options: ProfileExportOptions, profileIds?: string[]) {
  Object.assign(profiles.exportModalState.exportOptions, emptyProfileExportOptions, options)
  const previousSelection = profiles.selectedIds.value
  if (profileIds) profiles.selectedIds.value = profileIds
  try {
    await profiles.nodesPageActions.exportSelected()
  } finally {
    if (profileIds) profiles.selectedIds.value = previousSelection
  }
}

async function copyProfileExport(options: ProfileExportOptions, profileIds?: string[]) {
  await openProfileExport(options, profileIds)
  if (!profiles.exportModalState.showExportDialog) return
  await profiles.exportModalActions.copyExport()
  profiles.exportModalState.showExportDialog = false
}

function contextMenuStyle(menu: Dict) {
  const left = Math.max(0, Math.min(Number(menu.x) || 0, window.innerWidth - 250))
  const top = Math.max(8, Math.min(Number(menu.y) || 0, window.innerHeight - 580))
  return { left: `${left}px`, top: `${top}px` }
}

const headerState = reactive({ navItems, brandIconSrc, brandIconTitle, activePage, subscriptions: subscriptions.subscriptions, authenticated, locale, loading })
const headerActions = { navigate, refreshBase, disconnect }
const runtimeStripState = reactive({ status: runtime.status, currentProfile, activeRoutingId: routing.activeRoutingId, routes: routing.routes, busy: runtime.busy })
const runtimeStripActions = { activateRoute, coreAction: runtime.coreAction }
const connectionStripState = runtime.connectionStripState
const connectionStripActions = { listenerDescription: runtime.listenerDescription, formatBytes }
const noticeState = reactive({ notice, noticeKind })

const nodesPageState = Object.assign(profiles.nodesPageState, { subscriptions: subscriptions.subscriptions })
const nodesPageActions = {
  ...profiles.nodesPageActions,
  updateSubscriptions: subscriptions.subscriptionsPageActions.updateSubscriptions,
  subscriptionUpdateMessageKey: subscriptions.subscriptionUpdateMessageKey,
  openAddSubscription: subscriptions.subscriptionsPageActions.openAddSubscription,
  openEditSubscription: subscriptions.subscriptionsPageActions.openEditSubscription,
  shareSelected: () => openProfileExport({ includeShareUris: true }),
  shareProfile: (profileId: string) => openProfileExport({ includeShareUris: true }, [profileId]),
  exportFullConfig: () => openProfileExport({ includeClientConfig: true }),
  exportProfileConfig: (profileId: string) => openProfileExport({ includeClientConfig: true }, [profileId]),
  exportFullConfigToClipboard: () => copyProfileExport({ includeClientConfig: true }),
  exportProfileConfigToClipboard: (profileId: string) => copyProfileExport({ includeClientConfig: true }, [profileId]),
  exportShareLinksToClipboard: () => copyProfileExport({ includeShareUris: true }),
  exportShareLinksBase64: () => copyProfileExport({ base64ShareUris: true }),
  exportInnerUris: () => copyProfileExport({ includeInnerUri: true }),
  formatBytes,
}
const subscriptionsPageState = subscriptions.subscriptionsPageState
const subscriptionsPageActions = subscriptions.subscriptionsPageActions
const routingPageState = routing.routingPageState
const routingPageActions = routing.routingPageActions
const dnsPageState = dns.dnsPageState
const dnsPageActions = dns.dnsPageActions
const settingsPageState = settings.settingsPageState
const settingsPageActions = settings.settingsPageActions
const templatesPageState = templates.templatesPageState
const templatesPageActions = templates.templatesPageActions
const maintenancePageState = maintenance.maintenancePageState
const maintenancePageActions = maintenance.maintenancePageActions
const logsPageState = logs.logsPageState
const logsPageActions = logs.logsPageActions

const profileModalState = profiles.profileModalState
const profileModalActions = profiles.profileModalActions
const importProfilesModalState = profiles.importProfilesModalState
const importProfilesModalActions = profiles.importProfilesModalActions
const subscriptionModalState = subscriptions.subscriptionModalState
const subscriptionModalActions = subscriptions.subscriptionModalActions
const routeModalState = routing.routeModalState
const routeModalActions = routing.routeModalActions
const ruleModalState = routing.ruleModalState
const ruleModalActions = routing.ruleModalActions
const exportModalState = profiles.exportModalState
const exportModalActions = profiles.exportModalActions

async function navigate(page: string) {
  activePage.value = page
  contextMenu.value = null
  profiles.selectedIds.value = []
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

onMounted(async () => {
  document.addEventListener('keydown', closeDialogsOnEscape)
  await session.loadSetupStatus()
  if (setupRequired.value || !sessionToken.value) return
  try {
    await refreshBase()
    if (authenticated.value) {
      events.openEvents()
      await Promise.all([settings.loadSettings(), routing.loadRouting(), loadPageData()])
      await logs.loadLogs()
    }
  } catch (error) { showError(error) }
})

onUnmounted(() => {
  document.removeEventListener('keydown', closeDialogsOnEscape)
  clearTimeout(noticeTimer)
})

function closeDialogsOnEscape(event: KeyboardEvent) {
  if (event.key !== 'Escape') return
  contextMenu.value = null
  profiles.showProfileForm.value = false
  profiles.showImportForm.value = false
  profiles.showExportDialog.value = false
  subscriptions.showSubscriptionForm.value = false
  routing.showRouteForm.value = false
  routing.ruleModalState.showRuleForm = false
}
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
    <div v-if="contextMenu" class="context-menu" :style="contextMenuStyle(contextMenu)" @click="contextMenu = null">
      <button :disabled="contextMenu.profile.isCurrent" @click="nodesPageActions.selectProfile(contextMenu.profile)">{{ contextMenu.profile.isCurrent ? t('nodes.current') : t('nodes.switch') }}<span class="menu-shortcut">Enter</span></button>
      <button @click="openEditProfile(contextMenu.profile)">{{ t('common.edit') }}<span class="menu-shortcut">Ctrl+D</span></button>
      <button :disabled="!nodesPageState.selectedIds.length" @click="nodesPageActions.runProfileAction('copy')">{{ t('nodes.copySelected') }}</button>
      <button class="danger-text" :disabled="!nodesPageState.selectedIds.length" @click="nodesPageActions.runProfileAction('delete')">{{ t('nodes.removeSelected') }}<span class="menu-shortcut">Back</span></button>
      <button @click="nodesPageActions.runProfileAction('deduplicate')">{{ t('nodes.deduplicate') }}</button>
      <button @click="nodesPageActions.runProfileAction('remove-invalid')">{{ t('nodes.removeInvalid') }}</button>
      <div class="context-separator"></div>
      <button @click="nodesPageActions.startSpeedTest('tcping', [contextMenu.profile.indexId])">{{ t('nodes.tcping') }}<span class="menu-shortcut">Ctrl+O</span></button>
      <button @click="nodesPageActions.startSpeedTest('realping', [contextMenu.profile.indexId])">{{ t('nodes.realping') }}<span class="menu-shortcut">Ctrl+R</span></button>
      <button @click="nodesPageActions.startSpeedTest('speedtest', [contextMenu.profile.indexId])">{{ t('nodes.speedtest') }}<span class="menu-shortcut">Ctrl+T</span></button>
      <button @click="nodesPageActions.startSpeedTest('udpTest', [contextMenu.profile.indexId])">{{ t('nodes.udp') }}</button>
      <button @click="nodesPageActions.sortProfiles('DelayVal')">{{ t('nodes.sortByTestResults') }}</button>
      <div class="context-separator"></div>
      <FlyoutMenu context :label="t('nodes.moveGroup')" :disabled="!nodesPageState.selectedIds.length" @select="contextMenu = null">
        <button v-for="group in nodesPageState.groups" :key="group.id || 'all-target'" class="action-menu-item" role="menuitem" :disabled="!nodesPageState.selectedIds.length" @click="nodesPageActions.moveSelectedToGroup(group.id)">{{ group.name || t('common.allGroups') }}</button>
      </FlyoutMenu>
      <FlyoutMenu context :label="t('nodes.move')" :disabled="!nodesPageState.selectedIds.length" @select="contextMenu = null">
        <button class="action-menu-item" role="menuitem" @click="nodesPageActions.moveSelected('top')">{{ t('nodes.top') }}<span class="menu-shortcut">T</span></button>
        <button class="action-menu-item" role="menuitem" @click="nodesPageActions.moveSelected('up')">{{ t('nodes.up') }}<span class="menu-shortcut">U</span></button>
        <button class="action-menu-item" role="menuitem" @click="nodesPageActions.moveSelected('down')">{{ t('nodes.down') }}<span class="menu-shortcut">D</span></button>
        <button class="action-menu-item" role="menuitem" @click="nodesPageActions.moveSelected('bottom')">{{ t('nodes.bottom') }}<span class="menu-shortcut">B</span></button>
      </FlyoutMenu>
      <button :disabled="!nodesPageState.filteredProfiles.length" @click="!nodesPageState.allVisibleSelected && nodesPageActions.toggleAllVisible()">{{ t('nodes.selectAll') }}<span class="menu-shortcut">Ctrl+A</span></button>
      <div class="context-separator"></div>
      <button :disabled="!nodesPageState.selectedIds.length" @click="nodesPageActions.shareSelected">{{ t('nodes.shareProfile') }}<span class="menu-shortcut">Ctrl+F</span></button>
      <FlyoutMenu context :label="t('nodes.exportMenu')" :disabled="!nodesPageState.selectedIds.length" @select="contextMenu = null">
        <button class="action-menu-item" role="menuitem" :disabled="!nodesPageState.selectedIds.length" @click="nodesPageActions.exportFullConfig">{{ t('nodes.exportFullConfig') }}</button>
        <button class="action-menu-item" role="menuitem" :disabled="!nodesPageState.selectedIds.length" @click="nodesPageActions.exportFullConfigToClipboard">{{ t('nodes.exportFullConfigClipboard') }}</button>
        <div class="action-menu-separator" role="separator"></div>
        <button class="action-menu-item" role="menuitem" :disabled="!nodesPageState.selectedIds.length" @click="nodesPageActions.exportShareLinksToClipboard">{{ t('nodes.exportShareLinkClipboard') }}<span class="menu-shortcut">Ctrl+C</span></button>
        <button class="action-menu-item" role="menuitem" :disabled="!nodesPageState.selectedIds.length" @click="nodesPageActions.exportShareLinksBase64">{{ t('nodes.exportShareLinkBase64') }}</button>
        <button class="action-menu-item" role="menuitem" :disabled="!nodesPageState.selectedIds.length" @click="nodesPageActions.exportInnerUris">{{ t('nodes.exportInnerUri') }}</button>
      </FlyoutMenu>
      <div class="context-separator"></div>
      <FlyoutMenu context :label="t('nodes.generatePolicyGroups')" :disabled="!nodesPageState.selectedGroup" @select="contextMenu = null">
        <button class="action-menu-item" role="menuitem" :disabled="!nodesPageState.selectedGroup" @click="nodesPageActions.generateGroups(false)">{{ t('nodes.allProfiles') }}</button>
        <button class="action-menu-item" role="menuitem" :disabled="!nodesPageState.selectedGroup || !nodesPageState.profiles.length" @click="nodesPageActions.generateGroups(true)">{{ t('nodes.generateRegionGroups') }}</button>
      </FlyoutMenu>
      <div class="context-separator"></div>
      <div class="context-web-only-label">{{ t('nodes.webOnlyActions') }}</div>
      <button :disabled="!nodesPageState.selectedIds.length" @click="nodesPageActions.moveSelectedPosition">{{ t('nodes.position') }}…</button>
      <button :disabled="!nodesPageState.operations.includes('speedtest')" @click="nodesPageActions.stopSpeedTests">{{ t('nodes.stopTest') }}</button>
      <button :disabled="!nodesPageState.selectedIds.length" @click="nodesPageActions.exportSelected">{{ t('nodes.customExport') }}…</button>
    </div>
    <ProfileModal v-if="showProfileForm" :state="profileModalState" :actions="profileModalActions" />
    <ImportProfilesModal v-if="showImportForm" :state="importProfilesModalState" :actions="importProfilesModalActions" />
    <SubscriptionModal v-if="showSubscriptionForm" :state="subscriptionModalState" :actions="subscriptionModalActions" />
    <RouteModal v-if="showRouteForm" :state="routeModalState" :actions="routeModalActions" />
    <RouteRuleModal v-if="routingPageState && ruleModalState.showRuleForm" :state="ruleModalState" :actions="ruleModalActions" />
    <ExportModal v-if="showExportDialog" :state="exportModalState" :actions="exportModalActions" />
    </template>
  </div>
</template>
