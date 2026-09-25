import { computed, reactive, ref } from 'vue'
import type { ApiServices, Dict, ErrorHandler, Notice, Translate } from './types'

export function useRouting(options: ApiServices & {
  t: Translate
  showNotice: Notice
  showError: ErrorHandler
  loadStatus: () => Promise<void>
}) {
  const t = options.t
  const activeRoutingId = ref('')
  const routes = ref<Dict[]>([])
  const routingRules = ref<Dict[]>([])
  const rulesRaw = ref('[]')
  const ruleImportText = ref('')
  const appendRules = ref(false)
  const routingForm = ref<Dict>({})
  const routeForm = ref<Dict>({})
  const showRouteForm = ref(false)
  const editingRouteId = ref('')
  const currentRoute = computed(() => routes.value.find((route) => route.isActive) || null)

  async function loadRouting() {
    routes.value = await options.data('/api/settings/routing-profiles') || []
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
    routingRules.value = await options.data(`/api/settings/routing-profiles/${encodeURIComponent(id)}/rules`) || []
    rulesRaw.value = JSON.stringify(routingRules.value, null, 2)
  }

  async function activateRoute(id: string) {
    if (!id) return
    try {
      const result = await options.request(`/api/settings/routing-profiles/${encodeURIComponent(id)}/activate`, { method: 'POST' })
      options.showNotice(options.operationMessage(result))
      await Promise.all([loadRouting(), options.loadStatus()])
    } catch (error) { options.showError(error) }
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
      const result = await options.request(editingRouteId.value ? `/api/settings/routing-profiles/${encodeURIComponent(editingRouteId.value)}` : '/api/settings/routing-profiles', {
        method: editingRouteId.value ? 'PUT' : 'POST', body: routeForm.value,
      })
      showRouteForm.value = false
      options.showNotice(options.operationMessage(result))
      await loadRouting()
    } catch (error) { options.showError(error) }
  }

  async function deleteRoute(route: Dict) {
    if (!window.confirm(t('common.confirmDelete'))) return
    try {
      const result = await options.request(`/api/settings/routing-profiles/${encodeURIComponent(route.id)}`, { method: 'DELETE' })
      options.showNotice(options.operationMessage(result))
      await loadRouting()
    } catch (error) { options.showError(error) }
  }

  async function importRoutingProfiles() {
    try {
      const result = await options.request('/api/settings/routing-profiles/import', { method: 'POST' })
      options.showNotice(options.operationMessage(result))
      await loadRouting()
    } catch (error) { options.showError(error) }
  }

  async function saveRoutingRules() {
    try {
      const parsed = JSON.parse(rulesRaw.value)
      const result = await options.request(`/api/settings/routing-profiles/${encodeURIComponent(activeRoutingId.value)}/rules`, { method: 'PUT', body: parsed })
      options.showNotice(options.operationMessage(result))
      await loadRules()
    } catch (error) {
      if (error instanceof SyntaxError) options.showNotice(t('common.invalidJson'), 'error')
      else options.showError(error)
    }
  }

  async function importRoutingRules() {
    try {
      const result = await options.request(`/api/settings/routing-profiles/${encodeURIComponent(activeRoutingId.value)}/rules/import`, {
        method: 'POST', body: { content: ruleImportText.value, append: appendRules.value },
      })
      ruleImportText.value = ''
      options.showNotice(options.operationMessage(result))
      await loadRules()
    } catch (error) { options.showError(error) }
  }

  async function addRoutingRule() {
    const rules = [...routingRules.value, { id: crypto.randomUUID(), enabled: true, type: 'field', remarks: '', outboundTag: 'proxy' }]
    try {
      const result = await options.request(`/api/settings/routing-profiles/${encodeURIComponent(activeRoutingId.value)}/rules`, { method: 'PUT', body: rules })
      options.showNotice(options.operationMessage(result))
      await loadRules()
    } catch (error) { options.showError(error) }
  }

  async function removeRoutingRule(rule: Dict) {
    try {
      const result = await options.request(`/api/settings/routing-profiles/${encodeURIComponent(activeRoutingId.value)}/rules/${encodeURIComponent(rule.id)}`, { method: 'DELETE' })
      options.showNotice(options.operationMessage(result))
      await loadRules()
    } catch (error) { options.showError(error) }
  }

  async function moveRoutingRule(rule: Dict, direction: string) {
    try {
      const result = await options.request(`/api/settings/routing-profiles/${encodeURIComponent(activeRoutingId.value)}/rules/move`, {
        method: 'POST', body: { ruleId: rule.id, direction, position: -1 },
      })
      options.showNotice(options.operationMessage(result))
      await loadRules()
    } catch (error) { options.showError(error) }
  }

  async function copyRoutingRules() {
    try {
      await navigator.clipboard.writeText(rulesRaw.value)
      options.showNotice(t('common.copySuccess'))
    } catch { options.showNotice(t('common.clipboardUnavailable'), 'error') }
  }

  async function saveRoutingStrategies() {
    try {
      const result = await options.request('/api/settings/routing', { method: 'PUT', body: routingForm.value })
      options.showNotice(options.operationMessage(result))
    } catch (error) { options.showError(error) }
  }

  async function applyPreset(preset: string) {
    try {
      const result = await options.request(`/api/settings/regional-presets/${preset}`, { method: 'POST' })
      options.showNotice(options.operationMessage(result))
      await loadRouting()
    } catch (error) { options.showError(error) }
  }

  const routingPageState = reactive({ routes, activeRoutingId, currentRoute, routingForm, routingRules, rulesRaw, ruleImportText, appendRules })
  const routeModalState = reactive({ showRouteForm, routeForm, editingRouteId })

  return {
    activeRoutingId, routes, routingForm, showRouteForm, loadRouting, loadRules, routingPageState, routeModalState,
    activateRoute,
    routingPageActions: { importRoutingProfiles, openAddRoute, loadRules, openEditRoute, deleteRoute, applyPreset, saveRoutingStrategies, addRoutingRule, copyRoutingRules, saveRoutingRules, moveRoutingRule, removeRoutingRule, importRoutingRules },
    routeModalActions: { saveRoute },
  }
}
