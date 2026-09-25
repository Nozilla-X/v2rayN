<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { UiProps } from './types'

const { t } = useI18n()
const props = defineProps<UiProps>()
const state = props.state
const actions = props.actions
</script>

<template>
<header class="app-header">
  <div class="brand"><img class="brand-glyph" :src="state.brandIconSrc" :title="state.brandIconTitle" alt="" /><strong>{{ t('brand') }}</strong></div>
  <nav class="main-nav" :aria-label="t('brand')">
    <button v-for="item in state.navItems" :key="item.id" :class="['nav-tab', { selected: state.activePage === item.id }]" @click="actions.navigate(item.id)">
      <span class="nav-icon">{{ item.icon }}</span>{{ t(item.key) }}
      <span v-if="item.id === 'subscriptions'" class="nav-badge">{{ state.subscriptions.length }}</span>
    </button>
  </nav>
  <div class="header-right">
    <span :class="['connection-tag', { online: state.authenticated }]">{{ state.authenticated ? t('auth.connected') : t('auth.waiting') }}</span>
    <select v-model="state.locale" class="locale-select" :aria-label="t('brand')">
      <option value="zh-CN">{{ t('localeNames.zhCN') }}</option><option value="zh-TW">{{ t('localeNames.zhTW') }}</option><option value="en-US">{{ t('localeNames.enUS') }}</option>
    </select>
    <button v-if="state.authenticated" class="tool-button" :title="t('common.refresh')" :disabled="state.loading" @click="actions.refreshBase">⟳</button>
    <button v-if="state.authenticated" class="tool-button" @click="actions.disconnect">{{ t('auth.disconnect') }}</button>
  </div>
</header>
</template>
