import type { InjectionKey } from 'vue'

export const closeActionDropdownKey: InjectionKey<() => void> = Symbol('closeActionDropdown')
