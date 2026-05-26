import { useImperativeHandle, useMemo, useRef, useState } from 'react'
import {
  Box,
  Button,
  Card,
  CardContent,
  Collapse,
  List,
  ListItemButton,
  ListItemText,
  Stack,
  Typography
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { loadCameras, loadServers } from '../store/liveSlice'
import { loadMediaMtxServers } from '../store/mediaMtxSlice'
import CameraSetupPanel from './CameraSetupPanel'
import type { CameraSetupHandle } from './CameraSetupPanel'
import StreamSetupPanel from './StreamSetupPanel'
import type { StreamSetupHandle } from './StreamSetupPanel'

export type CameraControlHandle = { applyIfDirty: () => Promise<void> }

type DetailHandle = CameraSetupHandle | StreamSetupHandle

type Selection =
  | { kind: 'none' }
  | { kind: 'camera'; cameraId: string; serverId: string }
  | { kind: 'new_camera'; serverId: string }
  | { kind: 'stream'; cameraId: string; streamName: string }
  | { kind: 'new_stream'; cameraId: string }

export default function CameraControlPanel({ ref }: { ref?: React.Ref<CameraControlHandle> }) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const servers = useAppSelector((state) => state.live.servers)
  const camerasMap = useAppSelector((state) => state.live.cameraStatesById)
  const [selection, setSelection] = useState<Selection>({ kind: 'none' })
  const [isVisible, setIsVisible] = useState(true)

  const detailRef = useRef<DetailHandle>(null)

  const serversTree = useMemo(() => {
    const cams = Object.values(camerasMap)
    return servers.map((s) => ({
      serverId: s.serverId,
      cameras: cams.filter((c) => c.serverId === s.serverId)
    }))
  }, [servers, camerasMap])

  const refresh = async () => {
    await Promise.all([dispatch(loadCameras()), dispatch(loadServers()), dispatch(loadMediaMtxServers())])
  }

  // Auto-apply unsaved changes before switching to a different panel
  const setSelectionSafe = async (newSel: Selection) => {
    if (detailRef.current) await detailRef.current.applyIfDirty()
    setSelection(newSel)
  }

  const handleHide = async () => {
    if (detailRef.current) await detailRef.current.applyIfDirty()
    setIsVisible(false)
  }

  // Expose applyIfDirty for App.tsx to call before switching tabs
  useImperativeHandle(ref, () => ({
    applyIfDirty: async () => {
      if (detailRef.current) await detailRef.current.applyIfDirty()
    }
  }))

  return (
    <Box>
      {!isVisible && (
        <Button variant="contained" size="small" onClick={() => setIsVisible(true)}>
          {t('cameraControl.showControls')}
        </Button>
      )}

      <Collapse in={isVisible} timeout={220} unmountOnExit>
        <Card>
          <CardContent>
            <Stack direction={{ xs: 'column', md: 'row' }} spacing={2}>
              {/* Tree */}
              <Box sx={{ minWidth: 260, border: 1, borderColor: 'divider', borderRadius: 1 }}>
                <List dense disablePadding>
                  <ListItemButton onClick={() => void handleHide()}>
                    <ListItemText primary={t('cameraControl.hide')} primaryTypographyProps={{ color: 'text.secondary' }} />
                  </ListItemButton>

                  {serversTree.map((server) => (
                    <Box key={server.serverId}>
                      {/* Server row — click → new camera form */}
                      <ListItemButton
                        selected={selection.kind === 'new_camera' && selection.serverId === server.serverId}
                        onClick={() => void setSelectionSafe({ kind: 'new_camera', serverId: server.serverId })}
                      >
                        <ListItemText
                          primary={server.serverId}
                          secondary={t('cameraControl.newCamera')}
                          primaryTypographyProps={{ fontWeight: 600 }}
                        />
                      </ListItemButton>

                      {server.cameras.map((camera) => (
                        <Box key={camera.cameraId}>
                          {/* Camera row */}
                          <ListItemButton
                            sx={{ pl: 4 }}
                            selected={selection.kind === 'camera' && selection.cameraId === camera.cameraId}
                            onClick={() =>
                              void setSelectionSafe({ kind: 'camera', cameraId: camera.cameraId, serverId: server.serverId })
                            }
                          >
                            <ListItemText primary={camera.cameraId} />
                          </ListItemButton>

                          {/* Stream rows */}
                          {(camera.streams ?? []).map((stream) => (
                            <ListItemButton
                              key={stream.streamName}
                              sx={{ pl: 8 }}
                              selected={
                                selection.kind === 'stream' &&
                                selection.cameraId === camera.cameraId &&
                                selection.streamName === stream.streamName
                              }
                              onClick={() =>
                                void setSelectionSafe({ kind: 'stream', cameraId: camera.cameraId, streamName: stream.streamName })
                              }
                            >
                              <ListItemText
                                primary={stream.streamName}
                                secondary={stream.rtspUrl || stream.profileToken || undefined}
                                secondaryTypographyProps={{ noWrap: true }}
                              />
                            </ListItemButton>
                          ))}

                          {/* Add stream row */}
                          <ListItemButton
                            sx={{ pl: 8 }}
                            selected={selection.kind === 'new_stream' && selection.cameraId === camera.cameraId}
                            onClick={() => void setSelectionSafe({ kind: 'new_stream', cameraId: camera.cameraId })}
                          >
                            <ListItemText
                              primary={t('cameraControl.addStream')}
                              primaryTypographyProps={{ color: 'primary.main' }}
                            />
                          </ListItemButton>
                        </Box>
                      ))}
                    </Box>
                  ))}
                </List>
              </Box>

              {/* Detail panel */}
              <Box flex={1} sx={{ minWidth: 0 }}>
                {selection.kind === 'camera' && (
                  <CameraSetupPanel
                    ref={detailRef}
                    key={selection.cameraId}
                    cameraId={selection.cameraId}
                    serverId={selection.serverId}
                    onSaved={refresh}
                    onRemoved={async () => {
                      setSelection({ kind: 'none' })
                      await refresh()
                    }}
                  />
                )}

                {selection.kind === 'new_camera' && (
                  <CameraSetupPanel
                    ref={detailRef}
                    key={`new@${selection.serverId}`}
                    cameraId=""
                    serverId={selection.serverId}
                    onSaved={refresh}
                    onRemoved={refresh}
                  />
                )}

                {selection.kind === 'stream' && (
                  <StreamSetupPanel
                    ref={detailRef}
                    key={`${selection.cameraId}/${selection.streamName}`}
                    cameraId={selection.cameraId}
                    streamName={selection.streamName}
                    onSaved={refresh}
                    onRemoved={async () => {
                      setSelection({ kind: 'camera', cameraId: selection.cameraId, serverId: camerasMap[selection.cameraId]?.serverId ?? '' })
                      await refresh()
                    }}
                  />
                )}

                {selection.kind === 'new_stream' && (
                  <StreamSetupPanel
                    ref={detailRef}
                    key={`${selection.cameraId}/new`}
                    cameraId={selection.cameraId}
                    streamName={null}
                    onSaved={refresh}
                    onRemoved={refresh}
                  />
                )}

                {selection.kind === 'none' && (
                  <Typography color="text.secondary" sx={{ pt: 1 }}>
                    {t('cameraControl.selectHint')}
                  </Typography>
                )}
              </Box>
            </Stack>

            <Button variant="outlined" onClick={refresh} sx={{ mt: 2 }} size="small">
              {t('common.refresh')}
            </Button>
          </CardContent>
        </Card>
      </Collapse>
    </Box>
  )
}
