<script setup>
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'

const token = ref(localStorage.getItem('v2rayn-web-token') || '')
const tokenDraft = ref(token.value)
const authenticated = ref(false)
const loading = ref(false)
const busyAction = ref('')
const status = ref(null)
const profiles = ref([])
const subscriptions = ref([])
const logs = ref([])
const filter = ref('')
const subscriptionFilter = ref('')
const activeTab = ref('overview')
const notice = ref('')
const noticeKind = ref('success')
const showSubscriptionForm = ref(false)
const editingSubscriptionId = ref('')
const subscriptionForm = ref({ remarks: '', url: '', enabled: true })
const logPanel = ref(null)
let refreshTimer
let eventSource
let noticeTimer

const currentProfile = computed(() => profiles.value.find((item) => item.isCurrent) || null)
const filteredProfiles = computed(() => {
  const query = filter.value.trim().toLowerCase()
  if (!query) return profiles.value
  return profiles.value.filter((item) => [item.remarks, item.address, item.protocol, item.subscriptionName]
    .some((value) => String(value || '').toLowerCase().includes(query)))
})
const filteredSubscriptions = computed(() => {
  const query = subscriptionFilter.value.trim().toLowerCase()
  if (!query) return subscriptions.value
  return subscriptions.value.filter((item) => `${item.remarks} ${item.url}`.toLowerCase().includes(query))
})
const online = computed(() => authenticated.value && status.value !== null)
const listenerIsRunning = computed(() => status.value?.listeners?.some((listener) => listener.listening) ?? false)
const listenerLabel = computed(() => {
  const listener = status.value?.listeners?.[0]
  return listener?.port ? `:${listener.port}` : '未配置'
})
const listenerStatus = computed(() => {
  const listener = status.value?.listeners?.[0]
  return listener?.port ? `${listener.listening ? '监听中' : '未监听'} · 端口 ${listener.port}` : '没有可用监听端口'
})
const runtimeName = computed(() => {
  const runtime = status.value?.runtime || ''
  return runtime.split('|').slice(-1)[0]?.trim() || '—'
})

function notify(message, kind = 'success') {
  notice.value = message
  noticeKind.value = kind
  clearTimeout(noticeTimer)
  noticeTimer = setTimeout(() => { notice.value = '' }, 4200)
}

async function request(path, options = {}) {
  const response = await fetch(path, {
    ...options,
    headers: {
      ...(options.body ? { 'Content-Type': 'application/json' } : {}),
      ...(token.value ? { Authorization: `Bearer ${token.value}` } : {}),
      ...options.headers,
    },
  })
  if (response.status === 401) {
    authenticated.value = false
    closeEvents()
    throw new Error('访问令牌无效或已过期')
  }
  const body = response.status === 204 ? null : await response.json().catch(() => null)
  if (!response.ok) throw new Error(body?.error || body?.message || `请求失败 (${response.status})`)
  return body
}

async function refreshSnapshot() {
  if (!token.value || loading.value) return
  loading.value = true
  try {
    const [nextStatus, nextProfiles, nextSubscriptions] = await Promise.all([
      request('/api/status'),
      request('/api/profiles'),
      request('/api/subscriptions'),
    ])
    status.value = nextStatus
    profiles.value = nextProfiles
    subscriptions.value = nextSubscriptions
    authenticated.value = true
  } catch (error) {
    if (authenticated.value) notify(error.message, 'error')
  } finally {
    loading.value = false
  }
}

async function connect() {
  const candidate = tokenDraft.value.trim()
  if (!candidate) {
    notify('请输入 Web API 访问令牌', 'error')
    return
  }
  token.value = candidate
  localStorage.setItem('v2rayn-web-token', candidate)
  await refreshSnapshot()
  if (authenticated.value) {
    openEvents()
    notify('已连接到 v2rayN Web Backend')
  } else {
    notify('连接失败，请检查 API 访问令牌和 Backend 地址', 'error')
  }
}

function disconnect() {
  closeEvents()
  token.value = ''
  tokenDraft.value = ''
  localStorage.removeItem('v2rayn-web-token')
  authenticated.value = false
  status.value = null
  profiles.value = []
  subscriptions.value = []
}

