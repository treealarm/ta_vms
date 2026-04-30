import { useEffect, useMemo } from 'react'
import {
  Alert,
  Box,
  Card,
  CardContent,
  Grid,
  Stack
} from '@mui/material'

import { loadCameras, loadServers, removeCamera } from './store/liveSlice'
import { loadPlayers, removePlayer } from './store/archSlice'
import { useAppDispatch, useAppSelector } from './store'
import CameraUnifiedCard from './components/CameraUnifiedCard'
import LogJournalPanel from './components/LogJournalPanel'
import ServerCameraSetupPanel from './components/ServerCameraSetupPanel'

const MAX_CAMERAS = 4

function App() {
  const dispatch = useAppDispatch()

  const status = useAppSelector((state) => state.live.status !== 'Ready' ? state.live.status : state.arch.status !== 'Ready' ? state.arch.status : state.fs.status)

  const camerasMap = useAppSelector((state) => state.live.cameraStatesById)
  const servers = useAppSelector((state) => state.live.servers)

  const { visibleCameras, total } = useMemo(() => {
    const arr = Object.values(camerasMap)
    return {
      visibleCameras: arr.slice(0, MAX_CAMERAS),
      total: arr.length
    }
  }, [camerasMap])

  const isLimitReached = total >= MAX_CAMERAS

  useEffect(() => {
    let cancelled = false
    let retryTimer: ReturnType<typeof setTimeout> | null = null

    const bootstrap = async (attempt: number) => {
      if (cancelled) {
        return
      }

      const [serversResult, camerasResult, playersResult] = await Promise.all([
        dispatch(loadServers()),
        dispatch(loadCameras()),
        dispatch(loadPlayers())
      ])

      const hasError =
        loadServers.rejected.match(serversResult) ||
        loadCameras.rejected.match(camerasResult) ||
        loadPlayers.rejected.match(playersResult)

      if (hasError && attempt < 20 && !cancelled) {
        retryTimer = setTimeout(() => {
          void bootstrap(attempt + 1)
        }, 1500)
      }
    }

    void bootstrap(0)

    return () => {
      cancelled = true
      if (retryTimer != null) {
        clearTimeout(retryTimer)
      }
    }
  }, [dispatch])

  const refreshLists = async () => {
    await Promise.all([dispatch(loadServers()), dispatch(loadCameras()), dispatch(loadPlayers())])
  }

  return (
    <Box sx={{ width: '100%', p: { xs: 2, md: 4 }, pb: 36 }}>
      <Stack spacing={2.5}>
        <ServerCameraSetupPanel
          cameras={Object.values(camerasMap)}
          servers={servers}
          isLimitReached={isLimitReached}
          maxCameras={MAX_CAMERAS}
          onRefresh={refreshLists}
        />

        <Card>
          <CardContent>
            <Stack spacing={2}>
              <Grid container spacing={2}>
                {visibleCameras.map((camera) => (
                  <Grid key={camera.cameraId} size={{ xs: 12, sm: 6 }}>
                    <CameraUnifiedCard
                      camera={camera}
                      onRemoveCamera={async (cameraToDelete) => {
                        const result = await dispatch(
                          removeCamera({
                            cameraId: cameraToDelete.cameraId
                          })
                        )

                        if (!removeCamera.rejected.match(result)) {
                          await refreshLists()
                        }
                      }}

                      onRemovePlayer={async (playerToDelete) => {
                        const result = await dispatch(
                          removePlayer({
                            clientId: playerToDelete.client_id,
                            cameraId: playerToDelete.camera_id
                          })
                        )

                        if (!removePlayer.rejected.match(result)) {
                          await refreshLists()
                        }
                      }}
                    />
                  </Grid>
                ))}
              </Grid>

            </Stack>
          </CardContent>
        </Card>

        <Alert severity={status.startsWith('Error') ? 'error' : 'info'}>{status}</Alert>
      </Stack>
      <LogJournalPanel />
    </Box>
  )
}

export default App
