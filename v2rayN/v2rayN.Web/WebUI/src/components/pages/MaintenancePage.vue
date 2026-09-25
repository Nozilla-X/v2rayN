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
  <div class="page-toolbar"><div class="page-title"><h1>{{ t('maintenance.title') }}</h1></div><button class="button" @click="actions.loadMaintenance">{{ t('common.refresh') }}</button></div>
  <div class="maintenance-grid"><section class="subpanel"><div class="subpanel-heading"><h2>{{ t('maintenance.updates') }}</h2></div><div class="form-grid two-col"><label class="check-inline"><input v-model="state.xrayUpdate.preRelease" type="checkbox" />{{ t('maintenance.preRelease') }}</label><label class="check-inline"><input v-model="state.xrayUpdate.useProxy" type="checkbox" />{{ t('maintenance.useProxy') }}</label></div><div class="button-row"><button class="button" @click="actions.checkXrayUpdate">{{ t('maintenance.checkXray') }}</button><button class="button primary" :disabled="state.operations.includes('xray-update')" @click="actions.updateXray">{{ t('maintenance.updateXray') }}</button><button class="button" :disabled="state.operations.includes('geo-update')" @click="actions.updateGeo">{{ t('maintenance.updateGeo') }}</button></div><p v-if="state.xrayUpdate.result" class="operation-result">{{ state.xrayUpdate.result.updateAvailable ? t('maintenance.updateAvailable', { version: state.xrayUpdate.result.version }) : t('maintenance.upToDate') }}</p></section>
    <section class="subpanel"><div class="subpanel-heading"><h2>{{ t('maintenance.statistics') }}</h2></div><p class="muted">{{ t('maintenance.statistics') }} · {{ state.status?.statisticsEnabled ? t('common.enabled') : t('status.statisticsOff') }}</p><button class="button danger" @click="actions.clearStatistics">{{ t('maintenance.clearStatistics') }}</button></section>
    <section class="subpanel webdav-panel"><div class="subpanel-heading"><h2>{{ t('maintenance.webdav') }}</h2><span v-if="state.webdavForm.hasPassword" class="muted">{{ t('maintenance.passwordStored') }}</span></div><div class="form-grid two-col"><label>{{ t('maintenance.webdavUrl') }}<input v-model="state.webdavForm.url" /></label><label>{{ t('maintenance.webdavDir') }}<input v-model="state.webdavForm.dirName" /></label><label>{{ t('maintenance.webdavUser') }}<input v-model="state.webdavForm.userName" /></label><label>{{ t('maintenance.webdavPassword') }}<input v-model="state.webdavForm.password" type="password" /></label></div><div class="button-row"><button class="button primary" @click="actions.saveWebdav">{{ t('common.save') }}</button><button class="button" @click="actions.webdavAction('check')">{{ t('maintenance.checkWebdav') }}</button><button class="button" @click="actions.webdavAction('backup')">{{ t('maintenance.backupWebdav') }}</button><button class="button danger" @click="actions.webdavAction('restore')">{{ t('maintenance.restoreWebdav') }}</button></div></section>
    <section class="subpanel"><div class="subpanel-heading"><h2>{{ t('maintenance.backup') }}</h2></div><div class="button-row"><button class="button primary" @click="actions.downloadBackup">{{ t('maintenance.downloadBackup') }}</button><label class="button file-button">{{ t('maintenance.restoreUpload') }}<input type="file" accept=".zip,application/zip" @change="actions.uploadRestore" /></label></div></section>
    <section class="subpanel"><div class="subpanel-heading"><h2>{{ t('maintenance.operationList') }}</h2><button class="tool-button" @click="actions.loadOperations">⟳</button></div><div v-if="state.operations.length" class="operation-list"><span v-for="operation in state.operations" :key="operation" class="operation-pill"><i class="status-led on"></i>{{ operation }}</span></div><p v-else class="muted">{{ t('maintenance.noOperations') }}</p></section>
  </div>
</section>
</template>
