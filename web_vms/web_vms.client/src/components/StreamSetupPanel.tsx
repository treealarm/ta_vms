import { useCallback, useEffect, useState } from 'react'
import { Button, Chip, CircularProgress, FormControlLabel, MenuItem, Stack, Switch, TextField, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { addCamera } from '../store/liveSlice'
import type { CameraStreamDto, RecordingProfileDto } from '../dto/dtos'

type Props = {
  cameraId: string
  streamName: string | null  // null = new stream
  onSaved: () => Promise<void>
  onRemoved: () => Promise<void>
}

type ProfileInfo = { token: string; name: string; width: number; height: number; encoding: string }

export default function StreamSetupPanel({ cameraId, streamName, onSaved, onRemoved }: Props) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const camera = useAppSelector((state) => state.live.cameraStatesById[cameraId])
  const existingStream = streamName != null
    ? camera?.streams?.find((s) => s.streamName === streamName)
    : undefined
  const isNew = streamName === null

  const [name, setName] = useState(streamName ?? '')
  const [rtspUrl, setRtspUrl] = useState(existingStream?.rtspUrl ?? '')
  const [profileToken, setProfileToken] = useState(existingStream?.profileToken ?? '')
  const [webRtc, setWebRtc] = useState(existingStream?.webRtc ?? false)
  const [selectedProfileId, setSelectedProfileId] = useState<string>(existingStream?.recordingProfile?.id ?? '')

  const [recordingProfiles, setRecordingProfiles] = useState<RecordingProfileDto[]>([])
  const [profilesLoading, setProfilesLoading] = useState(false)

  const [availableOnvifProfiles, setAvailableOnvifProfiles] = useState<ProfileInfo[]>([])
  const [onvifLoading, setOnvifLoading] = useState(false)
  const [onvifError, setOnvifError] = useState<string | null>(null)

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

  if (!camera) return null

  const handleSave = async (e: React.FormEvent) => {
    e.preventDefault()
    const profile = recordingProfiles.find((p) => p.id === selectedProfileId)
    const updated: CameraStreamDto = {
      streamName: isNew ? name : streamName!,
      rtspUrl,
      profileToken,
      webRtc,
      recordingProfile: profile,
    }
    const others = (camera.streams ?? []).filter((s) => s.streamName !== updated.streamName)
    const result = await dispatch(addCamera({ ...camera, streams: [...others, updated] }))
    if (!addCamera.rejected.match(result)) await onSaved()
  }

  const handleRemove = async () => {
    const filtered = (camera.streams ?? []).filter((s) => s.streamName !== streamName)
    const result = await dispatch(addCamera({ ...camera, streams: filtered }))
    if (!addCamera.rejected.match(result)) await onRemoved()
  }

  return (
    <Stack component="form" spacing={2} onSubmit={handleSave}>
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
        label={t('stream.rtspUrl')}
        value={rtspUrl}
        onChange={(e) => setRtspUrl(e.target.value)}
        size="small"
        fullWidth
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
                  onClick={() => setProfileToken(p.token)}
                />
              )
            })}
          </Stack>
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

      <Stack direction="row" spacing={1}>
        <Button type="submit" variant="outlined" size="small">
          {isNew ? t('stream.addStream') : t('common.save')}
        </Button>
        {!isNew && (
          <Button color="error" variant="outlined" size="small" onClick={handleRemove}>
            {t('stream.deleteStream')}
          </Button>
        )}
      </Stack>
    </Stack>
  )
}
