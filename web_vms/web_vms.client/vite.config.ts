import { defineConfig } from 'vite'
import type { ProxyOptions } from 'vite'
import react from '@vitejs/plugin-react'
import checker from 'vite-plugin-checker'

const backendTarget = 'http://127.0.0.1:5134' // local ASP.NET backend (web_vms.Server)

// http-proxy, which Vite's dev proxy is built on, has no timeouts at all by default. A request
// whose upstream stalls or is reset mid-flight — a gateway restart is enough — then leaves the
// *browser* socket open forever, because nothing ever ends that response. A browser allows six
// connections per origin, so six such events over a session freeze the entire page: every later
// request queues behind them and is never sent.
//
// That failure is very hard to read from the outside. The gateway answers fresh requests in
// milliseconds, Vite answers fresh connections in milliseconds, and Vite's own pool of upstream
// connections sits idle and gets reused — all while the tab shows a loading spinner that never
// finishes and live video stops. Measured here: without these options a request to a stalled
// upstream never returns; with them the connection is released on schedule.
//
// These are inactivity timeouts (Node's socket setTimeout), not deadlines, so a slow but
// progressing download is unaffected.
const PROXY_TIMEOUT_MS = 20_000

// The analytics endpoints include the two Server-Sent Events streams the bbox and event overlays
// live on. Those are long-lived on purpose and go quiet between detections, with a keep-alive
// comment every 15s (see AnalyticsLiveController.HeartbeatInterval) — so they need a bound well
// above that heartbeat, not the ordinary one.
const SSE_PROXY_TIMEOUT_MS = 120_000

const withTimeouts = (timeout: number): Omit<ProxyOptions, 'target'> => ({
  changeOrigin: true,
  secure: false,
  timeout,
  proxyTimeout: timeout,
  configure: (proxy) => {
    // Without a handler an upstream error is unhandled and the client is left hanging; http-proxy
    // usually destroys the response socket before this can write a status, which is fine — what
    // matters is that the browser's connection is released instead of being held for the session.
    proxy.on('error', (err, _req, res) => {
      const response = res as { writeHead?: unknown; headersSent?: boolean; writableEnded?: boolean; destroyed?: boolean; end?: (chunk?: string) => void }
      if (typeof response?.writeHead !== 'function' || response.writableEnded || response.destroyed) return
      if (!response.headersSent) (response.writeHead as (code: number, headers: Record<string, string>) => void)(504, { 'Content-Type': 'text/plain' })
      response.end?.(`dev proxy: ${err.message}`)
    })
  },
})

export default defineConfig({
  plugins: [
    react(),
    checker({ typescript: true }),
  ],
  server: {
    host: '0.0.0.0',
    port: 5173, // Vite dev-server
    strictPort: true, // fail if port 5173 is taken instead of picking another
    proxy: {
      // Order matters: Vite takes the first prefix that matches, so the SSE streams have to be
      // listed before the general /api rule that would otherwise cut them off.
      '/api/analytics': { target: backendTarget, ws: true, ...withTimeouts(SSE_PROXY_TIMEOUT_MS) },
      // API and related routes → backend during dev
      '/api': { target: backendTarget, ws: true, ...withTimeouts(PROXY_TIMEOUT_MS) },
      '/swagger': { target: backendTarget, ...withTimeouts(PROXY_TIMEOUT_MS) },
      '/public': { target: backendTarget, ...withTimeouts(PROXY_TIMEOUT_MS) },
      '/static_files': { target: backendTarget, ...withTimeouts(PROXY_TIMEOUT_MS) },
    },
  },
})