function openEvents() {
  closeEvents()
  if (!token.value) return
  eventSource = new EventSource(`/api/events?access_token=${encodeURIComponent(token.value)}`)
  eventSource.addEventListener('log', (event) => appendLog(JSON.parse(event.data)))
  eventSource.addEventListener('status', (event) => { status.value = JSON.parse(event.data) })
  eventSource.addEventListener('subscription-progress', (event) => {
    const progress = JSON.parse(event.data)
    if (progress.success) refreshSnapshot()
  })
  eventSource.addEventListener('latency-result', (event) => {
    const result = JSON.parse(event.data)
    const profile = profiles.value.find((item) => item.indexId === result.indexId)
    if (profile) profile.delay = Number.parseInt(result.delay, 10) || -1
    refreshSnapshot()
  })
  eventSource.onerror = () => {
    if (eventSource?.readyState === EventSource.CLOSED) {
      notify('实时事件连接已关闭', 'error')
    }
  }
}

function closeEvents() {
  eventSource?.close()
  eventSource = null
}

function appendLog(entry) {
  logs.value.push({
    timestamp: entry.timestamp || new Date().toISOString(),
    source: entry.source || 'web',
    message: entry.message || '',
  })
  if (logs.value.length > 500) logs.value.splice(0, logs.value.length - 500)
}

async function loadLogs() {
  try {
    logs.value = await request('/api/logs?limit=200')
  } catch (error) {
    notify(error.message, 'error')
  }
}

async function coreAction(action) {
  busyAction.value = action
  try {
    const result = await request(`/api/core/${action}`, { method: 'POST' })
    notify(result.message || '操作完成')
    await refreshSnapshot()
  } catch (error) {
    notify(error.message, 'error')
  } finally {
    busyAction.value = ''
  }
}

async function selectProfile(profile) {
  if (profile.isCurrent) return
  busyAction.value = `profile:${profile.indexId}`
  try {
    const result = await request(`/api/profiles/${encodeURIComponent(profile.indexId)}/select`, { method: 'POST' })
    notify(result.message || '节点已切换')
    await refreshSnapshot()
  } catch (error) {
    notify(error.message, 'error')
  } finally {
    busyAction.value = ''
  }
}

async function testLatency(profile) {
  busyAction.value = `latency:${profile.indexId}`
  try {
    const result = await request(`/api/profiles/${encodeURIComponent(profile.indexId)}/latency`, { method: 'POST' })
    notify(result.message || '延迟测试已启动')
  } catch (error) {
    notify(error.message, 'error')
  } finally {
    busyAction.value = ''
  }
}

function openAddSubscription() {
  editingSubscriptionId.value = ''
  subscriptionForm.value = { remarks: '', url: '', enabled: true }
  showSubscriptionForm.value = true
}

function openEditSubscription(subscription) {
  editingSubscriptionId.value = subscription.id
  subscriptionForm.value = {
    remarks: subscription.remarks,
    url: subscription.url,
    enabled: subscription.enabled,
  }
  showSubscriptionForm.value = true
}

async function saveSubscription() {
  const editingId = editingSubscriptionId.value
  try {
    const result = await request(editingId ? `/api/subscriptions/${encodeURIComponent(editingId)}` : '/api/subscriptions', {
      method: editingId ? 'PUT' : 'POST',
      body: JSON.stringify(subscriptionForm.value),
    })
    showSubscriptionForm.value = false
    notify(editingId ? '订阅信息已保存' : '订阅已添加')
    await refreshSnapshot()
    if (!editingId && result?.id) await updateSubscription(result.id)
  } catch (error) {
    notify(error.message, 'error')
  }
}

async function updateSubscription(id) {
  try {
    const result = await request(`/api/subscriptions/${encodeURIComponent(id)}/update?useProxy=false`, { method: 'POST' })
    notify(result.message || '订阅更新已启动')
  } catch (error) {
    notify(error.message, 'error')
  }
}

