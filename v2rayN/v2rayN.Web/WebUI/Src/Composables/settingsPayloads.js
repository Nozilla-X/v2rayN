export function nullableNumber(value) {
  if (value == null) return null
  if (typeof value === 'string' && value.trim() === '') return null

  if (typeof value !== 'number' && typeof value !== 'string') {
    throw new TypeError('Expected a number or an empty nullable numeric value.')
  }

  const number = typeof value === 'number' ? value : Number(value.trim())
  if (!Number.isFinite(number)) {
    throw new TypeError('The numeric setting must be a finite number.')
  }
  return number
}

function requiredNumber(value) {
  const number = typeof value === 'number' ? value : typeof value === 'string' && value.trim() !== '' ? Number(value) : Number.NaN
  if (!Number.isFinite(number)) {
    throw new TypeError('The numeric setting must be a finite number.')
  }
  return number
}

function defaultedNumber(value) {
  return value == null || value === '' ? 0 : requiredNumber(value)
}

function parseLines(value) {
  return String(value ?? '').split(/\r?\n/).map((line) => line.trim()).filter(Boolean)
}

export function buildSpeedSettingsBody(speedForm) {
  return {
    ...speedForm,
    speedTestTimeout: requiredNumber(speedForm.speedTestTimeout),
    mixedConcurrencyCount: requiredNumber(speedForm.mixedConcurrencyCount),
    speedTestPageSize: nullableNumber(speedForm.speedTestPageSize),
    speedTestDelayInterval: nullableNumber(speedForm.speedTestDelayInterval),
  }
}

export function buildCoreSettingsBody(core) {
  const { fragmentLengthsText, fragmentDelaysText, ...coreSettings } = core
  return {
    ...coreSettings,
    fragmentLengths: parseLines(fragmentLengthsText),
    fragmentDelays: parseLines(fragmentDelaysText),
    mux4RayConcurrency: nullableNumber(coreSettings.mux4RayConcurrency),
    mux4RayXudpConcurrency: nullableNumber(coreSettings.mux4RayXudpConcurrency),
    mux4SboxMaxConnections: defaultedNumber(coreSettings.mux4SboxMaxConnections),
    hy2UpMbps: defaultedNumber(coreSettings.hy2UpMbps),
    hy2DownMbps: defaultedNumber(coreSettings.hy2DownMbps),
  }
}

export function buildApplicationSettingsBody(app) {
  return { ...app, geoAutoUpdateInterval: defaultedNumber(app.geoAutoUpdateInterval) }
}

export function buildSaveAllSettingsSections({ inbound, core, app, speed, coreTypes }) {
  return [
    {
      key: 'settings.inbound',
      path: '/api/settings/inbound',
      body: { ...inbound, localPort: requiredNumber(inbound.localPort), destOverride: inbound.destOverride || [] },
    },
    {
      key: 'settings.core',
      path: '/api/settings/core',
      body: buildCoreSettingsBody(core),
    },
    {
      key: 'settings.application',
      path: '/api/settings/application',
      body: buildApplicationSettingsBody(app),
    },
    {
      key: 'settings.speedtest',
      path: '/api/settings/speedtest',
      body: buildSpeedSettingsBody(speed),
    },
    {
      key: 'settings.coreTypes',
      path: '/api/settings/core-types',
      body: { mappings: coreTypes || [] },
    },
  ]
}
