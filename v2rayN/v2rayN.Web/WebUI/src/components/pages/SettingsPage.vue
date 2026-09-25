<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { UiProps } from '../types'

const { t } = useI18n()
const props = defineProps<UiProps>()
const state = props.state
const actions = props.actions
</script>

<template>
<section class="page settings-page">
  <div class="page-toolbar"><div class="page-title"><h1>{{ t('settings.title') }}</h1></div><span class="muted">{{ t('settings.restartHint') }}</span></div>
  <div class="settings-grid">
    <section class="subpanel"><div class="subpanel-heading"><h2>{{ t('settings.inbound') }}</h2></div><div class="form-grid two-col">
      <label>{{ t('settings.localPort') }}<input v-model.number="state.inboundForm.localPort" type="number" min="1" max="65535" /></label>
      <label class="check-inline"><input v-model="state.inboundForm.secondLocalPortEnabled" type="checkbox" />{{ t('settings.secondPort') }}</label>
      <label class="check-inline"><input v-model="state.inboundForm.udpEnabled" type="checkbox" />{{ t('settings.udp') }}</label>
      <label class="check-inline"><input v-model="state.inboundForm.sniffingEnabled" type="checkbox" />{{ t('settings.sniffing') }}</label>
      <label>{{ t('settings.destOverride') }}<textarea v-model="state.inboundForm.destOverrideText"></textarea></label>
      <label class="check-inline"><input v-model="state.inboundForm.routeOnly" type="checkbox" />{{ t('settings.routeOnly') }}</label>
      <label class="check-inline"><input v-model="state.inboundForm.allowLANConn" type="checkbox" />{{ t('settings.allowLan') }}</label>
      <label class="check-inline"><input v-model="state.inboundForm.newPort4LAN" type="checkbox" />{{ t('settings.newLanPort') }}</label>
      <label>{{ t('settings.user') }}<input v-model="state.inboundForm.user" /></label><label>{{ t('settings.pass') }}<input v-model="state.inboundForm.pass" type="password" /></label>
    </div><button class="button compact primary" @click="actions.saveInbound">{{ t('settings.saveInbound') }}</button></section>

    <section class="subpanel"><div class="subpanel-heading"><h2>{{ t('settings.core') }}</h2></div><div class="form-grid two-col">
      <label class="check-inline"><input v-model="state.coreForm.logEnabled" type="checkbox" />{{ t('settings.logEnabled') }}</label><label>{{ t('settings.loglevel') }}<select v-model="state.coreForm.loglevel"><option v-for="level in ['debug', 'info', 'warning', 'error', 'none']" :key="level">{{ level }}</option></select></label>
      <label>{{ t('settings.fingerprint') }}<input v-model="state.coreForm.defFingerprint" /></label><label>{{ t('settings.userAgent') }}<input v-model="state.coreForm.defUserAgent" /></label><label>{{ t('settings.sendThrough') }}<input v-model="state.coreForm.sendThrough" /></label><label>{{ t('settings.bindInterface') }}<input v-model="state.coreForm.bindInterface" /></label>
      <label>{{ t('settings.muxRay') }}<input v-model.number="state.coreForm.mux4RayConcurrency" type="number" min="0" /></label><label>{{ t('settings.muxXudp') }}<input v-model.number="state.coreForm.mux4RayXudpConcurrency" type="number" min="0" /></label><label>{{ t('settings.muxXudp443') }}<input v-model="state.coreForm.mux4RayXudpProxyUDP443" /></label><label>{{ t('settings.muxSboxProtocol') }}<input v-model="state.coreForm.mux4SboxProtocol" /></label><label>{{ t('settings.muxSboxConnections') }}<input v-model.number="state.coreForm.mux4SboxMaxConnections" type="number" min="0" /></label><label class="check-inline"><input v-model="state.coreForm.mux4SboxPadding" type="checkbox" />{{ t('settings.muxSboxPadding') }}</label><label class="check-inline"><input v-model="state.coreForm.enableCacheFile4Sbox" type="checkbox" />{{ t('settings.cacheSbox') }}</label>
      <label>{{ t('settings.hy2Up') }}<input v-model.number="state.coreForm.hy2UpMbps" type="number" min="0" /></label><label>{{ t('settings.hy2Down') }}<input v-model.number="state.coreForm.hy2DownMbps" type="number" min="0" /></label><label class="check-inline"><input v-model="state.coreForm.enableFragment" type="checkbox" />{{ t('settings.fragment') }}</label><label class="check-inline"><input v-model="state.coreForm.enableFinalFragment" type="checkbox" />{{ t('settings.finalFragment') }}</label><label>{{ t('settings.fragmentPackets') }}<input v-model="state.coreForm.fragmentPackets" /></label><label>{{ t('settings.fragmentMaxSplit') }}<input v-model="state.coreForm.fragmentMaxSplit" /></label><label>{{ t('settings.fragmentLengths') }}<textarea v-model="state.coreForm.fragmentLengthsText"></textarea></label><label>{{ t('settings.fragmentDelays') }}<textarea v-model="state.coreForm.fragmentDelaysText"></textarea></label>
    </div><button class="button compact primary" @click="actions.saveCoreSettings">{{ t('settings.saveCore') }}</button></section>

    <section class="subpanel"><div class="subpanel-heading"><h2>{{ t('settings.application') }}</h2></div><div class="form-grid two-col"><label class="check-inline"><input v-model="state.appForm.enableStatistics" type="checkbox" />{{ t('settings.statistics') }}</label><label class="check-inline"><input v-model="state.appForm.displayRealTimeSpeed" type="checkbox" />{{ t('settings.realtimeSpeed') }}</label><label class="check-inline"><input v-model="state.appForm.keepOlderDedupl" type="checkbox" />{{ t('settings.keepOlderDedupl') }}</label><label>{{ t('settings.geoAutoUpdate') }}<input v-model.number="state.appForm.geoAutoUpdateInterval" type="number" min="0" /></label><label>{{ t('settings.rootCertProvider') }}<input v-model="state.appForm.rootCertProvider" /></label><label>{{ t('settings.geoSourceUrl') }}<input v-model="state.appForm.geoSourceUrl" /></label><label>{{ t('settings.srsSourceUrl') }}<input v-model="state.appForm.srsSourceUrl" /></label><label>{{ t('settings.routeRulesSourceUrl') }}<input v-model="state.appForm.routeRulesTemplateSourceUrl" /></label><label>{{ t('settings.subConvertUrl') }}<input v-model="state.appForm.subConvertUrl" /></label></div><button class="button compact primary" @click="actions.saveAppSettings">{{ t('settings.saveApplication') }}</button></section>

    <section class="subpanel"><div class="subpanel-heading"><h2>{{ t('settings.speedtest') }}</h2></div><div class="form-grid two-col"><label>{{ t('settings.speedTimeout') }}<input v-model.number="state.speedForm.speedTestTimeout" type="number" min="1" /></label><label>{{ t('settings.mixedConcurrency') }}<input v-model.number="state.speedForm.mixedConcurrencyCount" type="number" min="1" /></label><label>{{ t('settings.speedUrl') }}<input v-model="state.speedForm.speedTestUrl" /></label><label>{{ t('settings.pingUrl') }}<input v-model="state.speedForm.speedPingTestUrl" /></label><label>{{ t('settings.ipApiUrl') }}<input v-model="state.speedForm.ipapiUrl" /></label><label>{{ t('settings.udpTarget') }}<input v-model="state.speedForm.udpTestTarget" /></label><label>{{ t('settings.pageSize') }}<input v-model.number="state.speedForm.speedTestPageSize" type="number" min="1" /></label><label>{{ t('settings.delayInterval') }}<input v-model.number="state.speedForm.speedTestDelayInterval" type="number" min="0" /></label></div><button class="button compact primary" @click="actions.saveSpeedSettings">{{ t('settings.saveSpeedtest') }}</button></section>

    <section class="subpanel"><div class="subpanel-heading"><h2>{{ t('settings.coreTypes') }}</h2><button class="button compact primary" @click="actions.saveCoreTypes">{{ t('settings.saveCoreTypes') }}</button></div><div class="mapping-list"><div v-for="mapping in state.settings.coreTypes || []" :key="mapping.configType" class="mapping-row"><span>{{ mapping.configType }}</span><select v-model="mapping.coreType"><option v-for="core in state.coreTypes" :key="core" :value="core">{{ core }}</option></select></div></div></section>
  </div>
</section>
</template>
