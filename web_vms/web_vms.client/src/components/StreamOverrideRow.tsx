import { useEffect, useState } from 'react'
import { Chip, CircularProgress, MenuItem, Select, Stack, Typography } from '@mui/material'
import type { CameraStreamDto, RecordingProfileDto } from '../dto/dtos'
import { useAppDispatch } from '../store'
import { fetchCameraState } from '../store/liveSlice'

type Props = {
  cameraId: string
  stream: CameraStreamDto | null
}

function modeLabel(mode: string): string {
  if (mode === 'continuous') return 'Rec'
  if (mode === 'prealarm') return 'Pre-alarm'
  return 'Off'
}

function modeColor(mode: string): 'error' | 'warning' | 'default' {
  if (mode === 'continuous') return 'error'
  if (mode === 'prealarm') return 'warning'
  return 'default'
}

export default function StreamOverrideRow({ cameraId, stream }: Props) {
  const dispatch = useAppDispatch()
  const [presets, setPresets] = useState<RecordingProfileDto[]>([])
  const [busy, setBusy] = useState(false)
  const [pendingValue, setPendingValue] = useState<string | null>(null)

  useEffect(() => {
    fetch('/api/recording-profiles')
      .then((r) => r.json() as Promise<RecordingProfileDto[]>)
      .then((all) => setPresets(all.filter((p) => p.isOverridePreset)))
      .catch(() => {})
  }, [])

  if (!stream) return null

  const effective = stream.overrideProfile ?? stream.recordingProfile
  const effectiveMode = effective?.mode ?? 'off'
  const isOverride = !!stream.overrideProfile
  const overrideValue = pendingValue !== null ? pendingValue : (stream.overrideProfile?.id ?? '')

  const handleChange = async (profileId: string) => {
    setPendingValue(profileId)
    setBusy(true)
    try {
      if (profileId === '') {
        await fetch(
          `/api/recording-profiles/stream-override?cameraId=${encodeURIComponent(cameraId)}&streamName=${encodeURIComponent(stream.streamName)}`,
          { method: 'DELETE' }
        )
      } else {
        await fetch('/api/recording-profiles/stream-override', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ cameraId, streamName: stream.streamName, profileId }),
        })
      }
      void dispatch(fetchCameraState(cameraId))
    } finally {
      setBusy(false)
      setPendingValue(null)
    }
  }

  return (
    <Stack direction="row" alignItems="center" spacing={1} sx={{ flexShrink: 0, px: 0.25 }}>
      {/* active mode status */}
      <Chip
        label={modeLabel(effectiveMode)}
        size="small"
        color={modeColor(effectiveMode)}
        variant={isOverride ? 'filled' : 'outlined'}
        sx={{ fontSize: '0.68rem', height: 20, flexShrink: 0 }}
      />
      {isOverride && (
        <Typography variant="caption" sx={{ color: 'primary.main', fontSize: '0.68rem', flexShrink: 0 }}>
          override
        </Typography>
      )}

      <Select
        size="small"
        displayEmpty
        value={overrideValue}
        onChange={(e) => void handleChange(e.target.value)}
        disabled={busy || presets.length === 0}
        sx={{ fontSize: '0.72rem', height: 22, minWidth: 96, '.MuiSelect-select': { py: 0, px: 0.75 } }}
        renderValue={(v) =>
          v === '' ? (
            <Typography variant="caption" sx={{ color: 'text.secondary' }}>Override…</Typography>
          ) : (
            <Typography variant="caption">{presets.find((p) => p.id === v)?.name ?? v}</Typography>
          )
        }
      >
        {isOverride && (
          <MenuItem value="" sx={{ fontSize: '0.72rem' }}>
            <em>Clear override</em>
          </MenuItem>
        )}
        {presets.map((p) => (
          <MenuItem key={p.id} value={p.id} sx={{ fontSize: '0.72rem' }}>
            {p.name}
          </MenuItem>
        ))}
      </Select>

      {busy && <CircularProgress size={14} />}
    </Stack>
  )
}
