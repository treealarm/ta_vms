import { useEffect, useState } from 'react'
import { BrowserRouter, Navigate, Route, Routes } from 'react-router-dom'
import { PendingEditsProvider } from './components/settings/PendingEdits'
import { Alert, Box, Button, CircularProgress, IconButton, Tab, Tabs, Typography } from '@mui/material'
import CloseIcon from '@mui/icons-material/Close'
import { useTranslation } from 'react-i18next'
import i18n from './i18n'

import { loadCameras, loadServers } from './store/liveSlice'
import { loadMonitors } from './store/monitorsSlice'
import { loadMediaMtxServers } from './store/mediaMtxSlice'
import { loadFrontendConfig, loadHealth } from './store/frontendConfigSlice'
import { toggleLogPanel } from './store/logSlice'
import { useAppDispatch, useAppSelector } from './store'
import RequireRole from './auth/RequireRole'
import LoginPage from './components/LoginPage'
import TestLivePage from './components/TestLivePage'
import OperatorView from './components/OperatorView'
import AdminView from './components/AdminView'
import LogJournalPanel from './components/LogJournalPanel'
import OnvifEventsLogPanel from './components/OnvifEventsLogPanel'
import FsActivityPanel from './components/FsActivityPanel'
import EntityStatePanel from './components/EntityStatePanel'
import AnalyticsActivityPanel from './components/AnalyticsActivityPanel'

