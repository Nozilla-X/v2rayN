<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useModalFocus } from '../../composables/useModalFocus'
import UiIcon from '../UiIcon.vue'
import type { UiProps } from '../types'

const { t } = useI18n()
const props = defineProps<UiProps>()
const state = props.state
const actions = props.actions
const dialog = ref<HTMLElement | null>(null)
const { onModalKeydown } = useModalFocus(dialog)
</script>

<template>
  <div v-if="state.showRouteForm" class="modal-shade" @click.self="state.showRouteForm = false">
    <form ref="dialog" class="modal-panel wide-modal modal-form" role="dialog" aria-modal="true" :aria-label="t(state.editingRouteId ? 'routing.edit' : 'routing.create')" tabindex="-1" @keydown="onModalKeydown" @submit.prevent="actions.saveRoute">
      <header class="modal-head"><h2>{{ t(state.editingRouteId ? 'routing.edit' : 'routing.create') }}</h2><button class="tool-button" type="button" :aria-label="t('common.close')" @click="state.showRouteForm = false"><UiIcon name="close" /></button></header>
      <div class="modal-content">
        <fieldset class="editor-section"><legend>{{ t('routing.profiles') }}</legend>
          <div class="form-grid two-col">
            <label>{{ t('routing.name') }}<input v-model="state.routeForm.remarks" required /></label>
            <label>{{ t('routing.url') }}<input v-model="state.routeForm.url" inputmode="url" /></label>
            <label>{{ t('routing.customIcon') }}<input v-model="state.routeForm.customIcon" /></label>
            <label class="check-inline"><input v-model="state.routeForm.enabled" type="checkbox" />{{ t('common.enabled') }}</label>
            <label class="check-inline"><input v-model="state.routeForm.locked" type="checkbox" />{{ t('routing.locked') }}</label>
            <label class="wide-field">{{ t('routing.singboxRuleSetPath') }}<input v-model="state.routeForm.customRulesetPath4Singbox" /></label>
          </div>
        </fieldset>
        <fieldset class="editor-section"><legend>{{ t('routing.domainStrategy') }}</legend>
          <div class="form-grid two-col"><label>{{ t('routing.domainStrategy') }}<input v-model="state.routeForm.domainStrategy" list="route-domain-strategies" /><datalist id="route-domain-strategies"><option v-for="strategy in ['AsIs', 'UseIP', 'UseIPv4v6', 'UseIPv6v4', 'UseIPv4', 'UseIPv6']" :key="strategy" :value="strategy" /></datalist></label><label>{{ t('routing.domainStrategySingbox') }}<input v-model="state.routeForm.domainStrategy4Singbox" /></label></div>
        </fieldset>
      </div>
      <footer class="modal-actions"><button class="button" type="button" @click="state.showRouteForm = false">{{ t('common.cancel') }}</button><button class="button primary" type="submit">{{ t('common.save') }}</button></footer>
    </form>
  </div>
</template>
