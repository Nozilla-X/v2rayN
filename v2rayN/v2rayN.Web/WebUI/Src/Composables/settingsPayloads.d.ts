export function nullableNumber(value: unknown): number | null

export function buildSpeedSettingsBody(speedForm: Record<string, unknown>): Record<string, unknown>

export function buildCoreSettingsBody(core: Record<string, any>): Record<string, any>

export function buildApplicationSettingsBody(app: Record<string, any>): Record<string, any>

export function buildSaveAllSettingsSections(input: {
  inbound: Record<string, any>
  core: Record<string, any>
  app: Record<string, any>
  speed: Record<string, unknown>
  coreTypes: unknown[]
}): Array<{ key: string; path: string; body: Record<string, any> }>