function LangToggle() {
  // The button label is the language code itself, so nothing here needs translating — i18n is
  // driven directly through the i18n instance below.
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

/** Health poll interval; also the delay before a persistent outage is surfaced (see persistentlyBroken). */
const HEALTH_POLL_MS = 15000

const BOOTSTRAP_RETRY_MS = 1500
const BOOTSTRAP_RETRY_CAP_MS = 15_000
const BOOTSTRAP_MAX_ATTEMPTS = 12

export default function App() {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const logsOpen = useAppSelector((s) => s.logs.panelOpen)
  const [debugTab, setDebugTab] = useState(0)
  const authToken = useAppSelector((s) => s.auth.token)
  const authRestoring = useAppSelector((s) => s.auth.restoring)
  const authExpired = useAppSelector((s) => s.auth.expired)

  // Re-runs whenever authToken changes, not just once on mount — otherwise a slow login (login
  // happens via client-side navigate(), App never remounts) can outlast the retry budget below
  // and leave every list empty until the user manually reloads the page.
  useEffect(() => {
    let cancelled = false
    let retryTimer: ReturnType<typeof setTimeout> | null = null

    const bootstrap = async (attempt: number) => {
      // Nothing to load for a session that is being revived or is already over — these would only
      // collect 401s and spend connections the revival itself needs.
      if (cancelled || authRestoring || authExpired) return
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
      // Backing off rather than retrying at a fixed rate. The usual reason all four of these fail
      // at once is that the gateway is still starting, which takes tens of seconds — and a browser
      // allows six connections per origin, so four requests every 1.5s for that whole window left
      // almost nothing for live video to negotiate on. Doubling reaches just as far in a fraction
      // of the requests.
      if (hasError && attempt < BOOTSTRAP_MAX_ATTEMPTS && !cancelled) {
        const delay = Math.min(BOOTSTRAP_RETRY_MS * 2 ** attempt, BOOTSTRAP_RETRY_CAP_MS)
        retryTimer = setTimeout(() => { void bootstrap(attempt + 1) }, delay)
      }
    }

    void bootstrap(0)
    return () => {
      cancelled = true
      if (retryTimer != null) clearTimeout(retryTimer)
    }
  }, [dispatch, authToken, authRestoring, authExpired])

  // Anonymous and cheap — used as the connectivity probe below, so retry indefinitely until the
  // backend actually responds (no attempt cap, unlike the auth-dependent bootstrap above).
  const configStatus = useAppSelector((s) => s.frontendConfig.status)
  const liveTestPlayer = useAppSelector((s) => s.frontendConfig.liveTestPlayer)
  const probeError = useAppSelector((s) => s.frontendConfig.lastError)
  const failedAttempts = useAppSelector((s) => s.frontendConfig.failedAttempts)
  const brokenDependencies = useAppSelector((s) => s.frontendConfig.brokenDependencies)
  // Everything is "unavailable" for the first few seconds while the services warm up, so a single
  // failing poll is not worth alarming about. Requiring a second consecutive one delays the banner
  // by exactly one health interval (see HEALTH_POLL_MS) — long enough to skip startup, short enough
  // that a real outage is still reported promptly.
  const persistentlyBroken = brokenDependencies.filter((d) => d.consecutive >= 2)
  useEffect(() => {
    let cancelled = false
    let retryTimer: ReturnType<typeof setTimeout> | null = null

    const tryLoad = async () => {
      if (cancelled) return
      const result = await dispatch(loadFrontendConfig())
      if (loadFrontendConfig.rejected.match(result) && !cancelled) {
        retryTimer = setTimeout(() => { void tryLoad() }, 2000)
        return
      }
      // The gateway answers even while its own dependencies are down, so ask what is actually
      // broken instead of leaving the user with a page that loads but cannot show any camera.
      if (!cancelled) void dispatch(loadHealth())
    }

    void tryLoad()
    // Kept polling rather than probed once: a restart race would otherwise pin a stale "vmscfg
    // unavailable" banner for the whole session, and a dependency that dies later would never
    // raise one. The endpoint answers from a 5s server-side cache, so this stays cheap.
    const healthTimer = setInterval(() => { void dispatch(loadHealth()) }, HEALTH_POLL_MS)

    return () => {
      cancelled = true
      if (retryTimer != null) clearTimeout(retryTimer)
      clearInterval(healthTimer)
    }
  }, [dispatch])

  return (
    <BrowserRouter>
      <PendingEditsProvider>
      <Box sx={{ display: 'flex', flexDirection: 'column', height: '100dvh', overflow: 'hidden' }}>
        {/* The gateway is up but something it needs is not, so the app would load and then show
            nothing but empty lists — say which service is broken instead. */}
        {persistentlyBroken.length > 0 && (
          <Alert severity="warning" square sx={{ flexShrink: 0, py: 0.25 }}>
            {t('app.dependencyDown', {
              services: persistentlyBroken.map((d) => `${d.name} (${d.error})`).join(', '),
            })}
          </Alert>
        )}
        <Box sx={{ flex: 1, minHeight: 0, overflow: 'hidden' }}>
          <Routes>
            {/* Always reachable regardless of the backend connectivity probe below — neither
                depends on it, and /login must stay reachable even if the backend never responds. */}
            <Route path="/login" element={<LoginPage />} />
            {/* Dev-only and anonymous end to end: the backend refuses to enable it alongside
                Keycloak, so don't offer the page when it says the endpoints aren't there. */}
            {liveTestPlayer && <Route path="/test/live" element={<TestLivePage />} />}
            {authRestoring ? (
              // A stored session is being revived. Neither the app nor the login form is the right
              // answer yet, and showing either and then swapping it is what looked so careless.
              <Route path="*" element={
                <Box sx={{
                  display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center',
                  height: '100%', gap: 2,
                }}>
                  <CircularProgress />
                  <Typography color="text.secondary">{t('app.connecting')}</Typography>
                </Box>
              } />
            ) : authExpired ? (
              // The session is over: login, without drawing a single page of the app first. The
              // /login route above still wins on its own path, so this cannot loop.
              <Route path="*" element={<Navigate to="/login" replace />} />
            ) : configStatus !== 'succeeded' ? (
              // Don't render monitor/admin views until we've confirmed the backend is actually
              // reachable — otherwise a cold-started frontend briefly shows an empty monitor view
              // (no token yet looks the same as "auth disabled") instead of a neutral loading state.
              <Route path="*" element={
                <Box sx={{
                  display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center',
                  height: '100%', gap: 2, px: 3, textAlign: 'center',
                }}>
                  <CircularProgress />
                  <Typography color="text.secondary">{t('app.connecting')}</Typography>
                  {/* Quiet for the first few seconds: a cold start races the backend and an error
                      shown then would be noise. After that, silence is the unhelpful answer. */}
                  {failedAttempts >= 3 && (
                    <Box sx={{ maxWidth: 520 }}>
                      <Typography variant="body2" color="error">
                        {t('app.backendUnreachable')}
                      </Typography>
                      <Typography variant="caption" color="text.secondary" display="block" sx={{ mt: 0.5 }}>
                        {t('app.backendUnreachableHint')}
                      </Typography>
                      {probeError && (
                        <Typography variant="caption" color="text.secondary" display="block" sx={{ mt: 0.5 }}>
                          {probeError}
                        </Typography>
                      )}
                    </Box>
                  )}
                </Box>
              } />
            ) : (
              <>
                <Route path="/operator/*" element={<RequireRole><OperatorView /></RequireRole>} />
                <Route path="/admin/*" element={<RequireRole role="admin"><AdminView /></RequireRole>} />
                <Route path="*" element={<Navigate to="/operator/monitors" replace />} />
              </>
            )}
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
                <Tab label={t('analyticsActivity.tabLabel')} sx={{ minHeight: 32, py: 0.25, fontSize: '0.75rem' }} />
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
              {debugTab === 4 && <AnalyticsActivityPanel />}
            </Box>
          </Box>
        )}
      </Box>
      </PendingEditsProvider>
    </BrowserRouter>
  )
}
