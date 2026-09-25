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
  <div class="page-toolbar"><div class="page-title"><h1>{{ t('templates.title') }}</h1></div><button class="button" @click="actions.loadTemplates">{{ t('common.refresh') }}</button></div>
  <div class="template-grid"><section v-for="template in state.templates" :key="template.id" class="subpanel template-panel"><div class="subpanel-heading"><h2>{{ template.coreType }}</h2><label class="check-inline"><input v-model="template.enabled" type="checkbox" />{{ t('templates.enabled') }}</label></div><div class="form-grid"><label>{{ t('templates.remarks') }}<input v-model="template.remarks" /></label><label>{{ t('templates.config') }}<textarea v-model="template.config" class="code-area" spellcheck="false"></textarea></label><label class="check-inline"><input v-model="template.addProxyOnly" type="checkbox" />{{ t('templates.addProxyOnly') }}</label><label>{{ t('templates.proxyDetour') }}<input v-model="template.proxyDetour" /></label></div><button class="button compact primary" @click="actions.saveTemplate(template)">{{ t('templates.save') }}</button></section><p v-if="!state.templates.length" class="muted empty-inline">{{ t('common.empty') }}</p></div>
</section>
</template>
