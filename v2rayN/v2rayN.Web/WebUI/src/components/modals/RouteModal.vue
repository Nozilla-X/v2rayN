<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { UiProps } from '../types'

const { t } = useI18n()
const props = defineProps<UiProps>()
const state = props.state
const actions = props.actions
</script>

<template>
<div v-if="state.showRouteForm" class="modal-shade" @click.self="state.showRouteForm = false"><form class="modal-panel" @submit.prevent="actions.saveRoute"><div class="modal-head"><h2>{{ t(state.editingRouteId ? 'routing.edit' : 'routing.create') }}</h2><button class="tool-button" type="button" @click="state.showRouteForm = false">×</button></div><div class="form-grid"><label>{{ t('routing.name') }}<input v-model="state.routeForm.remarks" required /></label><label>{{ t('routing.url') }}<input v-model="state.routeForm.url" /></label><label>{{ t('routing.domainStrategy') }}<input v-model="state.routeForm.domainStrategy" /></label><label>{{ t('routing.domainStrategySingbox') }}<input v-model="state.routeForm.domainStrategy4Singbox" /></label><label>{{ t('routing.ruleCount') }}<input v-model.number="state.routeForm.ruleNum" type="number" min="0" /></label><label>{{ t('routing.url') }}<input v-model="state.routeForm.customRulesetPath4Singbox" /></label><label class="check-inline"><input v-model="state.routeForm.enabled" type="checkbox" />{{ t('common.enabled') }}</label><label class="check-inline"><input v-model="state.routeForm.locked" type="checkbox" />{{ t('common.enabled') }}</label><label class="wide-field">{{ t('common.rawJson') }}<textarea v-model="state.routeForm.ruleSet" class="code-area"></textarea></label></div><div class="modal-actions"><button class="button" type="button" @click="state.showRouteForm = false">{{ t('common.cancel') }}</button><button class="button primary" type="submit">{{ t('common.save') }}</button></div></form></div>
</template>
