<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { UiProps } from '../types'

const { t } = useI18n()
const props = defineProps<UiProps>()
const state = props.state
const actions = props.actions
</script>

<template>
<section class="page">
  <div class="page-toolbar"><div class="page-title"><h1>{{ t('routing.title') }}</h1></div><div class="toolbar-main"><button class="button" @click="actions.importRoutingProfiles">{{ t('routing.importProfiles') }}</button><button class="button primary" @click="actions.openAddRoute">＋ {{ t('routing.create') }}</button></div></div>
  <div class="split-workspace">
    <section class="subpanel route-list-panel"><div class="subpanel-heading"><h2>{{ t('routing.profiles') }}</h2><span class="count-tag">{{ state.routes.length }}</span></div>
      <div v-for="route in state.routes" :key="route.id" :class="['route-row', { selected: route.id === state.activeRoutingId, current: route.isActive }]" @click="state.activeRoutingId = route.id; actions.loadRules(route.id)">
        <div class="route-info"><strong>{{ route.remarks }}</strong><small>{{ route.ruleNum }} · {{ route.enabled ? t('common.enabled') : t('common.disabled') }}</small></div><span v-if="route.isActive" class="current-label">{{ t('routing.default') }}</span>
        <div class="row-actions"><button class="tool-button" :title="t('common.edit')" @click.stop="actions.openEditRoute(route)">✎</button><button class="tool-button danger-text" :title="t('common.delete')" @click.stop="actions.deleteRoute(route)">×</button></div>
      </div>
      <p v-if="!state.routes.length" class="muted empty-inline">{{ t('routing.noRouting') }}</p>
      <div class="preset-bar"><label>{{ t('routing.regionalPreset') }}</label><button class="link-button" @click="actions.applyPreset('Default')">{{ t('routing.presetDefault') }}</button><button class="link-button" @click="actions.applyPreset('Russia')">{{ t('routing.presetRussia') }}</button><button class="link-button" @click="actions.applyPreset('Iran')">{{ t('routing.presetIran') }}</button></div>
      <div class="form-grid route-strategies"><label>{{ t('routing.domainStrategy') }}<input v-model="state.routingForm.domainStrategy" /></label><label>{{ t('routing.domainStrategySingbox') }}<input v-model="state.routingForm.domainStrategy4Singbox" /></label><button class="button compact primary" @click="actions.saveRoutingStrategies">{{ t('settings.saveRouting') }}</button></div>
    </section>
    <section class="subpanel rules-panel"><div class="subpanel-heading"><div><h2>{{ t('routing.rules') }}</h2><small>{{ state.currentRoute?.remarks || t('common.none') }}</small></div><div class="toolbar-main"><button class="button compact" :disabled="!state.activeRoutingId" @click="actions.addRoutingRule">＋ {{ t('routing.addRule') }}</button><button class="button compact" :disabled="!state.activeRoutingId" @click="actions.copyRoutingRules">{{ t('common.copy') }}</button><button class="button compact primary" :disabled="!state.activeRoutingId" @click="actions.saveRoutingRules">{{ t('routing.saveRules') }}</button></div></div>
      <div v-if="state.routingRules.length" class="rules-mini-table"><div v-for="rule in state.routingRules" :key="rule.id" class="rule-row"><span :class="['rule-state', { off: !rule.enabled }]">{{ rule.enabled ? '●' : '○' }}</span><strong>{{ rule.remarks || rule.type || rule.ruleType || '—' }}</strong><span class="rule-details">{{ rule.domain?.join(', ') || rule.ip?.join(', ') || rule.port || rule.network || '—' }}</span><span class="rule-outbound">{{ rule.outboundTag || '—' }}</span><div class="row-actions"><button class="tool-button" :title="t('routing.moveUp')" @click="actions.moveRoutingRule(rule, 'up')">↑</button><button class="tool-button" :title="t('routing.moveDown')" @click="actions.moveRoutingRule(rule, 'down')">↓</button><button class="tool-button danger-text" :title="t('common.delete')" @click="actions.removeRoutingRule(rule)">×</button></div></div></div>
      <p v-else class="muted empty-inline">{{ t('common.empty') }}</p>
      <label class="field-label raw-json-label">{{ t('common.rawJson') }}<small>{{ t('routing.ruleJsonHint') }}</small></label><textarea v-model="state.rulesRaw" class="code-area rules-json" spellcheck="false"></textarea>
      <div class="import-rule-row"><textarea v-model="state.ruleImportText" class="code-area import-rule-input" :placeholder="t('routing.importRules')"></textarea><div class="import-rule-controls"><label class="check-inline"><input v-model="state.appendRules" type="checkbox" />{{ t('routing.append') }}</label><button class="button" :disabled="!state.ruleImportText" @click="actions.importRoutingRules">{{ t('common.import') }}</button></div></div>
    </section>
  </div>
</section>
</template>
