<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { UiProps } from '../types'

const { t } = useI18n()
const props = defineProps<UiProps>()
const state = props.state
const actions = props.actions
</script>

<template>
  <div v-if="state.showExportDialog" class="modal-shade" @click.self="state.showExportDialog = false">
    <section class="modal-panel wide-modal modal-form">
      <header class="modal-head"><h2>{{ t('nodes.exportSelected') }}</h2><button class="tool-button" type="button" :aria-label="t('common.close')" @click="state.showExportDialog = false">×</button></header>
      <div class="modal-content export-modal-content">
        <div class="export-options"><label class="check-inline"><input v-model="state.exportOptions.includeShareUris" type="checkbox" />{{ t('nodes.includeShareUris') }}</label><label class="check-inline"><input v-model="state.exportOptions.base64ShareUris" type="checkbox" />{{ t('nodes.base64ShareUris') }}</label><label class="check-inline"><input v-model="state.exportOptions.includeInnerUri" type="checkbox" />{{ t('nodes.includeInnerUri') }}</label><label class="check-inline"><input v-model="state.exportOptions.includeClientConfig" type="checkbox" />{{ t('nodes.includeClientConfig') }}</label><button class="button compact" @click="actions.exportSelected">{{ t('common.refresh') }}</button></div>
        <textarea v-model="state.exportContent" class="code-area export-area" spellcheck="false" />
      </div>
      <footer class="modal-actions"><button class="button" @click="actions.copyExport">{{ t('common.copy') }}</button><button class="button" @click="actions.downloadExport">{{ t('common.download') }}</button><button class="button primary" @click="state.showExportDialog = false">{{ t('common.close') }}</button></footer>
    </section>
  </div>
</template>
