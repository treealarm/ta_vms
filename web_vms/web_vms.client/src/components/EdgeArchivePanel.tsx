import { useEffect, useState } from 'react'
import { Alert, Box, Chip, CircularProgress, Stack, Tab, Tabs, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'

import { useAppDispatch, useAppSelector } from '../store'
import { fetchEdgeCapabilities, fetchEdgeSummary, selectEdgeCamera } from '../store/edgeArchiveSlice'
import MasterDetailLayout from './settings/MasterDetailLayout'
import SettingsListItem from './settings/SettingsListItem'
import DetailToolbar from './settings/DetailToolbar'
import EdgeArchiveBrowsePanel from './EdgeArchiveBrowsePanel'
import EdgeCameraRecordingPanel from './EdgeCameraRecordingPanel'
import EdgeStoragePanel from './EdgeStoragePanel'
import type { EdgeArchiveSection } from './edgeArchiveModel'

const SECTIONS: EdgeArchiveSection[] = ['browse', 'recording', 'storage']

/**
 * The camera's own archive (SD card) over ONVIF Profile G: browse and import what it holds, manage
 * its on-board recording, inspect and format its storage.
 */
export default function EdgeArchivePanel() {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const cameras = useAppSelector((s) => Object.values(s.live.cameraStatesById))
  const capabilities = useAppSelector((s) => s.edgeArchive.capabilities)
  const summary = useAppSelector((s) => s.edgeArchive.summary)
  const status = useAppSelector((s) => s.edgeArchive.status)

  // Only ONVIF-reachable cameras can have an edge archive at all; the rest would just show errors.
  const onvifCameras = cameras.filter((c) => c.onvifConn?.ip)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [section, setSection] = useState<EdgeArchiveSection>('browse')

  const selected = selectedId ? onvifCameras.find((c) => c.cameraId === selectedId) ?? null : null

  // Shown only when it matters: a second or two is measurement noise, but hours mean the card is
  // indexed in a wholly different scale, and "unknown" means imports are refused.
  const clockOffsetLabel = (() => {
    if (!summary?.supported) return null
    const ms = summary.clockOffsetMs
    if (ms == null) return t('edge.clockUnknown')
    if (Math.abs(ms) < 5_000) return null
    const sign = ms > 0 ? '+' : '−'
    const abs = Math.abs(ms)
    const h = Math.floor(abs / 3600_000)
    const m = Math.floor((abs % 3600_000) / 60_000)
    const sec = Math.round((abs % 60_000) / 1000)
    const span = h > 0 ? `${h}h ${m}m` : m > 0 ? `${m}m ${sec}s` : `${sec}s`
    return t('edge.clockOffset', { offset: `${sign}${span}` })
  })()

  useEffect(() => {
    // Sets the camera the slice accepts answers for, so a slow response for the previous one is
    // dropped instead of overwriting this camera's state.
    dispatch(selectEdgeCamera(selectedId))
    if (!selectedId) return
    void dispatch(fetchEdgeCapabilities(selectedId))
    void dispatch(fetchEdgeSummary(selectedId))
  }, [dispatch, selectedId])

  if (onvifCameras.length === 0) {
    return (
      <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
        {t('edge.noOnvifCameras')}
      </Typography>
    )
  }

  // null means the probe has not answered yet, which is not the same as "the camera cannot do it" —
  // treating it as unsupported flashed a wrong verdict on every camera switch.
  const probing = selected !== null && capabilities === null && !status

  return (
    <MasterDetailLayout
      listTitle={t('edge.listTitle')}
      list={
        <Box>
          {onvifCameras.map((cam) => (
            <SettingsListItem
              key={cam.cameraId}
              selected={cam.cameraId === selectedId}
              onClick={() => setSelectedId(cam.cameraId)}
              primary={cam.name || cam.cameraId}
              secondary={`${cam.onvifConn?.ip ?? ''}:${cam.onvifConn?.port ?? ''}`}
            />
          ))}
        </Box>
      }
    >
      {!selected ? (
        <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
          {t('edge.selectCamera')}
        </Typography>
      ) : (
        <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
          <DetailToolbar
            title={selected.name || selected.cameraId}
            subtitle={`${selected.onvifConn?.ip}:${selected.onvifConn?.port}`}
            dirty={false}
            hideApply
          />

          {/* Three states, deliberately distinct: still asking the camera, it answered "no", or it
              answered "yes". A camera without Profile G gets the reason, not three empty tabs. */}
          {probing ? (
            <Stack direction="row" spacing={1} alignItems="center" sx={{ p: 2 }}>
              <CircularProgress size={18} />
              <Typography variant="body2" color="text.secondary">{t('edge.probing')}</Typography>
            </Stack>
          ) : capabilities === null ? (
            <Box sx={{ p: 2 }}>
              <Alert severity="error">{status || t('edge.probeFailed')}</Alert>
            </Box>
          ) : !capabilities.supported ? (
            <Box sx={{ p: 2 }}>
              <Alert severity="info">
                {capabilities.message || t('edge.notSupported')}
              </Alert>
            </Box>
          ) : (
            <>
              <Stack direction="row" spacing={1} sx={{ px: 2, pt: 1 }} alignItems="center">
                {summary?.supported && (
                  <Chip
                    size="small"
                    variant="outlined"
                    label={t('edge.recordingsOnBoard', { count: summary.numberRecordings })}
                  />
                )}
                {capabilities?.maxRecordings ? (
                  <Chip size="small" variant="outlined" label={t('edge.maxRecordings', { count: capabilities.maxRecordings })} />
                ) : null}
                {/* The card is indexed in the camera's clock. A large offset is not an error — the
                    times shown here are already corrected — but it explains why they differ from
                    what the camera's own web UI reports, and an unknown one blocks importing. */}
                {summary?.supported && clockOffsetLabel && (
                  <Chip
                    size="small"
                    variant="outlined"
                    color={summary.clockOffsetMs == null ? 'error' : 'warning'}
                    label={clockOffsetLabel}
                  />
                )}
              </Stack>
              <Tabs
                value={SECTIONS.indexOf(section)}
                onChange={(_, v: number) => setSection(SECTIONS[v])}
                sx={{ px: 2, borderBottom: 1, borderColor: 'divider' }}
              >
                <Tab label={t('edge.tabBrowse')} />
                <Tab label={t('edge.tabRecording')} />
                <Tab label={t('edge.tabStorage')} />
              </Tabs>

              <Box sx={{ flex: 1, minHeight: 0 }}>
                {section === 'browse' && (
                  <EdgeArchiveBrowsePanel
                    key={selected.cameraId}
                    cameraId={selected.cameraId}
                    dataFrom={summary?.dataFrom ?? ''}
                    dataUntil={summary?.dataUntil ?? ''}
                  />
                )}
                {section === 'recording' && (
                  <EdgeCameraRecordingPanel key={selected.cameraId} cameraId={selected.cameraId} />
                )}
                {section === 'storage' && (
                  <EdgeStoragePanel key={selected.cameraId} cameraId={selected.cameraId} />
                )}
              </Box>
            </>
          )}
        </Box>
      )}
    </MasterDetailLayout>
  )
}
