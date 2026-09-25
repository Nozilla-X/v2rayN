import { onUnmounted, ref, watch, type Ref } from 'vue'
import type { RequestApi, ErrorHandler, Notice, Translate } from './types'

export function useSession(options: {
  token: Ref<string>
  authenticated: Ref<boolean>
  loading: Ref<boolean>
  notice: Ref<string>
  request: RequestApi
  t: Translate
  showNotice: Notice
  showError: ErrorHandler
  closeEvents: () => void
  openEvents: () => void
  refreshData: () => Promise<void>
  loadConnectedData: () => Promise<void>
  resetSessionData: () => void
  loadStatus: () => Promise<void>
  loadProfiles: () => Promise<void>
}) {
  const t = options.t
  const managementKeyDraft = ref('')
  const setupStatusReady = ref(false)
  const setupRequired = ref(false)
  const setupAllowedFromRequest = ref(false)
  const setupKey = ref('')
  const setupConfirmKey = ref('')
  const setupSubmitting = ref(false)
  const setupError = ref('')
  let refreshTimer: ReturnType<typeof setInterval> | undefined

  async function loadSetupStatus() {
    try {
      const response = await fetch('/api/setup/status')
      if (!response.ok) throw new Error(`${response.status}`)
      const payload = await response.json()
      const setupStatus = payload?.data ?? payload
      setupRequired.value = Boolean(setupStatus?.setupRequired)
      setupAllowedFromRequest.value = Boolean(setupStatus?.setupAllowedFromThisRequest)
    } catch (error) {
      options.showError(error)
    } finally {
      setupStatusReady.value = true
    }
  }

  async function refreshBase() {
    if (!options.token.value || options.loading.value) return
    options.loading.value = true
    try {
      await options.refreshData()
      options.authenticated.value = true
    } catch (error) {
      if (options.authenticated.value) options.showError(error)
      else throw error
    } finally {
      options.loading.value = false
    }
  }

  async function login() {
    const managementKey = managementKeyDraft.value
    if (!managementKey) {
      options.showNotice(t('auth.tokenRequired'), 'error')
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
        options.showNotice(t('auth.rateLimited'), 'error')
        return
      }
      if (!response.ok || payload?.success !== true || !payload?.data?.token) {
        options.showNotice(t('auth.loginFailed'), 'error')
        return
      }

      managementKeyDraft.value = ''
      await connectWithSession(payload.data.token)
    } catch {
      options.showNotice(t('auth.connectFailed'), 'error')
    }
  }

  async function connectWithSession(sessionToken: string) {
    options.token.value = sessionToken
    try {
      await refreshBase()
      if (options.authenticated.value) {
        localStorage.setItem('v2rayn-web-token', sessionToken)
        options.openEvents()
        await options.loadConnectedData()
        options.showNotice(t('auth.connected'))
      }
    } catch (error) {
      clearSession()
      options.showError(error)
      if (!options.notice.value) options.showNotice(t('auth.connectFailed'), 'error')
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
    options.closeEvents()
    options.token.value = ''
    localStorage.removeItem('v2rayn-web-token')
    options.authenticated.value = false
    options.resetSessionData()
  }

  async function disconnect() {
    try {
      await options.request('/api/auth/logout', { method: 'POST' })
    } catch {
      // Clear the local session even when the backend is already unavailable.
    }
    clearSession()
  }

  watch(options.authenticated, (connected) => {
    clearInterval(refreshTimer)
    if (connected) {
      refreshTimer = setInterval(() => {
        void Promise.all([options.loadStatus(), options.loadProfiles()]).catch(options.showError)
      }, 8000)
    } else {
      options.closeEvents()
    }
  })

  onUnmounted(() => clearInterval(refreshTimer))

  return {
    token: options.token, authenticated: options.authenticated, loading: options.loading,
    managementKeyDraft, setupStatusReady, setupRequired, setupAllowedFromRequest, setupKey, setupConfirmKey, setupSubmitting, setupError,
    loadSetupStatus, refreshBase, login, connectWithSession, configureManagementKey, clearSession, disconnect,
  }
}
