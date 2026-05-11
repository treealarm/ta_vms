import { useCallback, useEffect, useState } from 'react'
import {
  Box,
  Button,
  Chip,
  Divider,
  IconButton,
  List,
  ListItem,
  ListItemText,
  MenuItem,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import DeleteIcon from '@mui/icons-material/Delete'
import EditIcon from '@mui/icons-material/Edit'
import type { RecordingProfileDto, RecordingMode } from '../dto/dtos'

const EMPTY_PROFILE: Omit<RecordingProfileDto, 'id'> = {
  name: '',
  mode: 'continuous',
  preRecordSeconds: 0,
  postRecordSeconds: 0,
  segmentSeconds: 60,
}

export default function RecordingProfilesPanel() {
  const [profiles, setProfiles] = useState<RecordingProfileDto[]>([])
  const [editing, setEditing] = useState<RecordingProfileDto | null>(null)
  const [isNew, setIsNew] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    try {
      const r = await fetch('/api/recording-profiles')
      if (!r.ok) throw new Error(`HTTP ${r.status}`)
      setProfiles(await r.json() as RecordingProfileDto[])
    } catch (e) {
      setError(String(e))
    }
  }, [])

  useEffect(() => { void load() }, [load])

  const startNew = () => {
    setEditing({ id: '', ...EMPTY_PROFILE })
    setIsNew(true)
    setError(null)
  }

  const startEdit = (p: RecordingProfileDto) => {
    setEditing({ ...p })
    setIsNew(false)
    setError(null)
  }

  const cancel = () => { setEditing(null); setError(null) }

  const save = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!editing) return
    setBusy(true)
    setError(null)
    try {
      const r = await fetch('/api/recording-profiles', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(editing),
      })
      const body = await r.json() as { success: boolean; message: string }
      if (!body.success) { setError(body.message); return }
      setEditing(null)
      await load()
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  const remove = async (id: string) => {
    setBusy(true)
    setError(null)
    try {
      const r = await fetch(`/api/recording-profiles/${encodeURIComponent(id)}`, { method: 'DELETE' })
      const body = await r.json() as { success: boolean; message: string }
      if (!body.success) { setError(body.message); return }
      if (editing?.id === id) setEditing(null)
      await load()
    } catch (e) {
      setError(String(e))
    } finally {
      setBusy(false)
    }
  }

  const setField = <K extends keyof RecordingProfileDto>(key: K, value: RecordingProfileDto[K]) =>
    setEditing((prev) => prev ? { ...prev, [key]: value } : null)

  const setNonNeg = (key: 'preRecordSeconds' | 'postRecordSeconds' | 'segmentSeconds', raw: string) => {
    const v = Number(raw)
    if (!Number.isNaN(v)) setField(key, Math.max(0, Math.floor(v)))
  }

  const modeLabel = (mode: RecordingMode) =>
    mode === 'continuous' ? 'Continuous' : mode === 'prealarm' ? 'Pre-alarm' : 'Off'

  return (
    <Stack spacing={2} sx={{ maxWidth: 560 }}>
      <Typography variant="h6">Recording profiles</Typography>

      <List disablePadding>
        {profiles.map((p, i) => (
          <Box key={p.id}>
            {i > 0 && <Divider />}
            <ListItem
              disablePadding
              secondaryAction={
                <Stack direction="row" spacing={0.5}>
                  <IconButton size="small" onClick={() => startEdit(p)} disabled={busy}>
                    <EditIcon fontSize="small" />
                  </IconButton>
                  <Tooltip title="Delete">
                    <span>
                      <IconButton size="small" color="error" onClick={() => void remove(p.id)} disabled={busy}>
                        <DeleteIcon fontSize="small" />
                      </IconButton>
                    </span>
                  </Tooltip>
                </Stack>
              }
            >
              <ListItemText
                sx={{ py: 0.5, pl: 1 }}
                primary={p.name}
                secondary={
                  <Stack direction="row" spacing={0.5} alignItems="center" component="span">
                    <Chip label={modeLabel(p.mode)} size="small" />
                    {p.mode === 'prealarm' && (
                      <Typography variant="caption" color="text.secondary">
                        pre {p.preRecordSeconds}s · post {p.postRecordSeconds}s · seg {p.segmentSeconds}s
                      </Typography>
                    )}
                    {p.mode === 'continuous' && (
                      <Typography variant="caption" color="text.secondary">
                        seg {p.segmentSeconds}s
                      </Typography>
                    )}
                  </Stack>
                }
              />
            </ListItem>
          </Box>
        ))}
        {profiles.length === 0 && (
          <Typography variant="body2" color="text.secondary" sx={{ pl: 1, py: 1 }}>
            No profiles yet.
          </Typography>
        )}
      </List>

      {!editing && (
        <Box>
          <Button variant="outlined" size="small" onClick={startNew}>
            Add profile
          </Button>
        </Box>
      )}

      {editing && (
        <Stack component="form" spacing={2} onSubmit={(e) => void save(e)}
          sx={{ pt: 1, borderTop: 1, borderColor: 'divider' }}>
          <Typography variant="subtitle2">
            {isNew ? 'New profile' : `Edit: ${profiles.find((p) => p.id === editing.id)?.name ?? editing.name}`}
          </Typography>

          <TextField
            label="Name"
            value={editing.name}
            onChange={(e) => setField('name', e.target.value)}
            required
            size="small"
            sx={{ maxWidth: 260 }}
          />

          <TextField
            select
            label="Mode"
            value={editing.mode}
            onChange={(e) => setField('mode', e.target.value as RecordingMode)}
            size="small"
            sx={{ maxWidth: 200 }}
          >
            <MenuItem value="continuous">Continuous</MenuItem>
            <MenuItem value="prealarm">Pre-alarm</MenuItem>
          </TextField>

          {editing.mode === 'prealarm' && (
            <Stack direction="row" spacing={1}>
              <TextField
                label="Pre-record, sec"
                type="number"
                value={editing.preRecordSeconds}
                onChange={(e) => setNonNeg('preRecordSeconds', e.target.value)}
                size="small"
                slotProps={{ htmlInput: { min: 0 } }}
                sx={{ width: 140 }}
              />
              <TextField
                label="Post-record, sec"
                type="number"
                value={editing.postRecordSeconds}
                onChange={(e) => setNonNeg('postRecordSeconds', e.target.value)}
                size="small"
                slotProps={{ htmlInput: { min: 0 } }}
                sx={{ width: 140 }}
              />
            </Stack>
          )}

          <TextField
            label="Segment, sec"
            type="number"
            value={editing.segmentSeconds}
            onChange={(e) => setNonNeg('segmentSeconds', e.target.value)}
            size="small"
            slotProps={{ htmlInput: { min: 1 } }}
            sx={{ width: 140 }}
          />

          {error && (
            <Typography variant="caption" color="error">{error}</Typography>
          )}

          <Stack direction="row" spacing={1}>
            <Button type="submit" variant="outlined" size="small" disabled={busy}>
              {isNew ? 'Create' : 'Save'}
            </Button>
            <Button size="small" onClick={cancel} disabled={busy}>Cancel</Button>
          </Stack>
        </Stack>
      )}

      {error && !editing && (
        <Typography variant="caption" color="error">{error}</Typography>
      )}
    </Stack>
  )
}
