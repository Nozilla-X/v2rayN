<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'

withDefaults(defineProps<{
  label: string
  prefix?: string
  variant?: 'default' | 'primary'
  disabled?: boolean
}>(), {
  prefix: '',
  variant: 'default',
  disabled: false,
})

const root = ref<HTMLElement | null>(null)
const open = ref(false)

function close() {
  open.value = false
}

function closeFromClick(event: MouseEvent) {
  const target = event.target
  if (target instanceof Element && target.closest('.menu-stay-open, .menu-submenu-toggle')) return
  close()
}

function onPointerDown(event: PointerEvent) {
  if (event.target instanceof Node && !root.value?.contains(event.target)) close()
}

function onKeydown(event: KeyboardEvent) {
  if (event.key !== 'Escape' || !open.value) return
  close()
  root.value?.querySelector<HTMLButtonElement>('.action-menu-trigger')?.focus()
}

onMounted(() => {
  document.addEventListener('pointerdown', onPointerDown)
  document.addEventListener('keydown', onKeydown)
})

onUnmounted(() => {
  document.removeEventListener('pointerdown', onPointerDown)
  document.removeEventListener('keydown', onKeydown)
})
</script>

<template>
  <div ref="root" class="action-dropdown" :class="{ open }">
    <button
      type="button"
      class="button action-menu-trigger"
      :class="{ primary: variant === 'primary' }"
      :disabled="disabled"
      aria-haspopup="menu"
      :aria-expanded="open"
      @click.stop="open = !open"
    >
      <span v-if="prefix" aria-hidden="true">{{ prefix }}</span>
      {{ label }}<span class="menu-caret" aria-hidden="true">▾</span>
    </button>
    <div v-if="open" class="action-menu-popup" role="menu" @click="closeFromClick">
      <slot />
    </div>
  </div>
</template>
