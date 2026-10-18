import { useCallback, useEffect, useState } from 'react'
import { Box, Button, Chip, CircularProgress, FormControlLabel, IconButton, InputAdornment, MenuItem, Stack, Switch, TextField, Tooltip, Typography } from '@mui/material'
import ContentCopyIcon from '@mui/icons-material/ContentCopy'
import FolderOpenIcon from '@mui/icons-material/FolderOpen'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { addCamera, fetchCameraState } from '../store/liveSlice'
import type { CameraStreamDto, RecordingProfileDto } from '../dto/dtos'
import { isRecordable } from '../dto/dtos'
import CameraStream from './CameraStream'
import StoragePathBrowserDialog from './StoragePathBrowserDialog'
import DetailToolbar from './settings/DetailToolbar'
import { useDirtyGuard } from './settings/useDirtyGuard'
import type { ApplyIfDirtyHandle } from './settings/useDirtyGuard'
import { copyToClipboard } from '../utils/copyToClipboard'

type Props = {
  ref?: React.Ref<ApplyIfDirtyHandle>
  cameraId: string
  streamName: string | null  // null = new stream
  onSaved: (createdStreamName?: string) => Promise<void>
  onRemoved: () => Promise<void>
}

type ProfileInfo = { token: string; name: string; width: number; height: number; encoding: string }

/**
 * The directory part of a configured path, or undefined when there is nothing to go on.
 *
 * Deliberately textual: this is a path on the recording node, in that machine's style, and the
 * browser has no business parsing it as its own. Both separators are honoured because the node may
 * be either, and a value that is already a directory is returned unchanged -- browsing opens it
 * rather than its parent.
 */
function folderOf(value: string): string | undefined {
  const trimmed = value.trim()
  if (!trimmed || trimmed.includes('://')) return undefined
  const cut = Math.max(trimmed.lastIndexOf('/'), trimmed.lastIndexOf('\\'))
  if (cut <= 0) return undefined
  return trimmed.slice(0, cut)
}

