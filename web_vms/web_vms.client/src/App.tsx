import { FormEvent, useMemo, useState } from 'react'
import './App.css'

type ApiResponse = {
  success: boolean
  message: string
}

const getErrorMessage = async (response: Response): Promise<string> => {
  const text = await response.text()
  return text || response.statusText
}

function App() {
  const [cameraId, setCameraId] = useState('camera-1')
  const [rtspUrl, setRtspUrl] = useState('rtsp://127.0.0.1:8554/stream')
  const [jpegHost, setJpegHost] = useState('127.0.0.1')
  const [jpegPort, setJpegPort] = useState(8090)
  const [status, setStatus] = useState('Ready')
  const [isBusy, setIsBusy] = useState(false)
  const [frameVersion, setFrameVersion] = useState(0)

  const singleFrameUrl = useMemo(
    () => `/api/vms/cameras/${encodeURIComponent(cameraId)}/jpeg/frame?v=${frameVersion}`,
    [cameraId, frameVersion]
  )

  const mjpegUrl = useMemo(
    () => `/api/vms/cameras/${encodeURIComponent(cameraId)}/jpeg/mjpeg`,
    [cameraId]
  )

  const runAction = async (action: () => Promise<Response>) => {
    setIsBusy(true)
    try {
      const response = await action()
      if (!response.ok) {
        setStatus(`Error: ${await getErrorMessage(response)}`)
        return
      }

      const result: ApiResponse = await response.json()
      setStatus(`${result.success ? 'OK' : 'Fail'}: ${result.message}`)
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Unknown error'
      setStatus(`Error: ${message}`)
    } finally {
      setIsBusy(false)
    }
  }

  const onAddCamera = async (event: FormEvent) => {
    event.preventDefault()
    await runAction(() =>
      fetch('/api/vms/cameras', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          cameraId,
          rtspUrl,
          startImmediately: false
        })
      })
    )
  }

  return (
    <main className="layout">
      <h1>VMS gRPC Test Console</h1>

      <form className="card" onSubmit={onAddCamera}>
        <h2>Camera</h2>
        <label>
          Camera ID
          <input value={cameraId} onChange={(event) => setCameraId(event.target.value)} required />
        </label>
        <label>
          RTSP URL
          <input value={rtspUrl} onChange={(event) => setRtspUrl(event.target.value)} required />
        </label>
        <button disabled={isBusy} type="submit">Add camera</button>
      </form>

      <section className="card actions">
        <h2>Commands</h2>
        <div className="actions-grid">
          <button disabled={isBusy} onClick={() => runAction(() => fetch(`/api/vms/cameras/${encodeURIComponent(cameraId)}/recording/start`, { method: 'POST' }))}>Start recording</button>
          <button disabled={isBusy} onClick={() => runAction(() => fetch(`/api/vms/cameras/${encodeURIComponent(cameraId)}/recording/stop`, { method: 'POST' }))}>Stop recording</button>
          <button
            disabled={isBusy}
            onClick={() =>
              runAction(() =>
                fetch(`/api/vms/cameras/${encodeURIComponent(cameraId)}/jpeg/start`, {
                  method: 'POST',
                  headers: { 'Content-Type': 'application/json' },
                  body: JSON.stringify({ host: jpegHost, port: Number(jpegPort) })
                })
              )
            }
          >
            Start JPEG stream
          </button>
          <button disabled={isBusy} onClick={() => runAction(() => fetch(`/api/vms/cameras/${encodeURIComponent(cameraId)}/jpeg/stop`, { method: 'POST' }))}>Stop JPEG stream</button>
        </div>

        <div className="inline-fields">
          <label>
            JPEG host
            <input value={jpegHost} onChange={(event) => setJpegHost(event.target.value)} />
          </label>
          <label>
            JPEG port
            <input type="number" value={jpegPort} onChange={(event) => setJpegPort(Number(event.target.value))} min={1} max={65535} />
          </label>
        </div>
      </section>

      <section className="card stream">
        <h2>Frames</h2>
        <p>MJPEG stream from gRPC:</p>
        <img className="frame" src={mjpegUrl} alt="Live MJPEG stream" />
        <button onClick={() => setFrameVersion((value) => value + 1)}>Refresh single frame</button>
        <img className="frame" src={singleFrameUrl} alt="Single JPEG frame" />
      </section>

      <p className="status">{status}</p>
    </main>
  )
}

export default App
