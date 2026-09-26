import assert from 'node:assert/strict'
import test from 'node:test'
import {
  buildSaveAllSettingsSections,
  buildSpeedSettingsBody,
  nullableNumber,
} from '../Src/Composables/settingsPayloads.js'

test('nullableNumber preserves null and empty values as null', () => {
  assert.equal(nullableNumber(null), null)
  assert.equal(nullableNumber(undefined), null)
  assert.equal(nullableNumber(''), null)
  assert.equal(nullableNumber('   '), null)
})

test('nullableNumber accepts valid numbers and does not coerce zero to null', () => {
  assert.equal(nullableNumber(25), 25)
  assert.equal(nullableNumber('25'), 25)
  assert.equal(nullableNumber(0), 0)
  assert.equal(nullableNumber('0'), 0)
})

test('nullableNumber rejects invalid and non-finite values', () => {
  for (const value of ['invalid', Number.NaN, Number.POSITIVE_INFINITY, true, {}]) {
    assert.throws(() => nullableNumber(value), TypeError)
  }
})

test('single speed-test save preserves nullable values and validates real zero', () => {
  const unchanged = {
    speedTestTimeout: 10000,
    mixedConcurrencyCount: 4,
    speedTestPageSize: null,
    speedTestDelayInterval: null,
  }
  assert.deepEqual(buildSpeedSettingsBody(unchanged), unchanged)
  assert.equal(buildSpeedSettingsBody({ ...unchanged, speedTestPageSize: '' }).speedTestPageSize, null)
  assert.equal(buildSpeedSettingsBody({ ...unchanged, speedTestDelayInterval: 0 }).speedTestDelayInterval, 0)
  assert.throws(() => buildSpeedSettingsBody({ ...unchanged, speedTestPageSize: 'invalid' }), TypeError)
})

test('save-all with unchanged settings emits nullable speed values without converting them to zero', () => {
  const sections = buildSaveAllSettingsSections({
    inbound: { localPort: 10808, destOverride: [] },
    core: {
      mux4RayConcurrency: null,
      mux4RayXudpConcurrency: null,
      mux4SboxMaxConnections: 4,
      hy2UpMbps: 0,
      hy2DownMbps: 0,
      fragmentLengthsText: '',
      fragmentDelaysText: '',
    },
    app: { geoAutoUpdateInterval: 0 },
    speed: {
      speedTestTimeout: 10000,
      mixedConcurrencyCount: 4,
      speedTestPageSize: null,
      speedTestDelayInterval: null,
    },
    coreTypes: [],
  })

  assert.equal(sections[3].path, '/api/settings/speedtest')
  assert.equal(sections[3].body.speedTestPageSize, null)
  assert.equal(sections[3].body.speedTestDelayInterval, null)
  assert.equal(sections[1].body.mux4RayConcurrency, null)
  assert.equal(sections[1].body.mux4RayXudpConcurrency, null)
})
