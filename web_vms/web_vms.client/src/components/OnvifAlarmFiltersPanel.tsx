import { useEffect, useState } from 'react'
import {
  Box,
  Button,
  IconButton,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import DeleteIcon from '@mui/icons-material/Delete'
import { useTranslation } from 'react-i18next'
import type { OnvifAlarmFilterDto } from '../dto/dtos'

type Props = { cameraId: string }

export default function OnvifAlarmFiltersPanel({ cameraId }: Props) {
  const { t } = useTranslation()
  const [filters, setFilters] = useState<OnvifAlarmFilterDto[]>([])
  const [newText, setNewText] = useState('')
  const [newTimeout, setNewTimeout] = useState(30)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = async () => {
    setLoading(true)
    setError(null)
    try {
      const resp = await fetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/alarm-filters`)
      if (!resp.ok) throw new Error(await resp.text())
      setFilters(await resp.json())
    } catch (e) {
      setError(String(e))
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => { void load() }, [cameraId])

  const handleAdd = async () => {
    if (!newText.trim()) return
    setError(null)
    try {
      const resp = await fetch(
        `/api/onvif/cameras/${encodeURIComponent(cameraId)}/alarm-filters`,
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ filterText: newText.trim(), timeoutSeconds: newTimeout }),
        },
      )
      if (!resp.ok) throw new Error(await resp.text())
      setNewText('')
      setNewTimeout(30)
      await load()
    } catch (e) {
      setError(String(e))
    }
  }

  const handleDelete = async (filterId: string) => {
    setError(null)
    try {
      const resp = await fetch(
        `/api/onvif/cameras/${encodeURIComponent(cameraId)}/alarm-filters/${encodeURIComponent(filterId)}`,
        { method: 'DELETE' },
      )
      if (!resp.ok) throw new Error(await resp.text())
      await load()
    } catch (e) {
      setError(String(e))
    }
  }

  return (
    <Stack spacing={2} sx={{ pt: 1 }}>
      {error && (
        <Typography color="error" variant="body2">{error}</Typography>
      )}

      {loading && (
        <Typography variant="body2" color="text.secondary">{t('common.loading')}</Typography>
      )}

      {!loading && filters.length === 0 && (
        <Typography variant="body2" color="text.secondary">
          {t('onvifFilters.noFilters')}
        </Typography>
      )}

      {filters.map((f) => (
        <Stack key={f.id} direction="row" spacing={1} alignItems="center">
          <Typography variant="body2" sx={{ flex: 1 }}>{f.filterText}</Typography>
          <Typography variant="body2" color="text.secondary" sx={{ whiteSpace: 'nowrap' }}>
            {f.timeoutSeconds}s
          </Typography>
          <Tooltip title={t('common.delete')}>
            <IconButton size="small" onClick={() => void handleDelete(f.id!)}>
              <DeleteIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        </Stack>
      ))}

      <Box component="form" onSubmit={(e) => { e.preventDefault(); void handleAdd() }}>
        <Stack direction="row" spacing={1} alignItems="flex-start">
          <TextField
            label={t('onvifFilters.filterText')}
            value={newText}
            onChange={(e) => setNewText(e.target.value)}
            size="small"
            sx={{ flex: 1 }}
          />
          <TextField
            label={t('onvifFilters.timeoutSec')}
            type="number"
            value={newTimeout}
            onChange={(e) => setNewTimeout(Math.max(1, Number(e.target.value)))}
            size="small"
            sx={{ width: 120 }}
            inputProps={{ min: 1 }}
          />
          <Button
            type="submit"
            variant="outlined"
            size="small"
            disabled={!newText.trim()}
            sx={{ mt: 0.5 }}
          >
            {t('onvifFilters.addFilter')}
          </Button>
        </Stack>
      </Box>
    </Stack>
  )
}
