import { useEffect, useState } from 'react'
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import { Button } from '@mui/material'
import { useTranslation } from 'react-i18next'
import i18n from './i18n'

import { loadCameras, loadServers } from './store/liveSlice'
import { loadMonitors } from './store/monitorsSlice'
import { loadMediaMtxServers } from './store/mediaMtxSlice'
import { useAppDispatch } from './store'
import OperatorView from './components/OperatorView'
import AdminView from './components/AdminView'

function LangToggle() {
  const { t: _t } = useTranslation()
  const [lang, setLang] = useState<'en' | 'es'>(
    () => (localStorage.getItem('lang') === 'es' ? 'es' : 'en')
  )
  const toggle = () => {
    const next = lang === 'en' ? 'es' : 'en'
    setLang(next)
    void i18n.changeLanguage(next)
    localStorage.setItem('lang', next)
  }
  return (
    <Button
      size="small"
      variant="text"
      onClick={toggle}
      sx={{ minWidth: 0, px: 1, py: 0.5, fontSize: '0.7rem', color: 'text.secondary' }}
    >
      {lang === 'en' ? 'ES' : 'EN'}
    </Button>
  )
}

export { LangToggle }

export default function App() {
  const dispatch = useAppDispatch()

  useEffect(() => {
    let cancelled = false
    let retryTimer: ReturnType<typeof setTimeout> | null = null

    const bootstrap = async (attempt: number) => {
      if (cancelled) return
      const [camerasResult, serversResult, monitorsResult, mtxResult] = await Promise.all([
        dispatch(loadCameras()),
        dispatch(loadServers()),
        dispatch(loadMonitors()),
        dispatch(loadMediaMtxServers()),
      ])
      const hasError =
        loadCameras.rejected.match(camerasResult) ||
        loadServers.rejected.match(serversResult) ||
        loadMonitors.rejected.match(monitorsResult) ||
        loadMediaMtxServers.rejected.match(mtxResult)
      if (hasError && attempt < 20 && !cancelled) {
        retryTimer = setTimeout(() => { void bootstrap(attempt + 1) }, 1500)
      }
    }

    void bootstrap(0)
    return () => {
      cancelled = true
      if (retryTimer != null) clearTimeout(retryTimer)
    }
  }, [dispatch])

  return (
    <BrowserRouter>
      <Routes>
        <Route path="/operator" element={<OperatorView />} />
        <Route path="/admin" element={<AdminView />} />
        <Route path="*" element={<Navigate to="/operator" replace />} />
      </Routes>
    </BrowserRouter>
  )
}
