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
  <div class="page-toolbar"><div class="page-title"><h1>{{ t('dns.title') }}</h1></div><button class="button" @click="actions.loadDns">{{ t('common.refresh') }}</button></div>
  <div class="dns-layout"><section class="subpanel"><div class="subpanel-heading"><h2>{{ t('dns.simple') }}</h2><button class="button compact primary" @click="actions.saveSimpleDns">{{ t('dns.saveSimple') }}</button></div><label class="field-label raw-json-label">{{ t('dns.jsonEditor') }}</label><textarea v-model="state.simpleDnsRaw" class="code-area dns-code" spellcheck="false"></textarea></section>
    <section class="subpanel"><div class="subpanel-heading"><h2>{{ t('dns.profiles') }}</h2></div><div v-for="profile in state.dnsProfiles" :key="profile.id" class="dns-profile-block"><div class="dns-profile-head"><strong>{{ profile.coreType }}</strong><label class="check-inline"><input v-model="profile.enabled" type="checkbox" />{{ t('dns.enabled') }}</label></div>
      <div class="form-grid"><label>{{ t('dns.remarks') }}<input v-model="profile.remarks" /></label><label>{{ t('dns.normalDns') }}<textarea v-model="profile.normalDNS"></textarea></label><label>{{ t('dns.domainStrategy') }}<input v-model="profile.domainStrategy4Freedom" /></label><label>{{ t('dns.domainDnsAddress') }}<input v-model="profile.domainDNSAddress" /></label><label class="check-inline"><input v-model="profile.useSystemHosts" type="checkbox" />{{ t('dns.useSystemHosts') }}</label></div><button class="button compact primary" @click="actions.saveDnsProfile(profile)">{{ t('dns.saveCoreDns', { core: profile.coreType }) }}</button>
    </div><p v-if="!state.dnsProfiles.length" class="muted empty-inline">{{ t('common.empty') }}</p></section>
  </div>
</section>
</template>
