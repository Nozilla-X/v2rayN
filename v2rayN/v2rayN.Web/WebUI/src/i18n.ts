import { createI18n } from 'vue-i18n'
import zhCN from './locales/zh-CN.json'
import zhTW from './locales/zh-TW.json'
import enUS from './locales/en-US.json'

const savedLocale = localStorage.getItem('v2rayn-web-locale')
const initialLocale = savedLocale === 'zh-TW' || savedLocale === 'en-US' ? savedLocale : 'zh-CN'

export default createI18n({
  legacy: false,
  locale: initialLocale,
  fallbackLocale: 'zh-CN',
  messages: { 'zh-CN': zhCN, 'zh-TW': zhTW, 'en-US': enUS },
})
