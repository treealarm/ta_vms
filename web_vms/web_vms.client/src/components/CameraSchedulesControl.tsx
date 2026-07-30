import { useEffect, useRef, useState } from 'react'
import {
  Box,
  Button,
  Chip,
  FormControl,
  IconButton,
  InputLabel,
  MenuItem,
  Select,
  Stack,
  Typography,
} from '@mui/material'
import DeleteIcon from '@mui/icons-material/Delete'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { loadSchedules, setCameraSchedules } from '../store/scheduleSlice'
import { fetchCameraState } from '../store/liveSlice'

type Props =
  | { cameraId: string; value?: undefined; onChange?: undefined }
  | { cameraId?: undefined; value: string[]; onChange: (v: string[]) => void }

/**
 * Assigns recording schedules to a camera. While any assigned window is open the camera records
 * continuously, ahead of its stream profiles and of any manual or alarm override.
 *
 * Controlled mode (value/onChange) is for a camera that does not exist yet; uncontrolled mode reads
 * the saved assignment from the camera state and applies it itself.
 */
export default function CameraSchedulesControl({ cameraId, value, onChange }: Props) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const schedules = useAppSelector((state) => state.schedule.schedules)
  const camera = useAppSelector((state) => state.live.cameraStatesById[cameraId ?? ''])

  const controlled = cameraId === undefined
  const savedIds = camera?.scheduleIds ?? []

  // Uncontrolled mode keeps only the unsaved edit; null means "no local changes", so the displayed
  // value follows the camera state without an effect syncing it back and forth.
  const [draft, setDraft] = useState<string[] | null>(null)
  const [pendingId, setPendingId] = useState('')

  const selectedIds = controlled ? value : draft ?? savedIds
  const dirty = !controlled && draft !== null

  useEffect(() => { void dispatch(loadSchedules()) }, [dispatch])

  // Auto-apply on unmount if dirty (uncontrolled mode only), like CameraStoragesControl. The latest
  // values are mirrored into a ref from an effect so the cleanup does not capture a stale closure.
  const latest = useRef({ dirty, cameraId, selectedIds })
  useEffect(() => {
    latest.current = { dirty, cameraId, selectedIds }
  }, [dirty, cameraId, selectedIds])
  useEffect(() => () => {
    const snapshot = latest.current
    if (!snapshot.dirty || !snapshot.cameraId) return
    void dispatch(setCameraSchedules({ cameraId: snapshot.cameraId, scheduleIds: snapshot.selectedIds }))
  }, [dispatch])

  const update = (next: string[]) => {
    if (controlled) onChange(next)
    else setDraft(next)
  }

  const onApply = async () => {
    await dispatch(setCameraSchedules({ cameraId: cameraId!, scheduleIds: selectedIds }))
    setDraft(null)
    // schedule_active is recomputed server-side, so refresh to pick up the new recording state
    await dispatch(fetchCameraState(cameraId!))
  }

  const nameOf = (id: string) => schedules.find((s) => s.id === id)?.name ?? id
  const available = schedules.filter((s) => s.id && !selectedIds.includes(s.id))

  return (
    <Stack spacing={1}>
      <Stack direction="row" spacing={1} alignItems="center">
        <Typography variant="body2" color="text.secondary">{t('scheduleControl.title')}</Typography>
        {!controlled && camera?.scheduleActive && (
          <Chip label={t('scheduleControl.activeNow')} size="small" color="error" variant="outlined" />
        )}
      </Stack>

      {selectedIds.length === 0 ? (
        <Typography variant="body2" color="text.secondary" sx={{ pl: 1 }}>
          {t('scheduleControl.noSchedules')}
        </Typography>
      ) : (
        <Box sx={{ border: 1, borderColor: 'divider', borderRadius: 1 }}>
          {selectedIds.map((id, i) => (
            <Stack
              key={id}
              direction="row"
              alignItems="center"
              spacing={1}
              sx={{ px: 1.5, py: 0.5, borderTop: i > 0 ? 1 : 0, borderColor: 'divider' }}
            >
              <Typography variant="body2" sx={{ flex: 1 }}>{nameOf(id)}</Typography>
              <IconButton size="small" onClick={() => update(selectedIds.filter((x) => x !== id))}>
                <DeleteIcon fontSize="inherit" />
              </IconButton>
            </Stack>
          ))}
        </Box>
      )}

      <Stack direction="row" spacing={1} alignItems="center">
        <FormControl size="small" sx={{ flex: 1 }} disabled={available.length === 0}>
          <InputLabel>{t('scheduleControl.addSchedule')}</InputLabel>
          <Select
            label={t('scheduleControl.addSchedule')}
            value={pendingId}
            onChange={(e) => setPendingId(e.target.value)}
          >
            {available.map((s) => (
              <MenuItem key={s.id} value={s.id}>{s.name}</MenuItem>
            ))}
          </Select>
        </FormControl>
        <Button
          variant="outlined"
          size="small"
          disabled={!pendingId}
          onClick={() => { update([...selectedIds, pendingId]); setPendingId('') }}
        >
          {t('common.add')}
        </Button>
        {!controlled && (
          <>
            <Button variant="text" size="small" disabled={!dirty} onClick={() => setDraft(null)}>
              {t('common.cancel')}
            </Button>
            <Button variant="contained" size="small" disabled={!dirty} onClick={() => void onApply()}>
              {t('common.apply')}
            </Button>
          </>
        )}
      </Stack>
    </Stack>
  )
}
