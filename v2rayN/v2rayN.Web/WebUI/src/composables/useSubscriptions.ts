import { reactive, ref, type Ref } from 'vue'
import type { ApiServices, Dict, ErrorHandler, Notice, Translate } from './types'

export function useSubscriptions(options: ApiServices & {
  t: Translate
  locale: Ref<string>
  showNotice: Notice
  showError: ErrorHandler
  loadOperations: () => Promise<void>
  loadGroups: () => Promise<void>
  loadProfiles: () => Promise<void>
  selectedGroup: Ref<string>
  coreTypes: string[]
}) {
  const t = options.t
  const subscriptions = ref<Dict[]>([])
  const subscriptionUseProxy = ref(false)
  const subscriptionForm = ref<Dict>({})
  const showSubscriptionForm = ref(false)
  const editingSubscriptionId = ref('')

  async function loadSubscriptions() {
    subscriptions.value = await options.data('/api/subscriptions') || []
  }

  function subscriptionUpdateMessageKey(subscriptionId: string | null, useProxy: boolean): string {
    const scope = subscriptionId ? 'Group' : 'All'
    return `subscriptions.update${scope}${useProxy ? 'ViaProxy' : ''}`
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
    subscriptionForm.value = { ...item, customCoreType: item.customCoreType ? options.canonicalCode(item.customCoreType, options.coreTypes) : null }
    showSubscriptionForm.value = true
  }

  async function saveSubscription() {
    try {
      const body = { ...subscriptionForm.value, autoUpdateInterval: Number(subscriptionForm.value.autoUpdateInterval || 0), sort: Number(subscriptionForm.value.sort || 0) }
      const result = await options.request(editingSubscriptionId.value ? `/api/subscriptions/${encodeURIComponent(editingSubscriptionId.value)}` : '/api/subscriptions', {
        method: editingSubscriptionId.value ? 'PUT' : 'POST', body,
      })
      showSubscriptionForm.value = false
      options.showNotice(options.operationMessage(result, editingSubscriptionId.value ? 'subscriptions.saved' : 'subscriptions.added'))
      await Promise.all([loadSubscriptions(), options.loadGroups()])
      if (!editingSubscriptionId.value && result.data?.id) await updateSubscription(result.data.id, subscriptionUseProxy.value)
    } catch (error) { options.showError(error) }
  }

  async function deleteSubscription(item: Dict) {
    if (!window.confirm(t('subscriptions.deleteConfirm', { name: item.remarks }))) return
    try {
      const result = await options.request(`/api/subscriptions/${encodeURIComponent(item.id)}`, { method: 'DELETE' })
      options.showNotice(options.operationMessage(result, 'subscriptions.deleted'))
      await loadSubscriptions()
      await options.loadGroups()
      await options.loadProfiles()
    } catch (error) { options.showError(error) }
  }

  async function updateSubscription(id: string, useProxy = subscriptionUseProxy.value) {
    try {
      const result = await options.request(`/api/subscriptions/${encodeURIComponent(id)}/update?useProxy=${useProxy}`, { method: 'POST' })
      options.showNotice(options.operationMessage(result, 'subscriptions.updateStarted'))
      await options.loadOperations()
    } catch (error) { options.showError(error) }
  }

  async function updateSubscriptions(subscriptionId: string | null, useProxy = subscriptionUseProxy.value) {
    try {
      const result = await options.request('/api/subscriptions/update', { method: 'POST', body: { subscriptionId, useProxy } })
      options.showNotice(options.operationMessage(result, 'subscriptions.updateStarted'))
      await options.loadOperations()
    } catch (error) { options.showError(error) }
  }

  async function shareSubscription(item: Dict) {
    try {
      const share = await options.data(`/api/subscriptions/${encodeURIComponent(item.id)}/share`)
      await navigator.clipboard.writeText(share.url)
      options.showNotice(t('subscriptions.shareCopied'))
    } catch (error) { options.showError(error) }
  }

  function formatDate(epochSeconds: number) {
    if (!epochSeconds) return '—'
    return new Intl.DateTimeFormat(options.locale.value, { dateStyle: 'short', timeStyle: 'short' }).format(new Date(epochSeconds * 1000))
  }

  const subscriptionsPageState = reactive({ subscriptions, subscriptionUseProxy, selectedGroup: options.selectedGroup })
  const subscriptionModalState = reactive({ showSubscriptionForm, subscriptionForm, editingSubscriptionId, coreTypes: options.coreTypes })

  return {
    subscriptions, subscriptionUseProxy, showSubscriptionForm, loadSubscriptions, subscriptionsPageState, subscriptionModalState,
    subscriptionUpdateMessageKey,
    subscriptionsPageActions: { formatDate, subscriptionUpdateMessageKey, updateSubscriptions, openAddSubscription, updateSubscription, shareSubscription, openEditSubscription, deleteSubscription },
    subscriptionModalActions: { saveSubscription },
  }
}
