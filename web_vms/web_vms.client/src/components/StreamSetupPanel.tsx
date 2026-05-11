import { useState } from 'react'
import { Button, Chip, CircularProgress, Stack, TextField, Typography } from '@mui/material'
import { useAppDispatch, useAppSelector } from '../store'
import { addCamera } from '../store/liveSlice'
import type { CameraStreamDto } from '../dto/dtos'

type Props = {
  cameraId: string
  streamName: string | null  // null = new stream
  onSaved: () => Promise<void>
  onRemoved: () => Promise<void>
}

export default function StreamSetupPanel({ cameraId, streamName, onSaved, onRemoved }: Props) {
  const dispatch = useAppDispatch()
  const camera = useAppSelector((state) => state.live.cameraStatesById[cameraId])
  const existingStream = streamName != null
    ? camera?.streams?.find((s) => s.streamName === streamName)
    : undefined
  const isNew = streamName === null

  const [name, setName] = useState(streamName ?? '')
  const [rtspUrl, setRtspUrl] = useState(existingStream?.rtspUrl ?? '')
  const [profileToken, setProfileToken] = useState(existingStream?.profileToken ?? '')
  type ProfileInfo = { token: string; name: string; width: number; height: number; encoding: string }
  const [availableProfiles, setAvailableProfiles] = useState<ProfileInfo[]>([])
  const [profilesLoading, setProfilesLoading] = useState(false)
  const [profilesError, setProfilesError] = useState<string | null>(null)

  if (!camera) return null

  const fetchProfiles = async () => {
    setProfilesLoading(true)
    setProfilesError(null)
    setAvailableProfiles([])
    try {
      const response = await fetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/profiles`)
      if (!response.ok) {
        const text = await response.text()
        setProfilesError(text || `Error ${response.status}`)
        return
      }
      const profiles = await response.json() as ProfileInfo[]
      setAvailableProfiles(profiles)
      if (profiles.length === 0) {
        setProfilesError('No profiles found on this camera')
      }
    } catch {
      setProfilesError('Failed to reach the camera')
    } finally {
      setProfilesLoading(false)
    }
  }

  const handleSave = async (e: React.FormEvent) => {
    e.preventDefault()
    const updated: CameraStreamDto = {
      streamName: isNew ? name : streamName!,
      rtspUrl,
      profileToken
    }
    const others = (camera.streams ?? []).filter((s) => s.streamName !== updated.streamName)
    const result = await dispatch(addCamera({ ...camera, streams: [...others, updated] }))
    if (!addCamera.rejected.match(result)) {
      await onSaved()
    }
  }

  const handleRemove = async () => {
    const filtered = (camera.streams ?? []).filter((s) => s.streamName !== streamName)
    const result = await dispatch(addCamera({ ...camera, streams: filtered }))
    if (!addCamera.rejected.match(result)) {
      await onRemoved()
    }
  }

  return (
    <Stack component="form" spacing={2} onSubmit={handleSave}>
      <Typography variant="subtitle2" color="text.secondary">
        {isNew ? `New stream on ${cameraId}` : `Stream: ${streamName}`}
      </Typography>

      <TextField
        label="Stream name"
        value={name}
        onChange={(e) => setName(e.target.value)}
        disabled={!isNew}
        required
        size="small"
        helperText="Unique within this camera, e.g. main, sub, hd"
      />

      <TextField
        label="RTSP URL"
        value={rtspUrl}
        onChange={(e) => setRtspUrl(e.target.value)}
        size="small"
        fullWidth
      />

      <Stack spacing={1}>
        <Stack direction="row" spacing={1} alignItems="center">
          <TextField
            label="ONVIF profile token"
            value={profileToken}
            onChange={(e) => setProfileToken(e.target.value)}
            size="small"
            fullWidth
            helperText="Leave empty for direct RTSP without ONVIF"
          />
          <Button
            size="small"
            variant="outlined"
            onClick={fetchProfiles}
            disabled={profilesLoading}
            sx={{ minWidth: 80, flexShrink: 0 }}
          >
            {profilesLoading ? <CircularProgress size={16} /> : 'Load'}
          </Button>
        </Stack>

        {profilesError && (
          <Typography variant="caption" color="error">
            {profilesError}
          </Typography>
        )}

        {availableProfiles.length > 0 && (
          <Stack direction="row" spacing={0.5} flexWrap="wrap">
            {availableProfiles.map((p) => {
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

      <Stack direction="row" spacing={1}>
        <Button type="submit" variant="outlined" size="small">
          {isNew ? 'Add stream' : 'Save'}
        </Button>
        {!isNew && (
          <Button color="error" variant="outlined" size="small" onClick={handleRemove}>
            Delete stream
          </Button>
        )}
      </Stack>
    </Stack>
  )
}
