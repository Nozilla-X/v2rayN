<script setup lang="ts">
import type { UiState } from './types'
import UiIcon from './UiIcon.vue'

const props = defineProps<{ state: UiState }>()
const state = props.state
</script>

<template>
<div v-if="state.notice" :class="['notice-bar', state.noticeKind]" role="status">{{ state.notice }}<button class="tool-button" :aria-label="state.closeLabel" @click="state.notice = ''"><UiIcon name="close" /></button></div>
</template>
