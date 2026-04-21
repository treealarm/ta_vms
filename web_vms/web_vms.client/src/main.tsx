import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { CssBaseline, ThemeProvider, createTheme } from '@mui/material'
import { Provider } from 'react-redux'
import './index.css'
import App from './App.tsx'
import { store } from './store'

const theme = createTheme({
  palette: {
    mode: 'dark',
    primary: {
      main: '#60a5fa'
    },
    background: {
      default: '#0f172a',
      paper: '#1e293b'
    }
  },
  shape: {
    borderRadius: 10
  }
})

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
