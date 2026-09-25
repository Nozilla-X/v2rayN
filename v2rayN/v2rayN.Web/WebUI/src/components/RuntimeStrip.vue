<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { UiProps } from './types'

const { t } = useI18n()
const props = defineProps<UiProps>()
const state = props.state
const actions = props.actions
</script>

<template>
<section class="runtime-strip">
  <div class="runtime-main">
    <span :class="['status-led', { on: state.status?.coreRunning }]"></span>
    <strong>{{ state.status?.coreRunning ? (state.status.coreType || t('nodes.core')) : t('nodes.stopped') }}</strong>
    <span class="runtime-separator"></span>
    <span>{{ t('nodes.current') }}:</span><b class="current-runtime-name">{{ state.currentProfile?.remarks || state.status?.currentProfileName || t('nodes.noneCurrent') }}</b>
    <span class="runtime-separator"></span>
    <label class="compact-select-label">{{ t('coreToolbar.route') }}</label>
    <select v-model="state.activeRoutingId" class="compact-select route-select" @change="actions.activateRoute(state.activeRoutingId)">
      <option value="">{{ t('common.none') }}</option>
      <option v-for="route in state.routes" :key="route.id" :value="route.id">{{ route.remarks }}</option>
    </select>
  </div>
  <div class="core-actions">
    <button class="button compact primary" :disabled="state.busy || state.status?.coreRunning" @click="actions.coreAction('start')">▶ {{ t('nodes.start') }}</button>
    <button class="button compact" :disabled="state.busy || !state.status?.coreRunning" @click="actions.coreAction('restart')">↻ {{ t('nodes.restart') }}</button>
    <button class="button compact danger" :disabled="state.busy || !state.status?.coreRunning" @click="actions.coreAction('stop')">■ {{ t('nodes.stop') }}</button>
  </div>
</section>
</template>