export default function StreamSetupPanel({ ref, cameraId, streamName, onSaved, onRemoved }: Props) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const camera = useAppSelector((state) => state.live.cameraStatesById[cameraId])
  const existingStream = streamName != null
    ? camera?.streams?.find((s) => s.streamName === streamName)
    : undefined
  const isNew = streamName === null

  const [name, setName] = useState(streamName ?? '')
  const [path, setPath] = useState(existingStream?.path ?? '')
  const [profileToken, setProfileToken] = useState(existingStream?.profileToken ?? '')
  const [webRtc, setWebRtc] = useState(existingStream?.webRtc ?? isNew)
  const [disabled, setDisabled] = useState(existingStream?.disabled === true)
  const [recordable, setRecordable] = useState(existingStream ? isRecordable(existingStream) : true)
  const [selectedProfileId, setSelectedProfileId] = useState<string>(existingStream?.recordingProfile?.id ?? '')

  const [browsing, setBrowsing] = useState(false)

  const [recordingProfiles, setRecordingProfiles] = useState<RecordingProfileDto[]>([])
  const [profilesLoading, setProfilesLoading] = useState(false)

  const [availableOnvifProfiles, setAvailableOnvifProfiles] = useState<ProfileInfo[]>([])
  const [onvifLoading, setOnvifLoading] = useState(false)
  const [onvifError, setOnvifError] = useState<string | null>(null)

  const [resolvedRtspUrl, setResolvedRtspUrl] = useState<string | null>(null)
  const [rtspUrlLoading, setRtspUrlLoading] = useState(false)
  const [rtspUrlError, setRtspUrlError] = useState<string | null>(null)
  const [rtspUrlCopied, setRtspUrlCopied] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)

  // Snapshot of the saved state — used for dirty detection and Cancel
  const [snap, setSnap] = useState({
    path: existingStream?.path ?? '',
    profileToken: existingStream?.profileToken ?? '',
    webRtc: existingStream?.webRtc ?? false,
    disabled: existingStream?.disabled === true,
    recordable: existingStream ? isRecordable(existingStream) : true,
    selectedProfileId: existingStream?.recordingProfile?.id ?? '',
  })

  // The declared resolution of the ONVIF profile this stream is bound to, when the profile list has
  // been fetched. Zero means "not known here", and VmsCfg then keeps whatever it already has — so
  // saving the panel without ever opening the profile list cannot erase it.
  const declared = availableOnvifProfiles.find((p) => p.token === profileToken)
  const declaredWidth = declared?.width ?? existingStream?.declaredWidth ?? 0
  const declaredHeight = declared?.height ?? existingStream?.declaredHeight ?? 0

  const dirty = !isNew && (
    path !== snap.path
    || profileToken !== snap.profileToken
    || webRtc !== snap.webRtc
    || disabled !== snap.disabled
    || recordable !== snap.recordable
    || selectedProfileId !== snap.selectedProfileId
  )

  const fetchOnvifProfiles = useCallback(async () => {
    setOnvifLoading(true)
    setOnvifError(null)
    setAvailableOnvifProfiles([])
    try {
      const response = await fetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/profiles`)
      if (!response.ok) {
        setOnvifError((await response.text()) || `Error ${response.status}`)
        return
      }
      const profiles = await response.json() as ProfileInfo[]
      setAvailableOnvifProfiles(profiles)
      if (profiles.length === 0) setOnvifError('No profiles found on this camera')
    } catch {
      setOnvifError('Failed to reach the camera')
    } finally {
      setOnvifLoading(false)
    }
  }, [cameraId])

  const fetchResolvedRtspUrl = useCallback(async () => {
    setRtspUrlLoading(true)
    setRtspUrlError(null)
    setResolvedRtspUrl(null)
    try {
      const qs = profileToken ? `?profileToken=${encodeURIComponent(profileToken)}` : ''
      const response = await fetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/stream-uri${qs}`)
      if (!response.ok) {
        setRtspUrlError((await response.text()) || `Error ${response.status}`)
        return
      }
      const data = await response.json() as { rtspUrl: string }
      if (data.rtspUrl) setResolvedRtspUrl(data.rtspUrl)
      else setRtspUrlError('Camera did not return a stream URL')
    } catch {
      setRtspUrlError('Failed to reach the camera')
    } finally {
      setRtspUrlLoading(false)
    }
  }, [cameraId, profileToken])

  // The resolved URL is only valid for the profile it was fetched for.
  useEffect(() => {
    setResolvedRtspUrl(null)
    setRtspUrlError(null)
  }, [profileToken])

  useEffect(() => {
    setProfilesLoading(true)
    fetch('/api/recording-profiles')
      .then((r) => r.json() as Promise<RecordingProfileDto[]>)
      .then(setRecordingProfiles)
      .catch(() => {})
      .finally(() => setProfilesLoading(false))
  }, [])

  useEffect(() => {
    void fetchOnvifProfiles()
  }, [fetchOnvifProfiles])

  // The recording profile object for the current selection. Falls back to the
  // stream's already-assigned profile while the /api/recording-profiles fetch
  // is still in flight — otherwise a quick Apply would silently wipe it.
  const resolveProfile = () =>
    recordingProfiles.find((p) => p.id === selectedProfileId)
      ?? (selectedProfileId && selectedProfileId === existingStream?.recordingProfile?.id
        ? existingStream?.recordingProfile
        : undefined)

  // Defined before the camera null-guard so the dirty guard can be registered
  // unconditionally; bails out gracefully when the camera is gone.
  const applyInternal = async (): Promise<boolean> => {
    if (!camera) return true
    if (isNew) {
      const newName = name.trim()
      if (!newName) return true  // nothing worth saving — allow discard
      if ((camera.streams ?? []).some((s) => s.streamName === newName)) {
        setSaveError(t('stream.duplicateName'))
        return false
      }
      const updated: CameraStreamDto = {
        streamName: newName,
        path,
        profileToken,
        webRtc,
        disabled: disabled || undefined,
        recordable,
        declaredWidth,
        declaredHeight,
        recordingProfile: resolveProfile(),
      }
      const result = await dispatch(addCamera({ ...camera, streams: [...(camera.streams ?? []), updated] }))
      if (addCamera.rejected.match(result)) return false
      await onSaved(newName)
      return true
    }
    if (!dirty) return true
    const updated: CameraStreamDto = {
      streamName: streamName!,
      path,
      profileToken,
      webRtc,
      disabled: disabled || undefined,
      recordable,
      declaredWidth,
      declaredHeight,
      recordingProfile: resolveProfile(),
    }
    const others = (camera.streams ?? []).filter((s) => s.streamName !== updated.streamName)
    const result = await dispatch(addCamera({ ...camera, streams: [...others, updated] }))
    if (!addCamera.rejected.match(result)) {
      setSnap({ path, profileToken, webRtc, disabled, recordable, selectedProfileId })
      await dispatch(fetchCameraState(cameraId))
      return true
    }
    return false
  }

  // For a new form "dirty" means "complete enough to save" — navigation then
  // auto-creates it instead of silently discarding the entered data
  useDirtyGuard(ref, isNew ? Boolean(name.trim()) : dirty, applyInternal)

  if (!camera) return null

  const handleApply = async (e: React.FormEvent) => {
    e.preventDefault()
    setSaveError(null)
    const ok = await applyInternal()
    if (ok && !isNew) await onSaved()
  }

  const handleCancel = () => {
    setPath(snap.path)
    setProfileToken(snap.profileToken)
    setWebRtc(snap.webRtc)
    setRecordable(snap.recordable)
    setDisabled(snap.disabled)
    setSelectedProfileId(snap.selectedProfileId)
  }

  const handleRemove = async () => {
    const filtered = (camera.streams ?? []).filter((s) => s.streamName !== streamName)
    // Clear legacy path when all streams removed — otherwise ToSetupProto re-creates
    // a synthetic "main" stream from the top-level path field.
    const payload = filtered.length === 0 ? { ...camera, streams: filtered, path: '' } : { ...camera, streams: filtered }
    const result = await dispatch(addCamera(payload))
    if (!addCamera.rejected.match(result)) await onRemoved()
  }

  const showPreview = !isNew && webRtc

  return (
    <Box component="form" onSubmit={(e) => void handleApply(e)} sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      <DetailToolbar
        title={isNew ? t('stream.newStream', { camera: camera.name || cameraId }) : (streamName ?? '')}
        subtitle={isNew ? undefined : (camera.name || cameraId)}
        dirty={isNew ? Boolean(name.trim()) : dirty}
        applyLabel={isNew ? t('stream.addStream') : undefined}
        onCancel={isNew ? undefined : handleCancel}
        onDelete={isNew ? undefined : () => void handleRemove()}
        deleteLabel={t('stream.deleteStream')}
      />

      <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', p: 2 }}>
        <Stack direction="row" spacing={3} alignItems="flex-start">
          {/* Left: form fields */}
          <Stack spacing={2} sx={{ flex: 1, minWidth: 0, maxWidth: 480 }}>
            <TextField
              label={t('stream.streamName')}
              value={name}
              onChange={(e) => { setName(e.target.value); setSaveError(null) }}
              disabled={!isNew}
              required
              size="small"
              helperText={t('stream.streamNameHelper')}
            />

            {saveError && (
              <Typography variant="caption" color="error">{saveError}</Typography>
            )}

            <Stack direction="row" spacing={1} alignItems="flex-start">
              <TextField
                label={t('stream.path')}
                value={path}
                onChange={(e) => setPath(e.target.value)}
                size="small"
                fullWidth
                disabled={Boolean(profileToken)}
                helperText={profileToken ? t('stream.pathHelperTokenSet') : t('stream.pathHelper')}
              />
              {/* Only for a camera with no device behind it: with an IP the path is an RTSP tail,
                  and browsing a disk for one would offer a path that becomes nonsense when the
                  camera's IP is prepended to it. */}
              {!profileToken && !camera?.onvifConn?.ip && (
                <Tooltip title={t('stream.browsePath')}>
                  <span>
                    <IconButton
                      size="small"
                      sx={{ mt: 0.5 }}
                      disabled={!camera?.serverId}
                      onClick={() => setBrowsing(true)}
                    >
                      <FolderOpenIcon fontSize="small" />
                    </IconButton>
                  </span>
                </Tooltip>
              )}
            </Stack>

            {/* The camera's own node, not a choice: the file has to be one video_srv can open,
                and it runs where the camera is pinned. Writability is not required either -- this
                folder is only ever read from. */}
            <StoragePathBrowserDialog
              open={browsing}
              onClose={() => setBrowsing(false)}
              onPick={setPath}
              serverId={camera?.serverId ?? undefined}
              requireWritable={false}
              includeFiles
              // Open where the current clip lives: changing which one a test camera plays means
              // reaching for its neighbour, and starting at the volume roots would make that a
              // walk back down the whole tree every time.
              initialPath={folderOf(path)}
              titleKey="stream.browseTitle"
            />

            <Stack spacing={1}>
              <Stack direction="row" spacing={1} alignItems="flex-start">
                <TextField
                  label={t('stream.onvifToken')}
                  value={profileToken}
                  onChange={(e) => setProfileToken(e.target.value)}
                  size="small"
                  fullWidth
                  helperText={t('stream.onvifTokenHelper')}
                />
                <Button
                  size="small"
                  variant="outlined"
                  onClick={() => void fetchOnvifProfiles()}
                  disabled={onvifLoading}
                  sx={{ minWidth: 88, flexShrink: 0, mt: '4px', height: 32 }}
                >
                  {onvifLoading ? <CircularProgress size={16} /> : t('common.refresh')}
                </Button>
              </Stack>

              {onvifError && (
                <Typography variant="caption" color="error">{onvifError}</Typography>
              )}

              {availableOnvifProfiles.length > 0 && (
                <Stack direction="row" spacing={0.5} flexWrap="wrap">
                  {availableOnvifProfiles.map((p) => {
                    const res = p.width && p.height ? ` ${p.width}×${p.height}` : ''
                    const enc = p.encoding ? ` ${p.encoding}` : ''
                    const label = p.name !== p.token ? `${p.name}${res}${enc}` : `${p.token}${res}${enc}`
                    return (
                      <Chip
                        key={p.token}
                        label={label}
                        size="small"
                        variant={profileToken === p.token ? 'filled' : 'outlined'}
                        color={profileToken === p.token ? 'primary' : 'default'}
                        onClick={() => {
                          setProfileToken(p.token)
                          if (isNew && !name) {
                            setName(p.token.toLowerCase())
                          }
                        }}
                      />
                    )
                  })}
                </Stack>
              )}

              <Stack direction="row" spacing={1} alignItems="flex-start">
                <Button
                  size="small"
                  variant="text"
                  onClick={() => void fetchResolvedRtspUrl()}
                  disabled={rtspUrlLoading}
                >
                  {rtspUrlLoading ? <CircularProgress size={16} /> : t('stream.showRtspUrl')}
                </Button>
              </Stack>

              {rtspUrlError && (
                <Typography variant="caption" color="error">{rtspUrlError}</Typography>
              )}

              {resolvedRtspUrl && (
                <TextField
                  value={resolvedRtspUrl}
                  size="small"
                  fullWidth
                  InputProps={{
                    readOnly: true,
                    endAdornment: (
                      <InputAdornment position="end">
                        <Tooltip title={rtspUrlCopied ? t('stream.rtspUrlCopied') : t('stream.copyRtspUrl')}>
                          <IconButton
                            size="small"
                            onClick={() => {
                              void copyToClipboard(resolvedRtspUrl).then((ok) => {
                                if (!ok) return
                                setRtspUrlCopied(true)
                                setTimeout(() => setRtspUrlCopied(false), 1500)
                              })
                            }}
                          >
                            <ContentCopyIcon fontSize="small" color={rtspUrlCopied ? 'success' : 'inherit'} />
                          </IconButton>
                        </Tooltip>
                      </InputAdornment>
                    ),
                  }}
                />
              )}
            </Stack>

            {/* Inert while the stream is not available for recording, but the chosen profile is
                deliberately left as it is: ticking the flag back on resumes recording with what was
                already configured, which is the whole reason the flag gates the effective mode
                instead of clearing the profile. */}
            <TextField
              select
              label={t('stream.recordingProfile')}
              value={selectedProfileId}
              onChange={(e) => setSelectedProfileId(e.target.value)}
              size="small"
              sx={{ maxWidth: 240 }}
              disabled={profilesLoading || !recordable}
              helperText={recordable ? undefined : t('stream.notRecordableHint')}
            >
              <MenuItem value="">{t('stream.noRecording')}</MenuItem>
              {recordingProfiles.map((p) => (
                <MenuItem key={p.id} value={p.id}>
                  {p.name}
                </MenuItem>
              ))}
            </TextField>

            <FormControlLabel
              control={<Switch checked={webRtc} onChange={(e) => setWebRtc(e.target.checked)} />}
              label={t('stream.webrtcLive')}
            />

            {/* Beside "Live video" on purpose: watching a stream and archiving it are independent
                choices, and putting the two switches together is what says so. */}
            <FormControlLabel
              control={<Switch checked={recordable} onChange={(e) => setRecordable(e.target.checked)} />}
              label={t('stream.recordable')}
            />

            <FormControlLabel
              control={<Switch checked={disabled} onChange={(e) => setDisabled(e.target.checked)} color="warning" />}
              label={t('stream.disabled')}
            />
          </Stack>

          {/* Right: live preview */}
          {showPreview && existingStream?.whepUrl && (
            <Box sx={{ width: 280, flexShrink: 0 }}>
              <Typography variant="caption" color="text.secondary" display="block" gutterBottom>
                {t('stream.livePreview')}
              </Typography>
              <CameraStream whepUrl={existingStream.whepUrl} stunUrl={camera?.effectiveWebrtcStunUrl} />
            </Box>
          )}
        </Stack>
      </Box>
    </Box>
  )
}
