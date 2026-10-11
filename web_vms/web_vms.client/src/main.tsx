import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { CssBaseline, ThemeProvider } from '@mui/material'
import { Provider } from 'react-redux'
import './i18n'
import './App.css'
import App from './App.tsx'
import { store } from './store'
import { installAuthFetch } from './auth/authFetch'
import { netTerrainTheme } from './theme/netTerrainTheme'
// Leaflet's own layout rules (panes, markers, controls); the maps tabs need them on every page load.
import 'leaflet/dist/leaflet.css'

installAuthFetch()

const theme = netTerrainTheme

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <Provider store={store}>
      <ThemeProvider theme={theme}>
        <CssBaseline />
        <App />
      </ThemeProvider>
    </Provider>
  </StrictMode>
)
