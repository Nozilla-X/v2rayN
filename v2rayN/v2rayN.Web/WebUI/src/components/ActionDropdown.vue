<script setup lang="ts">
import { nextTick, onMounted, onUnmounted, provide, ref } from 'vue'
import { closeActionDropdownKey } from './menuContext'

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
const popup = ref<HTMLElement | null>(null)
const open = ref(false)
const popupStyle = ref<Record<string, string>>({ left: '-10000px', top: '-10000px', visibility: 'hidden' })

function close() {
  open.value = false
}

async function updatePosition() {
  await nextTick()
  const anchor = root.value?.querySelector<HTMLButtonElement>('.action-menu-trigger')?.getBoundingClientRect()
  const menu = popup.value
  if (!anchor || !menu) return
  const margin = 8
  const maxWidth = Math.max(180, window.innerWidth - margin * 2)
  const width = Math.min(menu.offsetWidth, maxWidth)
  const left = Math.max(margin, Math.min(anchor.left, window.innerWidth - width - margin))
  const height = Math.min(menu.offsetHeight, window.innerHeight - margin * 2)
  const below = window.innerHeight - anchor.bottom - margin
  const top = below >= Math.min(height, 180) ? anchor.bottom + 3 : Math.max(margin, anchor.top - height - 3)
  popupStyle.value = { left: `${left}px`, top: `${top}px`, width: `${width}px`, maxHeight: `min(68vh, ${window.innerHeight - margin * 2}px)`, visibility: 'visible' }
}

function onResize() {
  if (open.value) void updatePosition()
}

provide(closeActionDropdownKey, close)

function closeFromClick(event: MouseEvent) {
  const target = event.target
  if (target instanceof Element && target.closest('.menu-stay-open, .menu-submenu-toggle')) return
  close()
}

function onPointerDown(event: PointerEvent) {
  if (event.target instanceof Element && event.target.closest('.flyout-menu-popup')) return
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
  window.addEventListener('resize', onResize)
  document.addEventListener('scroll', onResize, true)
})

onUnmounted(() => {
  document.removeEventListener('pointerdown', onPointerDown)
  document.removeEventListener('keydown', onKeydown)
  window.removeEventListener('resize', onResize)
  document.removeEventListener('scroll', onResize, true)
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
      @click.stop="open = !open; open && updatePosition()"
    >
      <span v-if="prefix" aria-hidden="true">{{ prefix }}</span>
      {{ label }}<span class="menu-caret" aria-hidden="true">▾</span>
    </button>
    <div v-if="open" ref="popup" class="action-menu-popup" :style="popupStyle" role="menu" @click="closeFromClick">
      <slot />
    </div>
  </div>
</template>
