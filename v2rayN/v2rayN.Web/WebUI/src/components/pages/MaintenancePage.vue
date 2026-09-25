<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import type { UiProps } from '../types'

const { t } = useI18n()
const props = defineProps<UiProps>()
const state = props.state
const actions = props.actions
const activeTab = ref('updates')
</script>

<template>
  <section class="page maintenance-page">
    <div class="page-toolbar"><div class="page-title"><h1>{{ t('maintenance.title') }}</h1></div><button class="button" @click="actions.loadMaintenance">{{ t('common.refresh') }}</button></div>
    <nav class="section-tabs" :aria-label="t('maintenance.title')"><button :class="{ selected: activeTab === 'updates' }" @click="activeTab = 'updates'">{{ t('maintenance.updates') }}</button><button :class="{ selected: activeTab === 'backup' }" @click="activeTab = 'backup'">{{ t('maintenance.backupRestore') }}</button></nav>

    <section v-if="activeTab === 'updates'" class="settings-section">
      <div class="settings-subsection update-section">
        <div class="section-heading"><div><h2>{{ t('maintenance.xray') }}</h2><small>{{ t('maintenance.xrayOnlyNotice') }}</small></div><span v-if="state.xrayUpdate.result" class="update-state">{{ state.xrayUpdate.result.updateAvailable ? t('maintenance.updateAvailable', { version: state.xrayUpdate.result.version }) : t('maintenance.upToDate') }}</span></div>
        <div class="update-core-row"><label class="check-inline"><input v-model="state.xrayUpdate.selected" type="checkbox" />Xray</label><span class="muted">{{ t('maintenance.backendCurrentCore') }}</span></div>
        <div class="settings-checks"><label class="check-inline"><input v-model="state.xrayUpdate.preRelease" type="checkbox" />{{ t('maintenance.preRelease') }}</label><label class="check-inline"><input v-model="state.xrayUpdate.useProxy" type="checkbox" />{{ t('maintenance.useProxy') }}</label></div>
        <div class="button-row"><button class="button" :disabled="!state.xrayUpdate.selected" @click="actions.checkXrayUpdate">{{ t('maintenance.checkOnly') }}</button><button class="button primary" :disabled="!state.xrayUpdate.selected || state.operations.includes('xray-update')" @click="actions.updateXray">{{ t('maintenance.checkAndUpdate') }}</button></div>
      </div>
      <div class="settings-subsection"><div class="section-heading"><div><h2>{{ t('maintenance.geoFiles') }}</h2><small>{{ t('maintenance.geoUpdateHint') }}</small></div></div><button class="button" :disabled="state.operations.includes('geo-update')" @click="actions.updateGeo">{{ t('maintenance.updateGeo') }}</button></div>
      <div class="settings-subsection"><div class="section-heading"><div><h2>{{ t('maintenance.statistics') }}</h2><small>{{ t('maintenance.statistics') }} · {{ state.status?.statisticsEnabled ? t('common.enabled') : t('status.statisticsOff') }}</small></div></div><button class="button danger" @click="actions.clearStatistics">{{ t('maintenance.clearStatistics') }}</button></div>
      <div class="settings-subsection"><div class="section-heading"><h2>{{ t('maintenance.operationList') }}</h2><button class="tool-button" :title="t('common.refresh')" @click="actions.loadOperations">↻</button></div><div v-if="state.operations.length" class="operation-list"><span v-for="operation in state.operations" :key="operation" class="operation-pill"><i class="status-led on"></i>{{ operation }}</span></div><p v-else class="muted">{{ t('maintenance.noOperations') }}</p></div>
    </section>

    <section v-else class="settings-section backup-section">
      <div class="settings-subsection"><h2>{{ t('maintenance.localBackup') }}</h2><div class="button-row"><button class="button primary" @click="actions.downloadBackup">{{ t('maintenance.downloadBackup') }}</button><label class="button danger file-button">{{ t('maintenance.restoreUpload') }}<input type="file" accept=".zip,application/zip" @change="actions.uploadRestore" /></label></div><p class="field-hint danger-note">{{ t('maintenance.restoreWarning') }}</p></div>
      <div class="settings-subsection"><div class="section-heading"><h2>{{ t('maintenance.webdav') }}</h2><span v-if="state.webdavForm.hasPassword" class="muted">{{ t('maintenance.passwordStored') }}</span></div><div class="form-grid two-col"><label>{{ t('maintenance.webdavUrl') }}<input v-model="state.webdavForm.url" /></label><label>{{ t('maintenance.webdavDir') }}<input v-model="state.webdavForm.dirName" /></label><label>{{ t('maintenance.webdavUser') }}<input v-model="state.webdavForm.userName" /></label><label>{{ t('maintenance.webdavPassword') }}<input v-model="state.webdavForm.password" type="password" /></label></div><div class="button-row"><button class="button primary" @click="actions.saveWebdav">{{ t('maintenance.webdavSettings') }}</button><button class="button" @click="actions.webdavAction('check')">{{ t('maintenance.checkWebdav') }}</button><button class="button" @click="actions.webdavAction('backup')">{{ t('maintenance.backupWebdav') }}</button><button class="button danger" @click="actions.webdavAction('restore')">{{ t('maintenance.restoreWebdav') }}</button></div><p class="field-hint danger-note">{{ t('maintenance.webdavRestoreWarning') }}</p></div>
    </section>
  </section>
</template>
