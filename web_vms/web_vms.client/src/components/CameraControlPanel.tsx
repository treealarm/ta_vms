import { useImperativeHandle, useMemo, useRef, useState } from 'react'
import { Box, Button, List, Menu, MenuItem, Stack, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { loadCameras, loadServers } from '../store/liveSlice'
import { loadMediaMtxServers } from '../store/mediaMtxSlice'
import CameraSetupPanel from './CameraSetupPanel'
import OnvifDiscoveryDialog from './OnvifDiscoveryDialog'
import StreamSetupPanel from './StreamSetupPanel'
import MasterDetailLayout from './settings/MasterDetailLayout'
import SettingsListItem from './settings/SettingsListItem'
import type { ApplyIfDirtyHandle } from './settings/useDirtyGuard'

type Selection =
  | { kind: 'none' }
  | { kind: 'server'; serverId: string }
  | { kind: 'camera'; cameraId: string; serverId: string }
  | { kind: 'new_camera'; serverId: string }
  | { kind: 'stream'; cameraId: string; streamName: string }
  | { kind: 'new_stream'; cameraId: string }

export default function CameraControlPanel({ ref }: { ref?: React.Ref<ApplyIfDirtyHandle> }) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const servers = useAppSelector((state) => state.live.servers)
  const camerasMap = useAppSelector((state) => state.live.cameraStatesById)
  const [selection, setSelection] = useState<Selection>({ kind: 'none' })
  const [discoveryOpen, setDiscoveryOpen] = useState(false)
  const [addMenuAnchor, setAddMenuAnchor] = useState<HTMLElement | null>(null)

  const detailRef = useRef<ApplyIfDirtyHandle>(null)

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

  // Servers/MediaMTX servers don't change when the discovery wizard adds a camera —
  // only the camera list needs to be re-fetched.
  const refreshCameras = async () => {
    await dispatch(loadCameras())
  }

  // Serializes guarded switches so a rapid second click cannot start a
  // concurrent applyIfDirty save for the same dirty form
  const switching = useRef(false)

  const setSelectionSafe = async (newSel: Selection) => {
    if (switching.current) return
    switching.current = true
    try {
      // Block the switch when saving the dirty form failed — the error stays visible
      if (detailRef.current && !(await detailRef.current.applyIfDirty())) return
      setSelection(newSel)
    } finally {
      switching.current = false
    }
  }

  // Camera in context for the "+" menu (a stream can be added to it)
  const contextCameraId =
    selection.kind === 'camera' || selection.kind === 'stream' || selection.kind === 'new_stream'
      ? selection.cameraId
      : undefined
  const contextServerId = selection.kind === 'server' ? selection.serverId : undefined

  const handleAddClick = (e: React.MouseEvent<HTMLElement>) => {
    // Single possible action — skip the menu
    if (!contextCameraId && servers.length === 1) {
      void setSelectionSafe({ kind: 'new_camera', serverId: servers[0].serverId })
      return
    }
    setAddMenuAnchor(e.currentTarget)
  }

  const pickAdd = (sel: Selection) => {
    setAddMenuAnchor(null)
    void setSelectionSafe(sel)
  }

  // The camera whose streams are currently shown expanded in the tree — collapses
  // every other camera so the list doesn't grow tall with every camera's streams.
  const expandedCameraId =
    selection.kind === 'camera' || selection.kind === 'stream' || selection.kind === 'new_stream'
      ? selection.cameraId
      : undefined

  useImperativeHandle(ref, () => ({
    applyIfDirty: () => detailRef.current ? detailRef.current.applyIfDirty() : Promise.resolve(true)
  }))

  const tree = (
    <List dense disablePadding>
      {serversTree.map((server) => (
        <Box key={server.serverId}>
          <SettingsListItem
            selected={selection.kind === 'server' && selection.serverId === server.serverId}
            onClick={() => void setSelectionSafe({ kind: 'server', serverId: server.serverId })}
            primary={<Box component="span" sx={{ fontWeight: 600 }}>{server.serverId}</Box>}
          />

          {server.cameras.map((camera) => (
            <Box key={camera.cameraId}>
              <SettingsListItem
                depth={1}
                dimmed={camera.disabled === true}
                selected={selection.kind === 'camera' && selection.cameraId === camera.cameraId}
                onClick={() =>
                  void setSelectionSafe({ kind: 'camera', cameraId: camera.cameraId, serverId: server.serverId })
                }
                primary={camera.name || camera.cameraId}
                secondary={camera.onvifConn?.ip}
              />

              {camera.cameraId === expandedCameraId && (camera.streams ?? []).map((stream) => (
                <SettingsListItem
                  key={stream.streamName}
                  depth={2}
                  dimmed={stream.disabled === true}
                  selected={
                    selection.kind === 'stream' &&
                    selection.cameraId === camera.cameraId &&
                    selection.streamName === stream.streamName
                  }
                  onClick={() =>
                    void setSelectionSafe({ kind: 'stream', cameraId: camera.cameraId, streamName: stream.streamName })
                  }
                  primary={stream.streamName}
                  secondary={stream.path || stream.profileToken || undefined}
                />
              ))}
            </Box>
          ))}
        </Box>
      ))}
    </List>
  )

  return (
    <MasterDetailLayout
      listTitle={t('cameraControl.listTitle')}
      listWidth={240}
      onAdd={handleAddClick}
      list={tree}
      footer={
        <Stack direction="row" spacing={1}>
          <Button variant="outlined" onClick={refresh} size="small">
            {t('common.refresh')}
          </Button>
          <Button variant="outlined" onClick={() => setDiscoveryOpen(true)} size="small">
            {t('cameraControl.discoverDevices')}
          </Button>
        </Stack>
      }
    >
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
          onSaved={async (createdId) => {
            await refresh()
            // Jump to the created camera — staying in create mode would re-upsert
            // the same generated id on a second Add click
            if (createdId) setSelection({ kind: 'camera', cameraId: createdId, serverId: selection.serverId })
          }}
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
          onSaved={async (createdName) => {
            await refresh()
            if (createdName) setSelection({ kind: 'stream', cameraId: selection.cameraId, streamName: createdName })
          }}
          onRemoved={refresh}
        />
      )}
      {(selection.kind === 'none' || selection.kind === 'server') && (
        <Typography color="text.secondary" sx={{ p: 2 }}>
          {selection.kind === 'server' ? t('cameraControl.serverHint') : t('cameraControl.selectHint')}
        </Typography>
      )}

      <Menu anchorEl={addMenuAnchor} open={Boolean(addMenuAnchor)} onClose={() => setAddMenuAnchor(null)}>
        {contextCameraId && (
          <MenuItem onClick={() => pickAdd({ kind: 'new_stream', cameraId: contextCameraId })}>
            {t('cameraControl.newStreamOn', { camera: camerasMap[contextCameraId]?.name || contextCameraId })}
          </MenuItem>
        )}
        {servers.map((s) => (
          <MenuItem
            key={s.serverId}
            selected={s.serverId === contextServerId}
            onClick={() => pickAdd({ kind: 'new_camera', serverId: s.serverId })}
          >
            {t('cameraControl.newCameraOn', { server: s.serverId })}
          </MenuItem>
        ))}
      </Menu>

      <OnvifDiscoveryDialog
        open={discoveryOpen}
        onClose={() => setDiscoveryOpen(false)}
        onAdded={refreshCameras}
      />
    </MasterDetailLayout>
  )
}
