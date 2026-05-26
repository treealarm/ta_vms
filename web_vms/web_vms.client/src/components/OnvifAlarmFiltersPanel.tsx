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
import CheckIcon from '@mui/icons-material/Check'
import CloseIcon from '@mui/icons-material/Close'
import EditIcon from '@mui/icons-material/Edit'
import { useTranslation } from 'react-i18next'
import type { OnvifAlarmFilterDto } from '../dto/dtos'

type Props = { cameraId: string }

async function upsertFilter(cameraId: string, filter: OnvifAlarmFilterDto) {
  const resp = await fetch(
    `/api/onvif/cameras/${encodeURIComponent(cameraId)}/alarm-filters`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(filter),
    },
  )
  if (!resp.ok) throw new Error(await resp.text())
}

function FilterRow({ filter, cameraId, onSaved, onDeleted }: {
  filter: OnvifAlarmFilterDto
  cameraId: string
  onSaved: () => void
  onDeleted: () => void
}) {
  const { t } = useTranslation()
  const [editing, setEditing] = useState(false)
  const [text, setText] = useState(filter.filterText)
  const [timeout, setTimeout] = useState(filter.timeoutSeconds)
  const [saving, setSaving] = useState(false)

  const handleSave = async () => {
    if (!text.trim()) return
    setSaving(true)
    try {
      await upsertFilter(cameraId, { ...filter, filterText: text.trim(), timeoutSeconds: timeout })
      setEditing(false)
      onSaved()
    } finally {
      setSaving(false)
    }
  }

  const handleCancel = () => {
    setText(filter.filterText)
    setTimeout(filter.timeoutSeconds)
    setEditing(false)
  }

  const handleDelete = async () => {
    const resp = await fetch(
      `/api/onvif/cameras/${encodeURIComponent(cameraId)}/alarm-filters/${encodeURIComponent(filter.id!)}`,
      { method: 'DELETE' },
    )
    if (resp.ok) onDeleted()
  }

  if (editing) {
    return (
      <Box component="form" onSubmit={(e) => { e.preventDefault(); void handleSave() }}>
        <Stack direction="row" spacing={1} alignItems="center">
          <TextField
            value={text}
            onChange={(e) => setText(e.target.value)}
            size="small"
            autoFocus
            sx={{ flex: 1 }}
          />
          <TextField
            type="number"
            value={timeout}
            onChange={(e) => setTimeout(Math.max(1, Number(e.target.value)))}
            size="small"
            sx={{ width: 100 }}
            inputProps={{ min: 1 }}
          />
          <Tooltip title={t('common.save')}>
            <span>
              <IconButton size="small" type="submit" disabled={!text.trim() || saving} color="primary">
                <CheckIcon fontSize="small" />
              </IconButton>
            </span>
          </Tooltip>
          <Tooltip title={t('common.cancel')}>
            <IconButton size="small" onClick={handleCancel}>
              <CloseIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        </Stack>
      </Box>
    )
  }

  return (
    <Stack direction="row" spacing={1} alignItems="center">
      <Typography variant="body2" sx={{ flex: 1 }}>{filter.filterText}</Typography>
      <Typography variant="body2" color="text.secondary" sx={{ whiteSpace: 'nowrap' }}>
        {filter.timeoutSeconds}s
      </Typography>
      <Tooltip title={t('recording.deleteTooltip')}>
        <IconButton size="small" onClick={() => setEditing(true)}>
          <EditIcon fontSize="small" />
        </IconButton>
      </Tooltip>
      <Tooltip title={t('common.delete')}>
        <IconButton size="small" color="error" onClick={() => void handleDelete()}>
          <DeleteIcon fontSize="small" />
        </IconButton>
      </Tooltip>
    </Stack>
  )
}

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
      await upsertFilter(cameraId, { cameraId, filterText: newText.trim(), timeoutSeconds: newTimeout })
      setNewText('')
      setNewTimeout(30)
      await load()
    } catch (e) {
      setError(String(e))
    }
  }

  return (
    <Stack spacing={1.5} sx={{ pt: 1 }}>
      {error && <Typography color="error" variant="body2">{error}</Typography>}
      {loading && <Typography variant="body2" color="text.secondary">{t('common.loading')}</Typography>}

      {!loading && filters.length === 0 && (
        <Typography variant="body2" color="text.secondary">{t('onvifFilters.noFilters')}</Typography>
      )}

      {filters.map((f) => (
        <FilterRow
          key={f.id}
          filter={f}
          cameraId={cameraId}
          onSaved={() => void load()}
          onDeleted={() => setFilters((prev) => prev.filter((x) => x.id !== f.id))}
        />
      ))}

      <Box component="form" onSubmit={(e) => { e.preventDefault(); void handleAdd() }} sx={{ pt: 0.5 }}>
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
