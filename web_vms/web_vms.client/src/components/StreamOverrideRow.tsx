import { useEffect, useState } from 'react'
import { CircularProgress, MenuItem, Select, Stack, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'
import type { CameraStreamDto, RecordingProfileDto } from '../dto/dtos'
import { useAppDispatch } from '../store'
import { setStreamOverride } from '../store/liveSlice'

type Props = {
  cameraId: string
  stream: CameraStreamDto | null
}

export default function StreamOverrideRow({ cameraId, stream }: Props) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
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

  const isOverride = !!stream.overrideProfile
  const overrideValue = pendingValue !== null ? pendingValue : (stream.overrideProfile?.id ?? '')

  const handleChange = async (profileId: string) => {
    setPendingValue(profileId)
    setBusy(true)
    try {
      await dispatch(setStreamOverride({ cameraId, streamName: stream.streamName, profileId: profileId === '' ? null : profileId }))
    } finally {
      setBusy(false)
      setPendingValue(null)
    }
  }

  return (
    <Stack direction="row" alignItems="center" spacing={1} sx={{ flexShrink: 0, px: 0.25 }}>
      {isOverride && (
        <Typography variant="caption" sx={{ color: 'primary.main', fontSize: '0.68rem', flexShrink: 0 }}>
          {t('streamOverride.override')}
        </Typography>
      )}

      <Select
        size="small"
        displayEmpty
        value={overrideValue}
        onChange={(e) => void handleChange(e.target.value)}
        disabled={busy || presets.length === 0}
        sx={{ flex: 1, fontSize: '0.72rem', height: 22, minWidth: 96, '.MuiSelect-select': { py: 0, px: 0.75 } }}
        renderValue={(v) =>
          v === '' ? (
            <Typography variant="caption" sx={{ color: 'text.secondary' }}>{t('streamOverride.overridePlaceholder')}</Typography>
          ) : (
            <Typography variant="caption">{presets.find((p) => p.id === v)?.name ?? v}</Typography>
          )
        }
      >
        {isOverride && (
          <MenuItem value="" sx={{ fontSize: '0.72rem' }}>
            <em>{t('streamOverride.clearOverride')}</em>
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
