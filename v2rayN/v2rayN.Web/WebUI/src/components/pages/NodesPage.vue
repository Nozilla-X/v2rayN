<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { UiProps } from '../types'

const { t } = useI18n()
const props = defineProps<UiProps>()
const state = props.state
const actions = props.actions
</script>

<template>
<section class="page nodes-page">
  <div class="page-toolbar">
    <div class="page-title"><h1>{{ t('nodes.title') }}</h1><span class="count-tag">{{ state.filteredProfiles.length }}</span></div>
    <div class="toolbar-main">
      <button class="button primary" @click="actions.openAddProfile">＋ {{ t('nodes.addNode') }}</button>
      <button class="button" @click="actions.openImportProfiles">{{ t('common.import') }}</button>
      <button class="button" @click="actions.updateSubscriptions(state.selectedGroup || null, false)">{{ t(actions.subscriptionUpdateMessageKey(state.selectedGroup || null, false)) }}</button>
      <select class="compact-select test-select" @change="($event.target as HTMLSelectElement).value && actions.startSpeedTest(($event.target as HTMLSelectElement).value)">
        <option value="">{{ t('nodes.test') }}…</option>
        <option v-for="action in state.testActions" :key="action.id" :value="action.id">{{ t(action.key) }}</option>
      </select>
      <button class="button" @click="actions.runProfileAction('test-group')">{{ t('nodes.testGroup') }}</button>
      <button class="button" :disabled="!state.operations.includes('speedtest')" @click="actions.stopSpeedTests">{{ t('nodes.stopTest') }}</button>
    </div>
  </div>

  <div class="group-toolbar">
    <span class="toolbar-label">{{ t('nodes.group') }}</span>
    <div class="group-chips">
      <button v-for="group in state.groups" :key="group.id || 'all'" :class="['group-chip', { selected: state.selectedGroup === group.id }]" @click="actions.changeGroup(group.id)">
        {{ group.name || t('common.allGroups') }}<small>{{ group.profileCount }}</small>
      </button>
    </div>
    <div class="group-generation"><button class="link-button" :disabled="!state.selectedGroup" :title="!state.selectedGroup ? t('nodes.groupGenerationSelectSubscription') : ''" @click="actions.generateGroups(false)">{{ t('nodes.generateAllGroups') }}</button><button class="link-button" :disabled="!state.selectedGroup || !state.profiles.length" :title="!state.selectedGroup ? t('nodes.groupGenerationSelectSubscription') : !state.profiles.length ? t('nodes.noProfile') : ''" @click="actions.generateGroups(true)">{{ t('nodes.generateRegionGroups') }}</button></div>
    <label class="search-box"><span>⌕</span><input v-model="state.filter" :placeholder="t('nodes.filterPlaceholder')" @keyup.enter="actions.loadProfiles" /><button v-if="state.filter" class="clear-search" :aria-label="t('common.close')" @click="state.filter = ''; actions.loadProfiles()">×</button></label>
  </div>

  <div v-if="state.selectedIds.length" class="bulk-toolbar">
    <strong>{{ t('common.selected', { count: state.selectedIds.length }) }}</strong>
    <button class="button compact" @click="actions.runProfileAction('copy')">{{ t('nodes.copySelected') }}</button>
    <button class="button compact" @click="actions.exportSelected">{{ t('nodes.exportSelected') }}</button>
    <select class="compact-select" @change="($event.target as HTMLSelectElement).value !== '' && actions.moveSelectedToGroup(($event.target as HTMLSelectElement).value)">
      <option value="">{{ t('nodes.moveGroup') }}…</option>
      <option v-for="group in state.groups" :key="group.id || 'all-target'" :value="group.id">{{ group.name || t('common.allGroups') }}</option>
    </select>
    <div class="button-group">
      <button class="button compact" :title="t('nodes.top')" @click="actions.moveSelected('top')">⇈</button><button class="button compact" :title="t('nodes.up')" @click="actions.moveSelected('up')">↑</button><button class="button compact" :title="t('nodes.down')" @click="actions.moveSelected('down')">↓</button><button class="button compact" :title="t('nodes.bottom')" @click="actions.moveSelected('bottom')">⇊</button><button class="button compact" :title="t('nodes.position')" @click="actions.moveSelectedPosition">#</button>
    </div>
    <button class="button compact danger" @click="actions.runProfileAction('delete')">{{ t('nodes.deleteSelected') }}</button>
    <button class="tool-button" @click="state.selectedIds = []">×</button>
  </div>

  <div class="table-wrap">
    <table class="profile-table">
      <thead><tr>
        <th class="check-cell"><input type="checkbox" :checked="state.allVisibleSelected" :aria-label="t('common.selected', { count: state.filteredProfiles.length })" @change="actions.toggleAllVisible" /></th>
        <th><button class="sort-button" @click="actions.sortProfiles('ConfigType')">{{ t('nodes.type') }}</button></th>
        <th><button class="sort-button" @click="actions.sortProfiles('Remarks')">{{ t('nodes.remarks') }}</button></th>
        <th><button class="sort-button" @click="actions.sortProfiles('Address')">{{ t('nodes.address') }}</button></th>
        <th><button class="sort-button" @click="actions.sortProfiles('Port')">{{ t('nodes.port') }}</button></th>
        <th><button class="sort-button" @click="actions.sortProfiles('Network')">{{ t('nodes.network') }}</button></th>
        <th><button class="sort-button" @click="actions.sortProfiles('StreamSecurity')">{{ t('nodes.tls') }}</button></th>
        <th><button class="sort-button" @click="actions.sortProfiles('SubRemarks')">{{ t('nodes.groupColumn') }}</button></th>
        <th><button class="sort-button" @click="actions.sortProfiles('DelayVal')">{{ t('nodes.delay') }}</button></th>
        <th><button class="sort-button" @click="actions.sortProfiles('SpeedVal')">{{ t('nodes.speed') }}</button></th>
        <th>{{ t('nodes.ip') }}</th>
        <th><button class="sort-button" @click="actions.sortProfiles('TodayUp')">↑ {{ t('nodes.todayUp') }}</button></th>
        <th><button class="sort-button" @click="actions.sortProfiles('TodayDown')">↓ {{ t('nodes.todayDown') }}</button></th>
        <th><button class="sort-button" @click="actions.sortProfiles('TotalUp')">↑ {{ t('nodes.totalUp') }}</button></th>
        <th><button class="sort-button" @click="actions.sortProfiles('TotalDown')">↓ {{ t('nodes.totalDown') }}</button></th>
        <th class="actions-cell">{{ t('nodes.actions') }}</th>
      </tr></thead>
      <tbody>
        <tr v-for="profile in state.filteredProfiles" :key="profile.indexId" :class="{ current: profile.isCurrent, selected: state.selectedIds.includes(profile.indexId) }" @dblclick="actions.selectProfile(profile)" @contextmenu="actions.openContext($event, profile)">
          <td class="check-cell"><input type="checkbox" :checked="state.selectedIds.includes(profile.indexId)" :aria-label="profile.remarks || profile.address" @change="actions.toggleProfile(profile.indexId)" @click.stop /></td>
          <td :data-label="t('nodes.type')"><span class="protocol-code">{{ profile.protocol }}</span></td>
          <td class="remark-cell" :data-label="t('nodes.remarks')"><span v-if="profile.isCurrent" class="current-marker" :title="t('nodes.current')">●</span><span class="remark-text" :title="profile.remarks">{{ profile.remarks || '—' }}</span></td>
          <td class="address-cell" :data-label="t('nodes.address')" :title="profile.address">{{ profile.address }}</td>
          <td class="number-cell" :data-label="t('nodes.port')">{{ profile.port }}</td>
          <td :data-label="t('nodes.network')">{{ profile.network || '—' }}</td>
          <td :data-label="t('nodes.tls')">{{ profile.streamSecurity || '—' }}</td>
          <td class="group-cell" :data-label="t('nodes.groupColumn')" :title="profile.subscriptionName">{{ profile.subscriptionName || t('common.none') }}</td>
          <td :data-label="t('nodes.delay')" :class="['number-cell', 'delay-cell', { bad: profile.delay < 0 }]">{{ actions.formatDelay(profile.delay) }}</td>
          <td class="number-cell" :data-label="t('nodes.speed')">{{ profile.speed ? `${profile.speed} MB/s` : '—' }}</td>
          <td class="ip-cell" :data-label="t('nodes.ip')" :title="profile.ipInfo">{{ profile.ipInfo || '—' }}</td>
          <td class="number-cell" :data-label="t('nodes.todayUp')">{{ actions.formatBytes(profile.todayUp) }}</td><td class="number-cell" :data-label="t('nodes.todayDown')">{{ actions.formatBytes(profile.todayDown) }}</td>
          <td class="number-cell" :data-label="t('nodes.totalUp')">{{ actions.formatBytes(profile.totalUp) }}</td><td class="number-cell" :data-label="t('nodes.totalDown')">{{ actions.formatBytes(profile.totalDown) }}</td>
          <td class="row-actions" :data-label="t('nodes.actions')"><button class="link-button" :disabled="profile.isCurrent" @click="actions.selectProfile(profile)">{{ profile.isCurrent ? t('nodes.current') : t('nodes.switch') }}</button><button class="link-button" @click="actions.startSpeedTest('tcping', [profile.indexId])">{{ t('nodes.test') }}</button><button class="tool-button row-more" :title="t('nodes.actions')" @click.stop="actions.openContext($event, profile)">⋯</button></td>
        </tr>
        <tr v-if="!state.filteredProfiles.length"><td colspan="16" class="empty-row">{{ state.profiles.length ? t('common.noResults') : t('nodes.noProfile') }}</td></tr>
      </tbody>
    </table>
  </div>
  <div class="table-footer"><span>{{ t('nodes.regexHint') }}</span><div class="quick-maintenance"><button class="link-button" @click="actions.runProfileAction('deduplicate')">{{ t('nodes.deduplicate') }}</button><button class="link-button" @click="actions.runProfileAction('remove-invalid')">{{ t('nodes.removeInvalid') }}</button><button class="link-button" @click="actions.loadProfiles">{{ t('common.refresh') }}</button></div></div>
</section>
</template>
