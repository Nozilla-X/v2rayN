<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import ActionDropdown from '../ActionDropdown.vue'
import type { UiProps } from '../types'

const { t } = useI18n()
const props = defineProps<UiProps>()
const state = props.state
const actions = props.actions
const selectedSubscription = computed(() => state.subscriptions.find((item: Record<string, any>) => item.id === state.selectedGroup) || null)
const autoFitColumns = ref(false)
</script>

<template>
<section class="page nodes-page">
  <div class="group-toolbar">
    <span class="toolbar-label">{{ t('nodes.group') }}</span>
    <div class="group-chips">
      <button v-for="group in state.groups" :key="group.id || 'all'" :class="['group-chip', { selected: state.selectedGroup === group.id }]" @click="actions.changeGroup(group.id)">
        {{ group.name || t('common.allGroups') }}<small>{{ group.profileCount }}</small>
      </button>
    </div>
    <button class="tool-button node-toolbar-action" :disabled="!selectedSubscription" :aria-label="t('subscriptions.editSubscription')" :title="t('subscriptions.editSubscription')" @click="selectedSubscription && actions.openEditSubscription(selectedSubscription)">✎</button>
    <button class="tool-button node-toolbar-action" :aria-label="t('subscriptions.addSubscription')" :title="t('subscriptions.addSubscription')" @click="actions.openAddSubscription">＋</button>
    <label class="search-box"><span>⌕</span><input v-model="state.filter" :placeholder="t('nodes.filterPlaceholder')" @keyup.enter="actions.loadProfiles" /><button v-if="state.filter" class="clear-search" :aria-label="t('common.close')" @click="state.filter = ''; actions.loadProfiles()">×</button></label>
    <button class="tool-button node-toolbar-action node-autofit-action" :class="{ selected: autoFitColumns }" :aria-pressed="autoFitColumns" :aria-label="t('nodes.autoFitColumns')" :title="t('nodes.autoFitColumns')" @click="autoFitColumns = !autoFitColumns">↔</button>
    <button class="tool-button node-toolbar-action" :aria-label="t('nodes.fastRealping')" :title="t('nodes.fastRealping')" @click="actions.startSpeedTest('fastRealping')">ϟ</button>
    <button class="tool-button node-toolbar-action" :aria-label="t('nodes.mixedtest')" :title="t('nodes.mixedtest')" @click="actions.startSpeedTest('mixedtest')">⇉</button>
  </div>

  <div class="page-toolbar">
    <div class="page-title"><h1>{{ t('nodes.title') }}</h1><span class="count-tag">{{ state.filteredProfiles.length }}</span></div>
    <div class="toolbar-main nodes-toolbar-main">
      <ActionDropdown :label="t('nodes.addMenu')" prefix="＋" variant="primary">
        <button class="action-menu-item" role="menuitem" @click="actions.openAddProfile">{{ t('nodes.addNode') }}</button>
      </ActionDropdown>

      <ActionDropdown :label="t('nodes.importMenu')">
        <button class="action-menu-item" role="menuitem" @click="actions.openImportProfiles">{{ t('nodes.importNodes') }}…</button>
      </ActionDropdown>

      <ActionDropdown :label="t('nodes.subscriptionMenu')">
        <button class="action-menu-item" role="menuitem" @click="actions.updateSubscriptions(state.selectedGroup || null, false)">{{ t(actions.subscriptionUpdateMessageKey(state.selectedGroup || null, false)) }}</button>
        <button class="action-menu-item" role="menuitem" @click="actions.updateSubscriptions(state.selectedGroup || null, true)">{{ t(actions.subscriptionUpdateMessageKey(state.selectedGroup || null, true)) }}</button>
        <div class="action-menu-separator" role="separator"></div>
        <button class="action-menu-item" role="menuitem" @click="actions.updateSubscriptions(null, false)">{{ t(actions.subscriptionUpdateMessageKey(null, false)) }}</button>
        <button class="action-menu-item" role="menuitem" @click="actions.updateSubscriptions(null, true)">{{ t(actions.subscriptionUpdateMessageKey(null, true)) }}</button>
      </ActionDropdown>

      <ActionDropdown :label="t('nodes.testMenu')">
        <button class="action-menu-item" role="menuitem" @click="actions.startSpeedTest('tcping')">{{ t('nodes.tcping') }}</button>
        <button class="action-menu-item" role="menuitem" @click="actions.startSpeedTest('realping')">{{ t('nodes.realping') }}</button>
        <button class="action-menu-item" role="menuitem" @click="actions.startSpeedTest('speedtest')">{{ t('nodes.speedtest') }}</button>
        <button class="action-menu-item" role="menuitem" @click="actions.startSpeedTest('udpTest')">{{ t('nodes.udp') }}</button>
        <button class="action-menu-item" role="menuitem" @click="actions.sortProfiles('DelayVal')">{{ t('nodes.sortByTestResults') }}</button>
        <div class="action-menu-separator" role="separator"></div>
        <button class="action-menu-item" role="menuitem" @click="actions.startSpeedTest('fastRealping')">{{ t('nodes.fastRealping') }}</button>
        <button class="action-menu-item" role="menuitem" @click="actions.startSpeedTest('mixedtest')">{{ t('nodes.mixedtest') }}</button>
        <button class="action-menu-item" role="menuitem" @click="actions.runProfileAction('test-group')">{{ t('nodes.testGroup') }}</button>
        <button class="action-menu-item" role="menuitem" :disabled="!state.operations.includes('speedtest')" @click="actions.stopSpeedTests">{{ t('nodes.stopTest') }}</button>
      </ActionDropdown>

      <ActionDropdown :label="t('nodes.organizeMenu')">
        <button class="action-menu-item" role="menuitem" :disabled="!state.selectedIds.length" @click="actions.runProfileAction('copy')">{{ t('nodes.copySelected') }}</button>
        <button class="action-menu-item danger" role="menuitem" :disabled="!state.selectedIds.length" @click="actions.runProfileAction('delete')">{{ t('nodes.removeSelected') }}</button>
        <button class="action-menu-item" role="menuitem" @click="actions.runProfileAction('deduplicate')">{{ t('nodes.deduplicate') }}</button>
        <button class="action-menu-item" role="menuitem" @click="actions.runProfileAction('remove-invalid')">{{ t('nodes.removeInvalid') }}</button>
        <div class="action-menu-separator" role="separator"></div>
        <details class="action-menu-submenu" :class="{ disabled: !state.selectedIds.length }">
          <summary class="action-menu-item menu-submenu-toggle" role="menuitem" :aria-disabled="!state.selectedIds.length" @click="!state.selectedIds.length && $event.preventDefault()">{{ t('nodes.moveGroup') }}<span class="submenu-caret">›</span></summary>
          <div class="action-menu-submenu-items">
            <button v-for="group in state.groups" :key="group.id || 'all-target'" class="action-menu-item" role="menuitem" :disabled="!state.selectedIds.length" @click="actions.moveSelectedToGroup(group.id)">{{ group.name || t('common.allGroups') }}</button>
          </div>
        </details>
        <details class="action-menu-submenu" :class="{ disabled: !state.selectedIds.length }">
          <summary class="action-menu-item menu-submenu-toggle" role="menuitem" :aria-disabled="!state.selectedIds.length" @click="!state.selectedIds.length && $event.preventDefault()">{{ t('nodes.move') }}<span class="submenu-caret">›</span></summary>
          <div class="action-menu-submenu-items">
            <button class="action-menu-item" role="menuitem" :disabled="!state.selectedIds.length" @click="actions.moveSelected('top')">{{ t('nodes.top') }}</button>
            <button class="action-menu-item" role="menuitem" :disabled="!state.selectedIds.length" @click="actions.moveSelected('up')">{{ t('nodes.up') }}</button>
            <button class="action-menu-item" role="menuitem" :disabled="!state.selectedIds.length" @click="actions.moveSelected('down')">{{ t('nodes.down') }}</button>
            <button class="action-menu-item" role="menuitem" :disabled="!state.selectedIds.length" @click="actions.moveSelected('bottom')">{{ t('nodes.bottom') }}</button>
          </div>
        </details>
        <button class="action-menu-item" role="menuitem" :disabled="!state.filteredProfiles.length" @click="!state.allVisibleSelected && actions.toggleAllVisible()">{{ t('nodes.selectAll') }}</button>
        <div class="action-menu-separator" role="separator"></div>
        <details class="action-menu-submenu" :class="{ disabled: !state.selectedGroup }">
          <summary class="action-menu-item menu-submenu-toggle" role="menuitem" :aria-disabled="!state.selectedGroup" :title="!state.selectedGroup ? t('nodes.groupGenerationSelectSubscription') : ''" @click="!state.selectedGroup && $event.preventDefault()">{{ t('nodes.generatePolicyGroups') }}<span class="submenu-caret">›</span></summary>
          <div class="action-menu-submenu-items">
            <button class="action-menu-item" role="menuitem" :disabled="!state.selectedGroup" @click="actions.generateGroups(false)">{{ t('nodes.allProfiles') }}</button>
            <button class="action-menu-item" role="menuitem" :disabled="!state.selectedGroup || !state.profiles.length" @click="actions.generateGroups(true)">{{ t('nodes.generateRegionGroups') }}</button>
          </div>
        </details>
        <div class="action-menu-separator" role="separator"></div>
        <div class="action-menu-label">{{ t('nodes.webOnlyActions') }}</div>
        <button class="action-menu-item" role="menuitem" :disabled="!state.selectedIds.length" @click="actions.moveSelectedPosition">{{ t('nodes.position') }}…</button>
        <button class="action-menu-item" role="menuitem" @click="actions.loadProfiles">{{ t('common.refresh') }}</button>
      </ActionDropdown>

      <ActionDropdown :label="t('nodes.shareMenu')">
        <button class="action-menu-item" role="menuitem" :disabled="!state.selectedIds.length" @click="actions.shareSelected">{{ t('nodes.shareProfile') }}</button>
      </ActionDropdown>

      <ActionDropdown :label="t('nodes.exportMenu')">
        <button class="action-menu-item" role="menuitem" :disabled="!state.selectedIds.length" @click="actions.exportFullConfig">{{ t('nodes.exportFullConfig') }}</button>
        <button class="action-menu-item" role="menuitem" :disabled="!state.selectedIds.length" @click="actions.exportFullConfigToClipboard">{{ t('nodes.exportFullConfigClipboard') }}</button>
        <div class="action-menu-separator" role="separator"></div>
        <button class="action-menu-item" role="menuitem" :disabled="!state.selectedIds.length" @click="actions.exportShareLinksToClipboard">{{ t('nodes.exportShareLinkClipboard') }}</button>
        <button class="action-menu-item" role="menuitem" :disabled="!state.selectedIds.length" @click="actions.exportShareLinksBase64">{{ t('nodes.exportShareLinkBase64') }}</button>
        <button class="action-menu-item" role="menuitem" :disabled="!state.selectedIds.length" @click="actions.exportInnerUris">{{ t('nodes.exportInnerUri') }}</button>
        <div class="action-menu-separator" role="separator"></div>
        <div class="action-menu-label">{{ t('nodes.webOnlyActions') }}</div>
        <button class="action-menu-item" role="menuitem" :disabled="!state.selectedIds.length" @click="actions.exportSelected">{{ t('nodes.customExport') }}…</button>
      </ActionDropdown>

      <div v-if="state.selectedIds.length" class="selection-summary">
        <strong>{{ t('common.selected', { count: state.selectedIds.length }) }}</strong>
        <button class="selection-clear" :aria-label="t('common.close')" :title="t('common.close')" @click="state.selectedIds = []">×</button>
      </div>
    </div>
  </div>

  <div class="table-wrap" :class="{ 'auto-fit-columns': autoFitColumns }">
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
  <div class="table-footer"><span>{{ t('nodes.regexHint') }}</span></div>
</section>
</template>
