import { reactive, ref, type Ref } from 'vue'
import type { ApiServices, Dict, ErrorHandler, Notice } from './types'
import type { Translate } from './types'

export function useSettings(options: ApiServices & {
  t: Translate
  showNotice: Notice
  showError: ErrorHandler
  loadStatus: () => Promise<void>
  coreTypes: string[]
  routingForm: Ref<Dict>
}) {
  const settings = ref<Dict>({})
  const inboundForm = ref<Dict>({})
  const coreForm = ref<Dict>({})
  const appForm = ref<Dict>({})
  const speedForm = ref<Dict>({})

  async function loadSettings() {
    settings.value = await options.data('/api/settings') || {}
    settings.value.coreTypes = (settings.value.coreTypes || []).map((mapping: Dict) => ({
      ...mapping,
      coreType: options.canonicalCode(mapping.coreType, options.coreTypes),
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
    options.routingForm.value = {
      domainStrategy: settings.value.domainStrategy || '',
      domainStrategy4Singbox: settings.value.domainStrategy4Singbox || '',
    }
  }

  function parseLines(value: string): string[] {
    return value.split(/\r?\n/).map((line) => line.trim()).filter(Boolean)
  }

  async function saveInbound() {
    try {
      const result = await options.request('/api/settings/inbound', {
        method: 'PUT', body: {
          localPort: Number(inboundForm.value.localPort), secondLocalPortEnabled: inboundForm.value.secondLocalPortEnabled,
          udpEnabled: inboundForm.value.udpEnabled, sniffingEnabled: inboundForm.value.sniffingEnabled,
          destOverride: parseLines(inboundForm.value.destOverrideText || ''), routeOnly: inboundForm.value.routeOnly,
          allowLANConn: inboundForm.value.allowLANConn, newPort4LAN: inboundForm.value.newPort4LAN,
          user: inboundForm.value.user, pass: inboundForm.value.pass,
        },
      })
      options.showNotice(options.operationMessage(result))
      await options.loadStatus()
    } catch (error) { options.showError(error) }
  }

  async function saveCoreSettings() {
    try {
      const { fragmentLengthsText, fragmentDelaysText, ...core } = coreForm.value
      const result = await options.request('/api/settings/core', {
        method: 'PUT', body: {
          ...core,
          fragmentLengths: parseLines(fragmentLengthsText || ''), fragmentDelays: parseLines(fragmentDelaysText || ''),
          mux4RayConcurrency: core.mux4RayConcurrency === '' ? null : Number(core.mux4RayConcurrency),
          mux4RayXudpConcurrency: core.mux4RayXudpConcurrency === '' ? null : Number(core.mux4RayXudpConcurrency),
          mux4SboxMaxConnections: Number(core.mux4SboxMaxConnections || 0), hy2UpMbps: Number(core.hy2UpMbps || 0), hy2DownMbps: Number(core.hy2DownMbps || 0),
        },
      })
      options.showNotice(options.operationMessage(result))
    } catch (error) { options.showError(error) }
  }

  async function saveAppSettings() {
    try {
      const result = await options.request('/api/settings/application', { method: 'PUT', body: { ...appForm.value, geoAutoUpdateInterval: Number(appForm.value.geoAutoUpdateInterval || 0) } })
      options.showNotice(options.operationMessage(result))
    } catch (error) { options.showError(error) }
  }

  async function saveSpeedSettings() {
    try {
      const result = await options.request('/api/settings/speedtest', { method: 'PUT', body: {
        ...speedForm.value,
        speedTestTimeout: Number(speedForm.value.speedTestTimeout), mixedConcurrencyCount: Number(speedForm.value.mixedConcurrencyCount),
        speedTestPageSize: speedForm.value.speedTestPageSize === '' ? null : Number(speedForm.value.speedTestPageSize),
        speedTestDelayInterval: speedForm.value.speedTestDelayInterval === '' ? null : Number(speedForm.value.speedTestDelayInterval),
      } })
      options.showNotice(options.operationMessage(result))
    } catch (error) { options.showError(error) }
  }

  async function saveCoreTypes() {
    try {
      const result = await options.request('/api/settings/core-types', { method: 'PUT', body: { mappings: settings.value.coreTypes || [] } })
      options.showNotice(options.operationMessage(result))
    } catch (error) { options.showError(error) }
  }

  async function saveAllSettings() {
    const { destOverrideText, ...inbound } = inboundForm.value
    const { fragmentLengthsText, fragmentDelaysText, ...core } = coreForm.value
    const sections: Array<{ key: string; path: string; body: Dict }> = [
      {
        key: 'settings.inbound',
        path: '/api/settings/inbound',
        body: { ...inbound, localPort: Number(inbound.localPort), destOverride: parseLines(destOverrideText || '') },
      },
      {
        key: 'settings.core',
        path: '/api/settings/core',
        body: {
          ...core,
          fragmentLengths: parseLines(fragmentLengthsText || ''), fragmentDelays: parseLines(fragmentDelaysText || ''),
          mux4RayConcurrency: core.mux4RayConcurrency === '' ? null : Number(core.mux4RayConcurrency),
          mux4RayXudpConcurrency: core.mux4RayXudpConcurrency === '' ? null : Number(core.mux4RayXudpConcurrency),
          mux4SboxMaxConnections: Number(core.mux4SboxMaxConnections || 0), hy2UpMbps: Number(core.hy2UpMbps || 0), hy2DownMbps: Number(core.hy2DownMbps || 0),
        },
      },
      {
        key: 'settings.application',
        path: '/api/settings/application',
        body: { ...appForm.value, geoAutoUpdateInterval: Number(appForm.value.geoAutoUpdateInterval || 0) },
      },
      {
        key: 'settings.speedtest',
        path: '/api/settings/speedtest',
        body: {
          ...speedForm.value,
          speedTestTimeout: Number(speedForm.value.speedTestTimeout), mixedConcurrencyCount: Number(speedForm.value.mixedConcurrencyCount),
          speedTestPageSize: speedForm.value.speedTestPageSize === '' ? null : Number(speedForm.value.speedTestPageSize),
          speedTestDelayInterval: speedForm.value.speedTestDelayInterval === '' ? null : Number(speedForm.value.speedTestDelayInterval),
        },
      },
      {
        key: 'settings.coreTypes',
        path: '/api/settings/core-types',
        body: { mappings: settings.value.coreTypes || [] },
      },
    ]
    const savedSections: string[] = []
    for (const section of sections) {
      try {
        await options.request(section.path, { method: 'PUT', body: section.body })
        savedSections.push(section.key)
      } catch {
        const completed = savedSections.length
          ? savedSections.map((key) => options.t(key)).join(', ')
          : options.t('common.none')
        options.showNotice(options.t('settings.saveAllFailed', {
          section: options.t(section.key),
          saved: completed,
        }), 'error')
        return { completed: savedSections, failed: section.key }
      }
    }

    try {
      await options.loadStatus()
      options.showNotice(options.t('settings.allSaved'))
    } catch {
      options.showNotice(options.t('settings.settingsRefreshFailed'), 'error')
    }
    return { completed: savedSections, failed: null }
  }

  const settingsPageState = reactive({ inboundForm, coreForm, appForm, speedForm, settings, coreTypes: options.coreTypes })

  return { settings, inboundForm, coreForm, appForm, speedForm, loadSettings, settingsPageState, settingsPageActions: { saveInbound, saveCoreSettings, saveAppSettings, saveSpeedSettings, saveCoreTypes, saveAllSettings } }
}
