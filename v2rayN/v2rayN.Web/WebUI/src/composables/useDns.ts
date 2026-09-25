import { reactive, ref } from 'vue'
import type { ApiServices, Dict, ErrorHandler, Notice, Translate } from './types'

export function useDns(options: ApiServices & { t: Translate; showNotice: Notice; showError: ErrorHandler }) {
  const t = options.t
  const simpleDnsRaw = ref('{}')
  const dnsProfiles = ref<Dict[]>([])

  async function loadDns() {
    const [simple, profilesResult] = await Promise.all([
      options.data('/api/settings/dns/simple'),
      options.data('/api/settings/dns/profiles'),
    ])
    simpleDnsRaw.value = JSON.stringify(simple || {}, null, 2)
    dnsProfiles.value = profilesResult || []
  }

  async function saveSimpleDns() {
    try {
      const payload = JSON.parse(simpleDnsRaw.value)
      const result = await options.request('/api/settings/dns/simple', { method: 'PUT', body: payload })
      options.showNotice(options.operationMessage(result))
    } catch (error) {
      if (error instanceof SyntaxError) options.showNotice(t('common.invalidJson'), 'error')
      else options.showError(error)
    }
  }

  async function saveDnsProfile(profile: Dict) {
    try {
      const result = await options.request(`/api/settings/dns/profiles/${encodeURIComponent(options.coreTypeRoute(profile.coreType))}`, {
        method: 'PUT',
        body: {
          remarks: profile.remarks, enabled: profile.enabled, useSystemHosts: profile.useSystemHosts,
          normalDNS: profile.normalDNS, domainStrategy4Freedom: profile.domainStrategy4Freedom,
          domainDNSAddress: profile.domainDNSAddress,
        },
      })
      options.showNotice(options.operationMessage(result))
    } catch (error) { options.showError(error) }
  }

  const dnsPageState = reactive({ simpleDnsRaw, dnsProfiles })

  return { simpleDnsRaw, dnsProfiles, loadDns, dnsPageState, dnsPageActions: { loadDns, saveSimpleDns, saveDnsProfile } }
}