async function deleteSubscription(subscription) {
  if (!window.confirm(`删除订阅“${subscription.remarks}”及其节点？`)) return
  try {
    await request(`/api/subscriptions/${encodeURIComponent(subscription.id)}`, { method: 'DELETE' })
    notify('订阅已删除')
    await refreshSnapshot()
  } catch (error) {
    notify(error.message, 'error')
  }
}

function formatDate(value) {
  if (!value) return '—'
  return new Intl.DateTimeFormat('zh-CN', { dateStyle: 'medium', timeStyle: 'medium' }).format(new Date(value))
}

function formatDelay(value) {
  if (value < 0) return '超时'
  if (!value) return '未测试'
  return `${value} ms`
}

watch(logs, async () => {
  await nextTick()
  if (logPanel.value) logPanel.value.scrollTop = logPanel.value.scrollHeight
}, { deep: true })

onMounted(async () => {
  if (!token.value) return
  tokenDraft.value = token.value
  await refreshSnapshot()
  if (authenticated.value) {
    openEvents()
    await loadLogs()
  }
})

watch(authenticated, (isAuthenticated) => {
  clearInterval(refreshTimer)
  if (isAuthenticated) refreshTimer = setInterval(refreshSnapshot, 7000)
  else closeEvents()
})

onUnmounted(() => {
  clearInterval(refreshTimer)
  clearTimeout(noticeTimer)
  closeEvents()
})
</script>

