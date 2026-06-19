import { useCallback, useEffect, useImperativeHandle, useRef, useState } from 'react'
import { Box, Button, Chip, CircularProgress, FormControlLabel, IconButton, InputAdornment, MenuItem, Stack, Switch, TextField, Tooltip, Typography } from '@mui/material'
import ContentCopyIcon from '@mui/icons-material/ContentCopy'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { addCamera, fetchCameraState } from '../store/liveSlice'
import type { CameraStreamDto, RecordingProfileDto } from '../dto/dtos'
import CameraStream from './CameraStream'

export type StreamSetupHandle = { applyIfDirty: () => Promise<boolean> }

type Props = {
  ref?: React.Ref<StreamSetupHandle>
  cameraId: string
  streamName: string | null  // null = new stream
  onSaved: () => Promise<void>
  onRemoved: () => Promise<void>
  onAddCameraOnServer: (serverId: string) => void
}

type ProfileInfo = { token: string; name: string; width: number; height: number; encoding: string }

export default function StreamSetupPanel({ ref, cameraId, streamName, onSaved, onRemoved, onAddCameraOnServer }: Props) {
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
  const [selectedProfileId, setSelectedProfileId] = useState<string>(existingStream?.recordingProfile?.id ?? '')

  const [recordingProfiles, setRecordingProfiles] = useState<RecordingProfileDto[]>([])
  const [profilesLoading, setProfilesLoading] = useState(false)

  const [availableOnvifProfiles, setAvailableOnvifProfiles] = useState<ProfileInfo[]>([])
  const [onvifLoading, setOnvifLoading] = useState(false)
  const [onvifError, setOnvifError] = useState<string | null>(null)

  const [resolvedRtspUrl, setResolvedRtspUrl] = useState<string | null>(null)
  const [rtspUrlLoading, setRtspUrlLoading] = useState(false)
  const [rtspUrlError, setRtspUrlError] = useState<string | null>(null)
  const [rtspUrlCopied, setRtspUrlCopied] = useState(false)

  // Snapshot of the saved state — used for dirty detection and Cancel
  const snap = useRef({
    path: existingStream?.path ?? '',
    profileToken: existingStream?.profileToken ?? '',
    webRtc: existingStream?.webRtc ?? false,
    disabled: existingStream?.disabled === true,
    selectedProfileId: existingStream?.recordingProfile?.id ?? '',
  })

  const dirty = !isNew && (
    path !== snap.current.path
    || profileToken !== snap.current.profileToken
    || webRtc !== snap.current.webRtc
    || disabled !== snap.current.disabled
    || selectedProfileId !== snap.current.selectedProfileId
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

  // Warn browser on close when dirty
  useEffect(() => {
    if (!dirty) return
    const handler = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = '' }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [dirty])

  // applyInternalRef always points to the latest apply logic so useImperativeHandle
  // can be called unconditionally (before the camera null-guard below).
  const applyInternalRef = useRef<() => Promise<boolean>>(async () => true)
  useImperativeHandle(ref, () => ({ applyIfDirty: () => applyInternalRef.current() }), [])

  if (!camera) return null

  const applyInternal = async (): Promise<boolean> => {
    if (!dirty) return true
    const profile = recordingProfiles.find((p) => p.id === selectedProfileId)
    const updated: CameraStreamDto = {
      streamName: streamName!,
      path,
      profileToken,
      webRtc,
      disabled: disabled || undefined,
      recordingProfile: profile,
    }
    const others = (camera.streams ?? []).filter((s) => s.streamName !== updated.streamName)
    const result = await dispatch(addCamera({ ...camera, streams: [...others, updated] }))
    if (!addCamera.rejected.match(result)) {
      snap.current = { path, profileToken, webRtc, disabled, selectedProfileId }
      await dispatch(fetchCameraState(cameraId))
      return true
    }
    return false
  }
  // Keep the ref up to date on every render so the handle always calls the latest closure
  applyInternalRef.current = applyInternal

  const handleApply = async (e: React.FormEvent) => {
    e.preventDefault()
    if (isNew) {
      const profile = recordingProfiles.find((p) => p.id === selectedProfileId)
      const updated: CameraStreamDto = { streamName: name, path, profileToken, webRtc, disabled: disabled || undefined, recordingProfile: profile }
      const others = (camera.streams ?? []).filter((s) => s.streamName !== updated.streamName)
      const result = await dispatch(addCamera({ ...camera, streams: [...others, updated] }))
      if (!addCamera.rejected.match(result)) await onSaved()
    } else {
      const ok = await applyInternal()
      if (ok) await onSaved()
    }
  }

  const handleCancel = () => {
    setPath(snap.current.path)
    setProfileToken(snap.current.profileToken)
    setWebRtc(snap.current.webRtc)
    setDisabled(snap.current.disabled)
    setSelectedProfileId(snap.current.selectedProfileId)
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
    <Stack component="form" direction="row" spacing={3} alignItems="flex-start" onSubmit={handleApply}>
      {/* Left: form fields */}
      <Stack spacing={2} sx={{ flex: 1, minWidth: 0 }}>
        <Typography variant="subtitle2" color="text.secondary">
          {isNew ? t('stream.newStream', { camera: cameraId }) : t('stream.existing', { name: streamName })}
        </Typography>

        <TextField
          label={t('stream.streamName')}
          value={name}
          onChange={(e) => setName(e.target.value)}
          disabled={!isNew}
          required
          size="small"
          helperText={t('stream.streamNameHelper')}
        />

        <TextField
          label={t('stream.path')}
          value={path}
          onChange={(e) => setPath(e.target.value)}
          size="small"
          fullWidth
          disabled={Boolean(profileToken)}
          helperText={profileToken ? t('stream.pathHelperTokenSet') : t('stream.pathHelper')}
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
                          void navigator.clipboard.writeText(resolvedRtspUrl).then(() => {
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

        <TextField
          select
          label={t('stream.recordingProfile')}
          value={selectedProfileId}
          onChange={(e) => setSelectedProfileId(e.target.value)}
          size="small"
          sx={{ maxWidth: 240 }}
          disabled={profilesLoading}
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

        <FormControlLabel
          control={<Switch checked={disabled} onChange={(e) => setDisabled(e.target.checked)} color="warning" />}
          label={t('stream.disabled')}
        />

        <Stack direction="row" spacing={1}>
          <Button type="submit" variant="outlined" size="small" disabled={!isNew && !dirty}>
            {isNew ? t('stream.addStream') : t('common.apply')}
          </Button>
          {!isNew && (
            <Button variant="text" size="small" onClick={handleCancel} disabled={!dirty}>
              {t('common.cancel')}
            </Button>
          )}
          {!isNew && (
            <Button color="error" variant="outlined" size="small" onClick={handleRemove}>
              {t('stream.deleteStream')}
            </Button>
          )}
          {camera.serverId && (
            <Button
              variant="text"
              size="small"
              onClick={() => onAddCameraOnServer(camera.serverId!)}
              sx={{ ml: 'auto' }}
            >
              {t('cameraControl.addCameraHere', { server: camera.serverId })}
            </Button>
          )}
        </Stack>
      </Stack>

      {/* Right: live preview */}
      {showPreview && (
        <Box sx={{ width: 280, flexShrink: 0 }}>
          <Typography variant="caption" color="text.secondary" display="block" gutterBottom>
            {t('stream.livePreview')}
          </Typography>
          <CameraStream
            streamName={`${cameraId}/${streamName!}`}
            baseUrl={`${window.location.protocol}//${camera.mediaMtxWebrtcHost || window.location.hostname}:${camera.mediaMtxWebrtcPort ?? 8889}`}
          />
        </Box>
      )}
    </Stack>
  )
}
