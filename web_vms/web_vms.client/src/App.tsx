import { useEffect, useState } from 'react'
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import { Box, Button, Tab, Tabs } from '@mui/material'
import { useTranslation } from 'react-i18next'
import i18n from './i18n'

import { loadCameras, loadServers } from './store/liveSlice'
import { loadMonitors } from './store/monitorsSlice'
import { loadMediaMtxServers } from './store/mediaMtxSlice'
import { useAppDispatch, useAppSelector } from './store'
import OperatorView from './components/OperatorView'
import AdminView from './components/AdminView'
import LogJournalPanel from './components/LogJournalPanel'
import OnvifEventsLogPanel from './components/OnvifEventsLogPanel'
import FsActivityPanel from './components/FsActivityPanel'

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
  const { t } = useTranslation()
  const logsOpen = useAppSelector((s) => s.logs.panelOpen)
  const [debugTab, setDebugTab] = useState(0)

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
      <Box sx={{ display: 'flex', flexDirection: 'column', height: '100dvh', overflow: 'hidden' }}>
        <Box sx={{ flex: 1, minHeight: 0, overflow: 'hidden' }}>
          <Routes>
            <Route path="/operator" element={<OperatorView />} />
            <Route path="/admin" element={<AdminView />} />
            <Route path="*" element={<Navigate to="/operator" replace />} />
          </Routes>
        </Box>
        {logsOpen && (
          <Box sx={{ height: '50vh', flexShrink: 0, borderTop: 1, borderColor: 'divider', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
            <Box sx={{ borderBottom: 1, borderColor: 'divider', px: 1 }}>
              <Tabs value={debugTab} onChange={(_, v: number) => setDebugTab(v)} sx={{ minHeight: 32 }} TabIndicatorProps={{ style: { height: 2 } }}>
                <Tab label={t('logs.title')} sx={{ minHeight: 32, py: 0.25, fontSize: '0.75rem' }} />
                <Tab label={t('onvifFilters.tabDebug')} sx={{ minHeight: 32, py: 0.25, fontSize: '0.75rem' }} />
                <Tab label={t('fsActivity.tabLabel')} sx={{ minHeight: 32, py: 0.25, fontSize: '0.75rem' }} />
              </Tabs>
            </Box>
            <Box sx={{ flex: 1, minHeight: 0, overflow: 'hidden', p: debugTab === 0 ? 1 : 0 }}>
              {debugTab === 0 && <LogJournalPanel />}
              {debugTab === 1 && <OnvifEventsLogPanel />}
              {debugTab === 2 && <FsActivityPanel />}
            </Box>
          </Box>
        )}
      </Box>
    </BrowserRouter>
  )
}