<template>
  <main class="app-shell">
    <aside class="sidebar">
      <div class="brand-lockup">
        <div class="brand-mark"><span></span><span></span><span></span></div>
        <div>
          <strong>v2rayN</strong>
          <small>WEB CONTROL</small>
        </div>
      </div>

      <div class="sidebar-label">控制台</div>
      <nav class="navigation">
        <button :class="['nav-item', { selected: activeTab === 'overview' }]" @click="activeTab = 'overview'">
          <span class="nav-icon">◫</span>运行概览
        </button>
        <button :class="['nav-item', { selected: activeTab === 'subscriptions' }]" @click="activeTab = 'subscriptions'">
          <span class="nav-icon">↗</span>订阅管理
          <span class="nav-count">{{ subscriptions.length }}</span>
        </button>
      </nav>

      <div class="sidebar-spacer"></div>
      <div class="sidebar-footer">
        <div class="connection-indicator"><span :class="['status-dot', { online }]"></span><span>{{ online ? 'Backend 已连接' : '等待连接' }}</span></div>
        <div class="sidebar-version">REST API · SSE</div>
      </div>
    </aside>

    <section class="main-column">
      <header class="topbar">
        <div class="breadcrumb"><span>v2rayN</span><b>/</b><strong>{{ activeTab === 'overview' ? '运行概览' : '订阅管理' }}</strong></div>
        <div class="topbar-actions">
          <span v-if="status" class="version-tag">{{ status.runtime.split('|')[0]?.trim() || 'v2rayN' }}</span>
          <button class="icon-button" title="刷新状态" :disabled="loading" @click="refreshSnapshot">⟳</button>
          <button v-if="authenticated" class="account-button" @click="disconnect"><span class="avatar">V</span><span>断开</span></button>
        </div>
      </header>

      <div class="page-content">
        <div class="page-heading">
          <div>
            <p class="eyebrow">PROXY MANAGEMENT</p>
            <h1>{{ activeTab === 'overview' ? '运行概览' : '订阅管理' }}</h1>
            <p class="page-subtitle">{{ activeTab === 'overview' ? '管理当前节点、Core 服务和本地代理监听。' : '集中管理订阅来源并跟踪更新进度。' }}</p>
          </div>
          <div v-if="activeTab === 'overview'" class="core-controls">
            <button class="button button-muted" :disabled="!authenticated || busyAction !== ''" @click="coreAction('restart')"><span>↻</span>重启</button>
            <button v-if="status?.coreRunning" class="button button-danger" :disabled="busyAction !== ''" @click="coreAction('stop')"><span>■</span>停止</button>
            <button v-else class="button button-primary" :disabled="!authenticated || busyAction !== ''" @click="coreAction('start')"><span>▶</span>启动 Core</button>
          </div>
          <button v-else class="button button-primary" @click="openAddSubscription"><span>＋</span>添加订阅</button>
        </div>

        <div v-if="notice" :class="['toast', noticeKind]">{{ notice }}</div>

        <section v-if="!authenticated" class="auth-card">
          <div class="auth-art"><div class="auth-orbit orbit-one"></div><div class="auth-orbit orbit-two"></div><div class="auth-core">V</div></div>
          <div class="auth-copy">
            <p class="eyebrow">SECURE ACCESS</p>
            <h2>连接 Web Backend</h2>
            <p>输入部署时设置的 <code>V2RAYN_WEB_API_KEY</code>。访问令牌仅保存在当前浏览器。</p>
            <form class="auth-form" @submit.prevent="connect">
              <label for="api-token">API 访问令牌</label>
              <div class="input-with-action"><input id="api-token" v-model="tokenDraft" type="password" autocomplete="current-password" placeholder="输入访问令牌" /><button class="button button-primary" type="submit">连接 <span>→</span></button></div>
            </form>
          </div>
        </section>

        <template v-else-if="activeTab === 'overview'">
          <section class="summary-grid">
            <article class="summary-card core-summary">
              <div class="summary-top"><span class="summary-icon purple">⌘</span><span :class="['pill', status?.coreRunning ? 'pill-green' : 'pill-muted']"><i></i>{{ status?.coreRunning ? '运行中' : '已停止' }}</span></div>
              <div class="summary-value">{{ status?.coreRunning ? (status.coreType || 'Xray') : '—' }}</div>
              <div class="summary-label">Core 服务</div>
              <div class="summary-foot">{{ status?.coreRunning ? `启动于 ${formatDate(status.coreStartedAt)}` : '选择节点后启动代理 Core' }}</div>
            </article>
            <article class="summary-card">
              <div class="summary-top"><span class="summary-icon blue">⇄</span><span class="small-status">{{ status?.xrayAvailable ? '已就绪' : '未安装' }}</span></div>
              <div class="summary-value">{{ status?.xrayAvailable ? 'Xray' : 'Xray' }}</div>
              <div class="summary-label">首选 Core</div>
              <div class="summary-foot">{{ status?.xrayAvailable ? 'Xray 可执行文件可用' : '请在应用 bin/xray 目录提供 Core' }}</div>
            </article>
            <article class="summary-card">
              <div class="summary-top"><span class="summary-icon amber">◉</span><span class="small-status">HTTP · SOCKS</span></div>
              <div class="summary-value">{{ listenerLabel }}</div>
              <div class="summary-label">混合监听</div>
              <div class="summary-foot">{{ listenerStatus }}</div>
            </article>
            <article class="summary-card">
              <div class="summary-top"><span class="summary-icon mint">▦</span><span class="small-status">配置库</span></div>
              <div class="summary-value">{{ status?.profileCount ?? 0 }} <small>节点</small></div>
              <div class="summary-label">服务端状态</div>
              <div class="summary-foot">{{ status?.subscriptionCount ?? 0 }} 个订阅 · {{ status?.runtime?.split('|').slice(-1)[0]?.trim() || 'Backend 在线' }}</div>
            </article>
          </section>

          <section class="current-node-panel">
            <div class="panel-heading">
              <div><p class="eyebrow">ACTIVE PROFILE</p><h2>当前节点</h2></div>
              <span v-if="currentProfile" class="protocol-badge">{{ currentProfile.protocol }}</span>
            </div>
            <div v-if="currentProfile" class="current-node-content">
              <div class="node-emblem">{{ currentProfile.protocol.slice(0, 1) }}</div>
              <div class="current-node-details"><h3>{{ currentProfile.remarks || '未命名节点' }}</h3><p>{{ currentProfile.address }}<span v-if="currentProfile.port">:{{ currentProfile.port }}</span><span class="separator">·</span>{{ currentProfile.network || '默认传输' }}</p></div>
              <div class="current-node-meta"><span>安全方式</span><strong>{{ currentProfile.streamSecurity || 'none' }}</strong></div>
              <div class="current-node-meta"><span>最近延迟</span><strong>{{ formatDelay(currentProfile.delay) }}</strong></div>
              <button class="button button-outline" :disabled="!currentProfile.canTest || busyAction !== ''" @click="testLatency(currentProfile)">{{ busyAction === `latency:${currentProfile.indexId}` ? '测试中…' : '测试延迟' }}</button>
            </div>
            <div v-else class="empty-current"><span class="empty-symbol">⌁</span><div><strong>尚未选择节点</strong><p>添加订阅并导入节点后，可在下方列表中切换。</p></div><button class="button button-outline" @click="activeTab = 'subscriptions'">管理订阅 <span>→</span></button></div>
          </section>

          <section class="content-grid">
            <article class="panel profile-panel">
              <div class="panel-heading panel-heading-row">
                <div><p class="eyebrow">PROFILES</p><h2>节点列表 <span class="heading-count">{{ filteredProfiles.length }}</span></h2></div>
                <div class="search-field"><span>⌕</span><input v-model="filter" placeholder="搜索节点" aria-label="搜索节点" /></div>
              </div>
              <div v-if="filteredProfiles.length" class="profile-list">
                <div v-for="profile in filteredProfiles" :key="profile.indexId" :class="['profile-row', { active: profile.isCurrent }]">
                  <div class="profile-country"><span>{{ profile.protocol.slice(0, 1) }}</span></div>
                  <div class="profile-main"><div class="profile-title"><strong>{{ profile.remarks || '未命名节点' }}</strong><span v-if="profile.isCurrent" class="current-label">当前</span></div><div class="profile-caption">{{ profile.address }}:{{ profile.port }}<span>·</span>{{ profile.coreType }}</div></div>
                  <div class="profile-delay"><span class="delay-bars"><i></i><i></i><i></i></span>{{ formatDelay(profile.delay) }}</div>
                  <div class="profile-actions"><button class="text-button" :disabled="!profile.canTest || busyAction !== ''" @click="testLatency(profile)">测速</button><button class="button button-small" :class="profile.isCurrent ? 'button-current' : 'button-outline'" :disabled="profile.isCurrent || busyAction !== ''" @click="selectProfile(profile)">{{ busyAction === `profile:${profile.indexId}` ? '切换中…' : profile.isCurrent ? '已连接' : '切换' }}</button></div>
                </div>
              </div>
              <div v-else class="empty-state"><span>⌕</span><strong>{{ profiles.length ? '没有匹配的节点' : '节点列表为空' }}</strong><p>{{ profiles.length ? '试试其他搜索词。' : '更新订阅后，节点会显示在这里。' }}</p></div>
            </article>

            <article class="panel runtime-panel">
              <div class="panel-heading"><div><p class="eyebrow">RUNTIME</p><h2>运行状态</h2></div><span :class="['runtime-indicator', { active: online }]"></span></div>
              <div class="runtime-row"><span>Backend</span><strong><i class="status-dot online"></i>{{ online ? '正常' : '离线' }}</strong></div>
              <div class="runtime-row"><span>Core 进程</span><strong><i :class="['status-dot', { online: status?.coreRunning }]"></i>{{ status?.coreRunning ? '运行中' : '已停止' }}</strong></div>
              <div class="runtime-row"><span>HTTP / SOCKS</span><strong><i :class="['status-dot', { online: listenerIsRunning }]"></i>{{ listenerStatus }}</strong></div>
              <div class="runtime-row"><span>Xray</span><strong>{{ status?.xrayAvailable ? '可用' : '缺少可执行文件' }}</strong></div>
              <div class="runtime-row"><span>运行环境</span><strong class="runtime-text">{{ runtimeName }}</strong></div>
              <div class="runtime-updated">状态更新于 {{ formatDate(status?.checkedAt) }}</div>
            </article>
          </section>

          <section class="panel log-panel">
            <div class="panel-heading panel-heading-row">
              <div><p class="eyebrow">LIVE OUTPUT</p><h2>运行日志 <span class="live-tag"><i></i>LIVE</span></h2></div>
              <button class="text-button" @click="loadLogs">刷新日志</button>
            </div>
            <div ref="logPanel" class="log-viewer">
              <div v-if="logs.length" v-for="(entry, index) in logs" :key="`${entry.timestamp}-${index}`" class="log-line"><time>{{ new Date(entry.timestamp).toLocaleTimeString('zh-CN', { hour12: false }) }}</time><span :class="['log-source', `source-${entry.source}`]">{{ entry.source }}</span><p>{{ entry.message }}</p></div>
              <div v-else class="log-empty">等待 Backend 输出日志…</div>
            </div>
          </section>
        </template>

        <section v-else class="panel subscriptions-panel">
          <div class="panel-heading panel-heading-row subscription-heading">
            <div><p class="eyebrow">SUBSCRIPTION SOURCES</p><h2>订阅来源 <span class="heading-count">{{ subscriptions.length }}</span></h2></div>
            <div class="subscription-tools"><div class="search-field"><span>⌕</span><input v-model="subscriptionFilter" placeholder="搜索订阅" aria-label="搜索订阅" /></div><button class="button button-outline" :disabled="loading" @click="refreshSnapshot">刷新列表</button></div>
          </div>
          <div v-if="filteredSubscriptions.length" class="subscription-list">
            <article v-for="subscription in filteredSubscriptions" :key="subscription.id" class="subscription-card">
              <div class="subscription-avatar">{{ subscription.remarks.slice(0, 1).toUpperCase() }}</div>
              <div class="subscription-info"><div class="subscription-title"><h3>{{ subscription.remarks }}</h3><span :class="['pill', subscription.enabled ? 'pill-green' : 'pill-muted']"><i></i>{{ subscription.enabled ? '已启用' : '已暂停' }}</span></div><p class="subscription-url" :title="subscription.url">{{ subscription.url }}</p><div class="subscription-meta"><span>更新 {{ subscription.updateTime ? formatDate(new Date(subscription.updateTime * 1000)) : '尚未更新' }}</span><span>间隔 {{ subscription.autoUpdateInterval || '手动' }}</span></div></div>
              <div class="subscription-actions"><button class="button button-primary button-small" :disabled="busyAction === `sub:${subscription.id}`" @click="updateSubscription(subscription.id)"><span>↻</span>立即更新</button><button class="icon-button" title="编辑订阅" @click="openEditSubscription(subscription)">✎</button><button class="icon-button danger-hover" title="删除订阅" @click="deleteSubscription(subscription)">×</button></div>
            </article>
          </div>
          <div v-else class="empty-state subscription-empty"><span>↗</span><strong>{{ subscriptions.length ? '没有匹配的订阅' : '还没有添加订阅' }}</strong><p>{{ subscriptions.length ? '试试其他搜索词。' : '添加一个订阅地址，即可导入和更新节点。' }}</p><button v-if="!subscriptions.length" class="button button-primary" @click="openAddSubscription">＋ 添加第一个订阅</button></div>
          <div class="subscription-footnote"><span class="info-mark">i</span><span>节点解析、订阅导入和数据持久化均由 ServiceLib 处理。</span></div>
        </section>

        <footer class="page-footer"><span>v2rayN Web · Headless ServiceLib frontend</span><span>REST API <i></i> SSE</span></footer>
      </div>
    </section>

    <div v-if="showSubscriptionForm" class="modal-backdrop" @click.self="showSubscriptionForm = false">
      <form class="modal-card" @submit.prevent="saveSubscription">
        <div class="modal-heading"><div><p class="eyebrow">SUBSCRIPTION</p><h2>{{ editingSubscriptionId ? '编辑订阅' : '添加订阅' }}</h2></div><button type="button" class="icon-button" @click="showSubscriptionForm = false">×</button></div>
        <label>订阅名称<input v-model="subscriptionForm.remarks" required maxlength="128" placeholder="例如：个人节点" /></label>
        <label>订阅地址<input v-model="subscriptionForm.url" required type="url" placeholder="https://example.com/subscribe" /></label>
        <label class="checkbox-label"><input v-model="subscriptionForm.enabled" type="checkbox" />启用此订阅</label>
        <div class="modal-actions"><button type="button" class="button button-outline" @click="showSubscriptionForm = false">取消</button><button class="button button-primary" type="submit">保存</button></div>
      </form>
    </div>
  </main>
</template>
