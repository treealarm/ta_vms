import { useImperativeHandle, useMemo, useRef, useState } from 'react'
import {
  Box,
  Button,
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

export type CameraControlHandle = { applyIfDirty: () => Promise<boolean> }

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

  const setSelectionSafe = async (newSel: Selection) => {
    if (detailRef.current) await detailRef.current.applyIfDirty()
    setSelection(newSel)
  }

  useImperativeHandle(ref, () => ({
    applyIfDirty: () => detailRef.current ? detailRef.current.applyIfDirty() : Promise.resolve(true)
  }))

  return (
    <Box sx={{ height: '100%', display: 'flex', flexDirection: 'column', gap: 1 }}>
      <Box sx={{ flex: 1, minHeight: 0, overflow: 'auto' }}>
      <Stack
        direction="row"
        spacing={2}
        sx={{ height: '100%', minWidth: 520 }}
        alignItems="stretch"
      >
        {/* Tree */}
        <Box sx={{
          width: 200,
          flexShrink: 0,
          border: 1,
          borderColor: 'divider',
          borderRadius: 1,
          overflowY: 'auto',
        }}>
          <List dense disablePadding>
            {serversTree.map((server) => (
              <Box key={server.serverId}>
                <ListItemButton
                  selected={selection.kind === 'new_camera' && selection.serverId === server.serverId}
                  onClick={() => void setSelectionSafe({ kind: 'new_camera', serverId: server.serverId })}
                >
                  <ListItemText
                    primary={server.serverId}
                    secondary={t('cameraControl.newCamera')}
                    primaryTypographyProps={{ fontWeight: 600, noWrap: true }}
                    secondaryTypographyProps={{ noWrap: true }}
                  />
                </ListItemButton>

                {server.cameras.map((camera) => (
                  <Box key={camera.cameraId}>
                    <ListItemButton
                      sx={{ pl: 3, opacity: camera.disabled === true ? 0.45 : 1 }}
                      selected={selection.kind === 'camera' && selection.cameraId === camera.cameraId}
                      onClick={() =>
                        void setSelectionSafe({ kind: 'camera', cameraId: camera.cameraId, serverId: server.serverId })
                      }
                    >
                      <ListItemText
                        primary={camera.cameraId}
                        primaryTypographyProps={{ noWrap: true }}
                      />
                    </ListItemButton>

                    {(camera.streams ?? []).map((stream) => (
                      <ListItemButton
                        key={stream.streamName}
                        sx={{ pl: 5, opacity: stream.disabled === true ? 0.45 : 1 }}
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
                          primaryTypographyProps={{ noWrap: true }}
                          secondaryTypographyProps={{ noWrap: true }}
                        />
                      </ListItemButton>
                    ))}

                    <ListItemButton
                      sx={{ pl: 5 }}
                      selected={selection.kind === 'new_stream' && selection.cameraId === camera.cameraId}
                      onClick={() => void setSelectionSafe({ kind: 'new_stream', cameraId: camera.cameraId })}
                    >
                      <ListItemText
                        primary={t('cameraControl.addStream')}
                        primaryTypographyProps={{ color: 'primary.main', noWrap: true }}
                      />
                    </ListItemButton>
                  </Box>
                ))}
              </Box>
            ))}
          </List>
        </Box>

        {/* Detail panel */}
        <Box sx={{ flex: 1, minWidth: 0, minHeight: 0, overflow: 'auto' }}>
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
      </Box>

      <Button variant="outlined" onClick={refresh} size="small" sx={{ alignSelf: 'flex-start' }}>
        {t('common.refresh')}
      </Button>
    </Box>
  )
}
