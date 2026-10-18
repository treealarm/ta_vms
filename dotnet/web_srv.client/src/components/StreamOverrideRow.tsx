import { useEffect, useState } from 'react'
import { CircularProgress, MenuItem, Select, Stack, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { recordableStreams, type CameraStreamDto, type RecordingProfileDto } from '../dto/dtos'
import { useAppDispatch } from '../store'
import { setStreamOverride } from '../store/liveSlice'

type Props = {
  cameraId: string
  /**
   * Every stream of the camera, not just the visible one. An alarm override is applied to the whole
   * camera (ActivateAlarmOverride leaves stream_name empty), so a control that could only clear the
   * stream being watched left the override stuck on any stream without WebRTC — recording forever,
   * with the card outlined red, and nothing in the monitor able to touch it. The API stays
   * per-stream; this control is simply the camera-wide one.
   */
  streams: CameraStreamDto[]
}

export default function StreamOverrideRow({ cameraId, streams }: Props) {
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

  if (streams.length === 0) return null

  // Any stream carrying an override counts. Deliberately not "all of them": a leftover on a stream
  // the monitor cannot show is exactly the case that has to stay visible and clearable.
  const overridden = streams.find((s) => s.overrideProfile)
  const isOverride = !!overridden
  const overrideValue = pendingValue !== null ? pendingValue : (overridden?.overrideProfile?.id ?? '')

  // Setting an override starts a recording, so it reaches only the streams allowed into the
  // archive — the server refuses the rest anyway. Clearing one is not a recording and must always
  // be possible: an override left on a stream whose flag was cleared afterwards has to stay
  // removable, which is the same reason this control spans the camera rather than one stream.
  // The same set the monitor's record button acts on — enabled and available for recording. An
  // override on a disabled stream would be as inert as one on a stream barred from the archive.
  const settableStreams = recordableStreams({ streams })
  const canSet = settableStreams.length > 0

  const handleChange = async (profileId: string) => {
    const clearing = profileId === ''
    setPendingValue(profileId)
    setBusy(true)
    try {
      // Sequential rather than parallel: each call rewrites the camera's config in VmsCfg, and
      // firing them together would have them race over the same rows.
      for (const s of clearing ? streams : settableStreams) {
        await dispatch(setStreamOverride({
          cameraId,
          streamName: s.streamName,
          profileId: clearing ? null : profileId,
        }))
      }
    } finally {
      setBusy(false)
      setPendingValue(null)
    }
  }

  return (
    <Stack
      direction="row"
      alignItems="center"
      spacing={1}
      title={
        !canSet && !isOverride
          ? t('cameraCard.noRecordableStreams')
          : streams.length > 1 ? t('streamOverride.appliesToAllStreams') : undefined
      }
      sx={{ flexShrink: 0, px: 0.25 }}
    >
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
        disabled={busy || presets.length === 0 || (!canSet && !isOverride)}
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
