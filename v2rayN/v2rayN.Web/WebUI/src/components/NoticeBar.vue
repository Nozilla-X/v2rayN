<script setup lang="ts">
import type { UiState } from './types'

const props = defineProps<{ state: UiState }>()
const state = props.state
</script>

<template>
<div v-if="state.notice" :class="['notice-bar', state.noticeKind]" role="status">{{ state.notice }}<button class="tool-button" @click="state.notice = ''">×</button></div>
</template>
