import { useCallback, useEffect, useMemo, useState } from 'react'
import type { FormEvent } from 'react'
import './App.css'

type ApiResponse = {
  success: boolean
  message: string
}

type CameraInfo = {
  cameraId: string
  rtspUrl: string
  isRecording: boolean
}

const getErrorMessage = async (response: Response): Promise<string> => {
  const text = await response.text()
  return text || response.statusText
}

function App() {
  const [cameraId, setCameraId] = useState('cam1')
  const [rtspUrl, setRtspUrl] = useState('rtsp://127.0.0.1:8554/stream')
  const [status, setStatus] = useState('Ready')
  const [isBusy, setIsBusy] = useState(false)
  const [frameVersion, setFrameVersion] = useState(0)
  const [webSocketEnabled, setWebSocketEnabled] = useState(false)
  const [liveFrameUrl, setLiveFrameUrl] = useState<string | null>(null)
  const [cameras, setCameras] = useState<CameraInfo[]>([])

  const singleFrameUrl = useMemo(
    () => `/api/vms/cameras/${encodeURIComponent(cameraId)}/jpeg/frame?v=${frameVersion}`,
    [cameraId, frameVersion]
  )

  const loadCameras = useCallback(async (preferredCameraId?: string) => {
    try {
      const response = await fetch('/api/vms/cameras')
      if (!response.ok) {
        throw new Error(await getErrorMessage(response))
      }

      const cameraList: CameraInfo[] = await response.json()
      setCameras(cameraList)

      if (preferredCameraId) {
        const preferredCamera = cameraList.find((camera) => camera.cameraId === preferredCameraId)
        if (preferredCamera) {
          setCameraId(preferredCamera.cameraId)
          setRtspUrl(preferredCamera.rtspUrl)
          return
        }
      }

      const selectedCamera = cameraList.find((camera) => camera.cameraId === cameraId)
      if (selectedCamera) {
        setRtspUrl(selectedCamera.rtspUrl)
      }
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Unknown error'
      setStatus(`Error loading cameras: ${message}`)
    }
  }, [cameraId])

  useEffect(() => {
    void loadCameras()
  }, [loadCameras])

  useEffect(() => {
    if (!webSocketEnabled) {
      return
    }

    const wsProtocol = window.location.protocol === 'https:' ? 'wss' : 'ws'
    const ws = new WebSocket(`${wsProtocol}://${window.location.host}/api/vms/cameras/${encodeURIComponent(cameraId)}/jpeg/ws`)

    ws.binaryType = 'blob'

    ws.onopen = () => setStatus('WebSocket JPEG stream connected')
    ws.onerror = () => setStatus('WebSocket JPEG stream error')
    ws.onclose = () => setStatus('WebSocket JPEG stream disconnected')
    ws.onmessage = (event) => {
      const objectUrl = URL.createObjectURL(event.data as Blob)
      setLiveFrameUrl((previousUrl) => {
        if (previousUrl) {
          URL.revokeObjectURL(previousUrl)
        }

        return objectUrl
      })
    }

    return () => {
      ws.close()
      setLiveFrameUrl((previousUrl) => {
        if (previousUrl) {
          URL.revokeObjectURL(previousUrl)
        }

        return null
      })
    }
  }, [cameraId, webSocketEnabled])

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
      await loadCameras(cameraId)
    } catch (error) {
      const message = error instanceof Error ? error.message : 'Unknown error'
      setStatus(`Error: ${message}`)
    } finally {
      setIsBusy(false)
    }
  }

  const onAddCamera = async (event: FormEvent) => {
    event.preventDefault()
    const newCameraId = cameraId

    await runAction(() =>
      fetch('/api/vms/cameras', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          cameraId: newCameraId,
          rtspUrl,
          startImmediately: false
        })
      })
    )

    await loadCameras(newCameraId)
  }

  const onCameraSelect = (selectedCameraId: string) => {
    setCameraId(selectedCameraId)
    const selectedCamera = cameras.find((camera) => camera.cameraId === selectedCameraId)
    if (selectedCamera) {
      setRtspUrl(selectedCamera.rtspUrl)
    }
  }

  return (
    <main className="layout">
      <h1>VMS gRPC Test Console</h1>

      <form className="card" onSubmit={onAddCamera}>
        <h2>Camera</h2>
        <label>
          Added cameras
          <div className="inline-fields">
            <select
              value={cameras.some((camera) => camera.cameraId === cameraId) ? cameraId : ''}
              onChange={(event) => onCameraSelect(event.target.value)}
            >
              <option value="">Select added camera</option>
              {cameras.map((camera) => (
                <option key={camera.cameraId} value={camera.cameraId}>
                  {camera.cameraId} ({camera.isRecording ? 'recording' : 'idle'})
                </option>
              ))}
            </select>
            <button type="button" disabled={isBusy} onClick={() => void loadCameras(cameraId)}>Refresh list</button>
          </div>
        </label>
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
                  method: 'POST'
                })
              )
            }
          >
            Start JPEG stream
          </button>
          <button disabled={isBusy} onClick={() => runAction(() => fetch(`/api/vms/cameras/${encodeURIComponent(cameraId)}/jpeg/stop`, { method: 'POST' }))}>Stop JPEG stream</button>
        </div>
      </section>

      <section className="card stream">
        <h2>Frames</h2>
        <label>
          <input
            type="checkbox"
            checked={webSocketEnabled}
            onChange={(event) => setWebSocketEnabled(event.target.checked)}
          />
          Show live frames over WebSocket
        </label>
        {webSocketEnabled && (
          <img className="frame" src={liveFrameUrl ?? undefined} alt="Live WebSocket JPEG stream" />
        )}
        <button onClick={() => setFrameVersion((value) => value + 1)}>Refresh single frame</button>
        <img className="frame" src={singleFrameUrl} alt="Single JPEG frame" />
      </section>

      <p className="status">{status}</p>
    </main>
  )
}

export default App
