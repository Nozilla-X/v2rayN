<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { UiProps } from '../types'

const { t } = useI18n()
const props = defineProps<UiProps>()
const state = props.state
const actions = props.actions
</script>

<template>
<div v-if="state.showImportForm" class="modal-shade" @click.self="state.showImportForm = false"><form class="modal-panel" @submit.prevent="actions.importProfiles"><div class="modal-head"><h2>{{ t('nodes.importNodes') }}</h2><button class="tool-button" type="button" @click="state.showImportForm = false">×</button></div><label>{{ t('subscriptions.source') }}<select v-model="state.importForm.subscriptionId"><option value="">{{ t('common.allGroups') }}</option><option v-for="group in state.groups.filter((item: Record<string, any>) => item.id)" :key="group.id" :value="group.id">{{ group.name }}</option></select></label><label>{{ t('nodes.importContent') }}<textarea v-model="state.importForm.content" class="code-area import-content" required :placeholder="t('nodes.importHint')"></textarea></label><label class="check-inline"><input v-model="state.importForm.isSubscription" type="checkbox" />{{ t('nodes.isSubscription') }}</label><div class="modal-actions"><label class="button file-button">{{ t('common.openFile') }}<input type="file" accept=".txt,.json,.conf" @change="actions.readImportFile" /></label><button class="button" type="button" @click="actions.pasteImport">{{ t('common.paste') }}</button><button class="button" type="button" @click="state.showImportForm = false">{{ t('common.cancel') }}</button><button class="button primary" type="submit">{{ t('common.import') }}</button></div></form></div>
</template>
