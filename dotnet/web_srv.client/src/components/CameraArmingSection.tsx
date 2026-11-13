import { useEffect, useState } from 'react'
import { Alert, MenuItem, Stack, TextField, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { getCameraArmState, setCameraArmMode } from '../api/controlApi'
import { errorText } from '../api/requestJson'
import type { CameraArmMode } from '../dto/dtos'

/**
 * The administrator's arming mode for a camera. It is not on the operator's card: `inherit` is a
 * setting, and an operator's toggle there is a command — on guard or not, decided explicitly. This
 * one says who decides: the zone the camera stands in, or the card.
 */
export default function CameraArmingSection({ cameraId }: { cameraId: string }) {
  const { t } = useTranslation()
  const [mode, setMode] = useState<CameraArmMode>('inherit')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let live = true
    void getCameraArmState(cameraId)
      .then((state) => { if (live) setMode(state.armMode ?? 'inherit') })
      .catch((e) => { if (live) setError(errorText(e)) })
      .finally(() => { if (live) setLoading(false) })
    return () => { live = false }
  }, [cameraId])

  const write = async (next: CameraArmMode) => {
    setMode(next)
    setError(null)
    try {
      await setCameraArmMode(cameraId, next)
    } catch (e) {
      setError(errorText(e))
    }
  }

  return (
    <Stack spacing={1}>
      <TextField
        select
        size="small"
        label={t('cameras.armMode')}
        value={mode}
        disabled={loading}
        onChange={(e) => void write(e.target.value as CameraArmMode)}
        sx={{ maxWidth: 280 }}
        helperText={t('cameras.armModeHint')}
      >
        <MenuItem value="inherit">{t('cameras.armModeInherit')}</MenuItem>
        <MenuItem value="armed">{t('cameras.armModeArmed')}</MenuItem>
        <MenuItem value="disarmed">{t('cameras.armModeDisarmed')}</MenuItem>
      </TextField>
      <Typography variant="caption" color="text.secondary">
        {t('cameras.armModeEffects')}
      </Typography>
      {error && <Alert severity="error" sx={{ py: 0 }}>{error}</Alert>}
    </Stack>
  )
}
