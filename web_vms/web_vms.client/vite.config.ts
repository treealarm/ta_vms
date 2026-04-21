import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

const backendTarget = 'http://127.0.0.1:5134' // твой ASP.NET backend

export default defineConfig({
  plugins: [react()],
  server: {
    host: '0.0.0.0',
    port: 5173, // Vite dev-server
    strictPort: true, // не менять порт автоматически
    proxy: {
      // proxy для API-запросов
      '/api': { target: backendTarget, changeOrigin: true, secure: false, ws: true },
      '/swagger': { target: backendTarget, changeOrigin: true, secure: false },
      '/public': { target: backendTarget, changeOrigin: true, secure: false },
      '/static_files': { target: backendTarget, changeOrigin: true, secure: false },
    },
  },
})
