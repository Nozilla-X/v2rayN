<script setup lang="ts">
import { computed, ref } from 'vue'
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
const protocol = computed(() => state.profileForm.configType)
const isGroup = computed(() => ['PolicyGroup', 'ProxyChain'].includes(protocol.value))
const isTls = computed(() => ['tls', 'reality'].includes(String(state.profileForm.streamSecurity).toLowerCase()))
const isReality = computed(() => String(state.profileForm.streamSecurity).toLowerCase() === 'reality')

const transports = ['tcp', 'raw', 'ws', 'grpc', 'xhttp', 'httpupgrade', 'kcp', 'http', 'quic', 'domainsocket']
</script>

<template>
  <div v-if="state.showProfileForm" class="modal-shade" @click.self="state.showProfileForm = false">
    <form ref="dialog" class="modal-panel wide-modal modal-form" role="dialog" aria-modal="true" :aria-label="t(state.editingProfileId ? 'nodes.editNode' : 'nodes.addNode')" tabindex="-1" @keydown="onModalKeydown" @submit.prevent="actions.saveProfile">
      <header class="modal-head">
        <div><h2>{{ t(state.editingProfileId ? 'nodes.editNode' : 'nodes.addNode') }}</h2><small>{{ t('nodes.protocolEditorHint') }}</small></div>
        <button class="tool-button" type="button" :aria-label="t('common.close')" @click="state.showProfileForm = false"><UiIcon name="close" /></button>
      </header>

      <div class="modal-content">
        <fieldset class="editor-section">
          <legend>{{ t('nodes.profileBase') }}</legend>
          <div class="form-grid three-col">
            <label>{{ t('nodes.type') }}<select v-model="state.profileForm.configType"><option v-for="kind in state.protocolTypes" :key="kind" :value="kind">{{ kind === 'Anytls' ? 'AnyTLS' : kind }}</option><option value="PolicyGroup">PolicyGroup</option><option value="ProxyChain">ProxyChain</option></select></label>
            <label>{{ t('nodes.coreType') }}<select v-model="state.profileForm.coreType"><option v-for="core in state.coreTypes" :key="core" :value="core">{{ core === 'sing_box' ? 'sing-box' : core }}</option></select></label>
            <label>{{ t('nodes.remarks') }}<input v-model="state.profileForm.remarks" required /></label>
            <template v-if="!isGroup">
              <label>{{ t('nodes.address') }}<input v-model="state.profileForm.address" required autocomplete="off" /></label>
              <label>{{ t('nodes.port') }}<input v-model.number="state.profileForm.port" type="number" min="1" max="65535" required /></label>
              <label>{{ t('nodes.network') }}<select v-model="state.profileForm.network"><option v-for="network in transports" :key="network" :value="network">{{ network }}</option></select></label>
            </template>
          </div>
        </fieldset>

        <fieldset v-if="isGroup" class="editor-section">
          <legend>{{ t(protocol === 'PolicyGroup' ? 'nodes.policyGroup' : 'nodes.proxyChain') }}</legend>
          <div class="form-grid two-col">
            <label v-if="protocol === 'PolicyGroup'">{{ t('nodes.groupStrategy') }}<select v-model="state.profileForm.protoExtra.multipleLoad"><option value="LeastPing">{{ t('nodes.strategyLeastPing') }}</option><option value="Fallback">{{ t('nodes.strategyFallback') }}</option><option value="Random">{{ t('nodes.strategyRandom') }}</option><option value="RoundRobin">{{ t('nodes.strategyRoundRobin') }}</option><option value="LeastLoad">{{ t('nodes.strategyLeastLoad') }}</option></select></label>
            <label>{{ t('nodes.groupSubscription') }}<select v-model="state.profileForm.protoExtra.subChildItems"><option value="">{{ t('common.none') }}</option><option v-for="group in state.groups" :key="group.id" :value="group.id">{{ group.name || t('common.allGroups') }}</option></select></label>
            <label>{{ t('nodes.groupFilter') }}<input v-model="state.profileForm.protoExtra.filter" /></label>
            <div class="wide-field group-member-editor"><strong>{{ t('nodes.groupMembers') }}</strong><small class="field-hint">{{ t('nodes.groupMembersHint') }}</small><div class="group-member-columns"><div class="group-profile-choices"><label v-for="item in state.profileCatalog" :key="item.indexId" class="group-profile-choice"><input type="checkbox" :disabled="item.indexId === state.editingProfileId" :checked="state.groupChildIds.includes(item.indexId)" @change="actions.toggleGroupChild(item.indexId)" /><span>{{ item.remarks }}<small>{{ item.configType }} · {{ item.address }}:{{ item.port }}</small></span></label></div><div class="group-member-order"><div v-for="(id, index) in state.groupChildIds" :key="id" class="group-member-row"><span>{{ state.profileCatalog.find((item: Record<string, any>) => item.indexId === id)?.remarks || id }}</span><button class="tool-button" type="button" :disabled="index === 0" :aria-label="t('nodes.moveMemberUp')" @click="actions.moveGroupChild(id, 'up')"><UiIcon name="arrow-up" /></button><button class="tool-button" type="button" :disabled="index === state.groupChildIds.length - 1" :aria-label="t('nodes.moveMemberDown')" @click="actions.moveGroupChild(id, 'down')"><UiIcon name="arrow-down" /></button><button class="tool-button danger-text" type="button" :aria-label="t('common.delete')" @click="actions.toggleGroupChild(id)"><UiIcon name="close" /></button></div><p v-if="!state.groupChildIds.length" class="muted">{{ t('common.empty') }}</p></div></div></div>
          </div>
        </fieldset>

        <template v-else>
          <fieldset class="editor-section">
            <legend>{{ t('nodes.authentication') }}</legend>
            <div class="form-grid three-col">
              <label v-if="!['HTTP', 'SOCKS', 'Naive', 'WireGuard'].includes(protocol)">{{ t(protocol === 'VMess' || protocol === 'VLESS' ? 'nodes.uuid' : 'nodes.password') }}<input v-model="state.profileForm.password" :required="['VMess', 'VLESS', 'Shadowsocks', 'Trojan', 'Hysteria2', 'TUIC', 'Anytls'].includes(protocol)" autocomplete="off" /></label>
              <label v-if="['HTTP', 'SOCKS', 'Naive'].includes(protocol)">{{ t('nodes.username') }}<input v-model="state.profileForm.username" autocomplete="off" /></label>
              <label v-if="protocol === 'TUIC'">{{ t('nodes.uuid') }}<input v-model="state.profileForm.username" required autocomplete="off" /></label>
              <label v-if="['HTTP', 'SOCKS', 'Naive'].includes(protocol)">{{ t('nodes.password') }}<input v-model="state.profileForm.password" autocomplete="off" /></label>
              <label v-if="protocol === 'VMess'">{{ t('nodes.configVersion') }}<input v-model.number="state.profileForm.configVersion" type="number" min="1" /></label>
              <label v-if="protocol === 'VMess'">{{ t('nodes.alterId') }}<input v-model="state.profileForm.protoExtra.alterId" /></label>
              <label v-if="protocol === 'VMess'">{{ t('nodes.security') }}<input v-model="state.profileForm.protoExtra.vmessSecurity" /></label>
              <label v-if="protocol === 'VLESS'">{{ t('nodes.flow') }}<input v-model="state.profileForm.protoExtra.flow" placeholder="xtls-rprx-vision" /></label>
              <label v-if="protocol === 'VLESS'">{{ t('nodes.encryption') }}<input v-model="state.profileForm.protoExtra.vlessEncryption" /></label>
              <label v-if="protocol === 'Shadowsocks'">{{ t('nodes.method') }}<input v-model="state.profileForm.protoExtra.ssMethod" /></label>
              <label v-if="['Shadowsocks', 'Naive'].includes(protocol)" class="check-inline"><input v-model="state.profileForm.protoExtra.uot" type="checkbox" />{{ t('nodes.udpOverTcp') }}</label>
              <label v-if="protocol === 'TUIC'">{{ t('nodes.congestionControl') }}<input v-model="state.profileForm.protoExtra.congestionControl" /></label>
              <label v-if="protocol === 'Naive' && state.profileForm.protoExtra.naiveQuic">{{ t('nodes.congestionControl') }}<input v-model="state.profileForm.protoExtra.congestionControl" /></label>
              <template v-if="protocol === 'Hysteria2'">
                <label>{{ t('nodes.uploadBandwidth') }}<input v-model.number="state.profileForm.protoExtra.upMbps" type="number" min="0" /></label>
                <label>{{ t('nodes.downloadBandwidth') }}<input v-model.number="state.profileForm.protoExtra.downMbps" type="number" min="0" /></label>
                <label>{{ t('nodes.salamanderPassword') }}<input v-model="state.profileForm.protoExtra.salamanderPass" /></label>
                <label>{{ t('nodes.portHopping') }}<input v-model="state.profileForm.protoExtra.ports" /></label>
                <label>{{ t('nodes.hopInterval') }}<input v-model="state.profileForm.protoExtra.hopInterval" /></label>
                <label>{{ t('nodes.realmUrl') }}<input v-model="state.profileForm.protoExtra.hy2RealmUrl" /></label>
                <label>{{ t('nodes.geckoMinPacket') }}<input v-model="state.profileForm.protoExtra.geckoMinPacketSize" /></label>
                <label>{{ t('nodes.geckoMaxPacket') }}<input v-model="state.profileForm.protoExtra.geckoMaxPacketSize" /></label>
              </template>
              <template v-if="protocol === 'WireGuard'">
                <label>{{ t('nodes.wgPublicKey') }}<input v-model="state.profileForm.protoExtra.wgPublicKey" /></label>
                <label>{{ t('nodes.wgPresharedKey') }}<input v-model="state.profileForm.protoExtra.wgPresharedKey" /></label>
                <label>{{ t('nodes.wgAddress') }}<input v-model="state.profileForm.protoExtra.wgInterfaceAddress" /></label>
                <label>{{ t('nodes.wgReserved') }}<input v-model="state.profileForm.protoExtra.wgReserved" /></label>
                <label>{{ t('nodes.wgMtu') }}<input v-model.number="state.profileForm.protoExtra.wgMtu" type="number" min="0" /></label>
                <label>{{ t('nodes.wgDns') }}<input v-model="state.profileForm.protoExtra.wgDns" /></label>
              </template>
              <label v-if="['Anytls', 'Naive'].includes(protocol)">{{ t('nodes.insecureConcurrency') }}<input v-model.number="state.profileForm.protoExtra.insecureConcurrency" type="number" min="0" /></label>
              <label v-if="protocol === 'Naive'" class="check-inline"><input v-model="state.profileForm.protoExtra.naiveQuic" type="checkbox" />{{ t('nodes.naiveQuic') }}</label>
              <label v-if="protocol === 'HTTP'">{{ t('nodes.httpHeaders') }}<textarea v-model="state.profileForm.protoExtra.httpHeaders" /></label>
              <label v-if="['VMess', 'VLESS'].includes(protocol)" class="check-inline"><input v-model="state.profileForm.muxEnabled" type="checkbox" />{{ t('nodes.mux') }}</label>
            </div>
          </fieldset>

          <fieldset class="editor-section">
            <legend>{{ t('nodes.transport') }}</legend>
            <div class="form-grid three-col">
              <template v-if="state.profileForm.network === 'raw' || state.profileForm.network === 'tcp'">
                <label>{{ t('nodes.rawHeaderType') }}<input v-model="state.profileForm.transportExtra.rawHeaderType" /></label>
              </template>
              <template v-if="state.profileForm.network === 'ws' || state.profileForm.network === 'http' || state.profileForm.network === 'httpupgrade'">
                <label>{{ t('nodes.host') }}<input v-model="state.profileForm.transportExtra.host" /></label>
                <label>{{ t('nodes.path') }}<input v-model="state.profileForm.transportExtra.path" /></label>
              </template>
              <template v-if="state.profileForm.network === 'grpc'">
                <label>{{ t('nodes.grpcAuthority') }}<input v-model="state.profileForm.transportExtra.grpcAuthority" /></label>
                <label>{{ t('nodes.grpcServiceName') }}<input v-model="state.profileForm.transportExtra.grpcServiceName" /></label>
                <label>{{ t('nodes.grpcMode') }}<input v-model="state.profileForm.transportExtra.grpcMode" /></label>
              </template>
              <template v-if="state.profileForm.network === 'xhttp'">
                <label>{{ t('nodes.xhttpMode') }}<input v-model="state.profileForm.transportExtra.xhttpMode" /></label>
                <label class="wide-field">{{ t('nodes.xhttpExtra') }}<textarea v-model="state.profileForm.transportExtra.xhttpExtra" /></label>
              </template>
              <template v-if="state.profileForm.network === 'kcp'">
                <label>{{ t('nodes.kcpHeaderType') }}<input v-model="state.profileForm.transportExtra.kcpHeaderType" /></label>
                <label>{{ t('nodes.kcpSeed') }}<input v-model="state.profileForm.transportExtra.kcpSeed" /></label>
                <label>{{ t('nodes.kcpMtu') }}<input v-model.number="state.profileForm.transportExtra.kcpMtu" type="number" min="0" /></label>
              </template>
            </div>
          </fieldset>

          <fieldset class="editor-section">
            <legend>{{ t('nodes.tlsReality') }}</legend>
            <div class="form-grid three-col">
              <label>{{ t('nodes.streamSecurity') }}<select v-model="state.profileForm.streamSecurity"><option value="">{{ t('common.none') }}</option><option value="tls">TLS</option><option value="reality">Reality</option></select></label>
              <template v-if="isTls">
                <label>{{ t('nodes.sni') }}<input v-model="state.profileForm.sni" /></label>
                <label>{{ t('nodes.alpn') }}<input v-model="state.profileForm.alpn" placeholder="h2,http/1.1" /></label>
                <label>{{ t('nodes.fingerprint') }}<input v-model="state.profileForm.fingerprint" /></label>
                <label class="check-inline"><input v-model="state.profileForm.allowInsecure" type="checkbox" />{{ t('nodes.allowInsecure') }}</label>
              </template>
              <template v-if="isReality">
                <label>{{ t('nodes.publicKey') }}<input v-model="state.profileForm.publicKey" /></label>
                <label>{{ t('nodes.shortId') }}<input v-model="state.profileForm.shortId" /></label>
                <label>{{ t('nodes.spiderX') }}<input v-model="state.profileForm.spiderX" /></label>
                <label>{{ t('nodes.mldsa65Verify') }}<input v-model="state.profileForm.mldsa65Verify" /></label>
              </template>
              <template v-if="state.profileForm.streamSecurity === 'tls'">
                <label>{{ t('nodes.cert') }}<textarea v-model="state.profileForm.cert" /></label>
                <label>{{ t('nodes.certSha') }}<input v-model="state.profileForm.certSha" /></label>
                <label>{{ t('nodes.echConfigList') }}<textarea v-model="state.profileForm.echConfigList" /></label>
                <label>{{ t('nodes.verifyPeerCertByName') }}<input v-model="state.profileForm.verifyPeerCertByName" /></label>
                <label>{{ t('nodes.finalmask') }}<input v-model="state.profileForm.finalmask" /></label>
              </template>
            </div>
          </fieldset>
        </template>

        <details class="advanced-editor">
          <summary>{{ t('nodes.advancedProfileFields') }}</summary>
          <p class="field-hint">{{ t('nodes.advancedProfileHint') }}</p>
          <textarea v-model="state.profileAdvancedJson" class="code-area advanced-profile-json" spellcheck="false" />
        </details>
        <p v-if="state.profileModalError" class="inline-error" role="alert">{{ state.profileModalError }}</p>
      </div>

      <footer class="modal-actions">
        <button class="button" type="button" @click="state.showProfileForm = false">{{ t('common.cancel') }}</button>
        <button class="button primary" type="submit">{{ t('common.save') }}</button>
      </footer>
    </form>
  </div>
</template>
