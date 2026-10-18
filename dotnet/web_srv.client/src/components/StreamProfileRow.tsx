import { useEffect, useState } from 'react'
import { CircularProgress, MenuItem, Select } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { isRecordable, type CameraStreamDto, type RecordingProfileDto } from '../dto/dtos'
import { useAppDispatch } from '../store'
import { setStreamRecordingProfile } from '../store/liveSlice'

type Props = {
  cameraId: string
  stream: CameraStreamDto | null
}

export default function StreamProfileRow({ cameraId, stream }: Props) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const [profiles, setProfiles] = useState<RecordingProfileDto[]>([])
  const [busy, setBusy] = useState(false)
  const [pendingValue, setPendingValue] = useState<string | null>(null)

  useEffect(() => {
    fetch('/api/recording-profiles')
      .then((r) => r.json() as Promise<RecordingProfileDto[]>)
      .then(setProfiles)
      .catch(() => {})
  }, [])

  if (!stream) return null

  // A stream that is not available for recording keeps its configured profile — the server refuses
  // to change it and would ignore it anyway — so the control stays visible and goes inert. Hiding
  // it would suggest this stream has no recording settings at all.
  const recordable = isRecordable(stream)
  const currentValue = pendingValue !== null ? pendingValue : (stream.recordingProfile?.id ?? '')

  const handleChange = async (profileId: string) => {
    if (!profileId) return
    setPendingValue(profileId)
    setBusy(true)
    try {
      await dispatch(setStreamRecordingProfile({ cameraId, streamName: stream.streamName, profileId }))
    } finally {
      setBusy(false)
      setPendingValue(null)
    }
  }

  return (
    <>
      <Select
        size="small"
        displayEmpty
        value={currentValue}
        onChange={(e) => void handleChange(e.target.value)}
        disabled={busy || profiles.length === 0 || !recordable}
        title={recordable ? undefined : t('cameraCard.streamNotRecordable')}
        sx={{ flex: 1, fontSize: '0.72rem', height: 22, minWidth: 96, '.MuiSelect-select': { py: 0, px: 0.75 } }}
        renderValue={(v) =>
          profiles.find((p) => p.id === v)?.name ?? (
            <span style={{ color: 'inherit', opacity: 0.5 }}>—</span>
          )
        }
      >
        {profiles.map((p) => (
          <MenuItem key={p.id} value={p.id} sx={{ fontSize: '0.72rem' }}>
            {p.name}
          </MenuItem>
        ))}
      </Select>
      {busy && <CircularProgress size={14} />}
    </>
  )
}
