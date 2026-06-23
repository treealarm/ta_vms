import { useEffect, useState } from 'react'
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import { Box, Button, IconButton, Tab, Tabs } from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import { useTranslation } from 'react-i18next'
import i18n from './i18n'

import { loadCameras, loadServers } from './store/liveSlice'
import { loadMonitors } from './store/monitorsSlice'
import { loadMediaMtxServers } from './store/mediaMtxSlice'
import { loadFrontendConfig } from './store/frontendConfigSlice'
import { toggleLogPanel } from './store/logSlice'
import { useAppDispatch, useAppSelector } from './store'
import RequireRole from './auth/RequireRole'
import LoginPage from './components/LoginPage'
import OperatorView from './components/OperatorView'
import AdminView from './components/AdminView'
import LogJournalPanel from './components/LogJournalPanel'
import OnvifEventsLogPanel from './components/OnvifEventsLogPanel'
import FsActivityPanel from './components/FsActivityPanel'
import EntityStatePanel from './components/EntityStatePanel'

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
  const authToken = useAppSelector((s) => s.auth.token)

  // Re-runs whenever authToken changes, not just once on mount — otherwise a slow login (login
  // happens via client-side navigate(), App never remounts) can outlast the retry budget below
  // and leave every list empty until the user manually reloads the page.
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
  }, [dispatch, authToken])

  // Anonymous, static config — no auth/retry dance needed, just fetch once.
  useEffect(() => {
    void dispatch(loadFrontendConfig())
  }, [dispatch])

  return (
    <BrowserRouter>
      <Box sx={{ display: 'flex', flexDirection: 'column', height: '100dvh', overflow: 'hidden' }}>
        <Box sx={{ flex: 1, minHeight: 0, overflow: 'hidden' }}>
          <Routes>
            <Route path="/login" element={<LoginPage />} />
            <Route path="/operator/*" element={<RequireRole><OperatorView /></RequireRole>} />
            <Route path="/admin/*" element={<RequireRole role="admin"><AdminView /></RequireRole>} />
            <Route path="*" element={<Navigate to="/operator/monitors" replace />} />
          </Routes>
        </Box>
        {logsOpen && (
          <Box sx={{ height: '50vh', flexShrink: 0, borderTop: 1, borderColor: 'divider', display: 'flex', flexDirection: 'column', overflow: 'hidden' }}>
            <Box sx={{ borderBottom: 1, borderColor: 'divider', px: 1, display: 'flex', alignItems: 'center' }}>
              <Tabs value={debugTab} onChange={(_, v: number) => setDebugTab(v)} sx={{ minHeight: 32, flex: 1 }} TabIndicatorProps={{ style: { height: 2 } }}>
                <Tab label={t('logs.title')} sx={{ minHeight: 32, py: 0.25, fontSize: '0.75rem' }} />
                <Tab label={t('onvifFilters.tabDebug')} sx={{ minHeight: 32, py: 0.25, fontSize: '0.75rem' }} />
                <Tab label={t('fsActivity.tabLabel')} sx={{ minHeight: 32, py: 0.25, fontSize: '0.75rem' }} />
                <Tab label={t('entityState.tabLabel')} sx={{ minHeight: 32, py: 0.25, fontSize: '0.75rem' }} />
              </Tabs>
              <IconButton size="small" onClick={() => dispatch(toggleLogPanel())} sx={{ ml: 1 }}>
                <CloseIcon fontSize="small" />
              </IconButton>
            </Box>
            <Box sx={{ flex: 1, minHeight: 0, overflow: 'hidden', p: debugTab === 0 ? 1 : 0 }}>
              {debugTab === 0 && <LogJournalPanel />}
              {debugTab === 1 && <OnvifEventsLogPanel />}
              {debugTab === 2 && <FsActivityPanel />}
              {debugTab === 3 && <EntityStatePanel />}
            </Box>
          </Box>
        )}
      </Box>
    </BrowserRouter>
  )
}
