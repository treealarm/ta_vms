import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

const backendTarget = 'http://127.0.0.1:5134' // local ASP.NET backend (web_vms.Server)

export default defineConfig({
  plugins: [react()],
  server: {
    host: '0.0.0.0',
    port: 5173, // Vite dev-server
    strictPort: true, // fail if port 5173 is taken instead of picking another
    proxy: {
      // API and related routes → backend during dev
      '/api': { target: backendTarget, changeOrigin: true, secure: false, ws: true },
      '/swagger': { target: backendTarget, changeOrigin: true, secure: false },
      '/public': { target: backendTarget, changeOrigin: true, secure: false },
      '/static_files': { target: backendTarget, changeOrigin: true, secure: false },
    },
  },
})
