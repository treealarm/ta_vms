import i18n from 'i18next'
import { initReactI18next } from 'react-i18next'
import en from './locales/en/translation.json'
import es from './locales/es/translation.json'

const saved = localStorage.getItem('lang')
const initial = saved === 'es' ? 'es' : 'en'

void i18n.use(initReactI18next).init({
  lng: initial,
  fallbackLng: 'en',
  resources: {
    en: { translation: en },
    es: { translation: es },
  },
  interpolation: {
    escapeValue: false,
  },
})

export default i18n
