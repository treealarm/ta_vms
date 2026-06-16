import { useEffect, useImperativeHandle, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  IconButton,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  TextField,
  Typography,
} from '@mui/material'
import DeleteIcon from '@mui/icons-material/Delete'
import EditIcon from '@mui/icons-material/Edit'
import AddIcon from '@mui/icons-material/Add'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { loadMediaMtxServers, removeMediaMtxServer, upsertMediaMtxServer } from '../store/mediaMtxSlice'
import type { MediaMtxServerDto } from '../dto/dtos'

export type MediaMtxServersHandle = { applyIfDirty: () => Promise<boolean> }

const EMPTY: MediaMtxServerDto = { id: '', name: '', host: '', webrtcHost: '', rtspPort: 8554, webrtcPort: 8889, apiPort: 9997 }

export default function MediaMtxServersPanel({ ref }: { ref?: React.Ref<MediaMtxServersHandle> }) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const { servers, isBusy, status } = useAppSelector((s) => s.mediaMtx)
  const [editing, setEditing] = useState<MediaMtxServerDto | null>(null)

  useEffect(() => {
    dispatch(loadMediaMtxServers())
  }, [dispatch])

  // Warn browser on close when editing
  useEffect(() => {
    if (!editing) return
    const handler = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = '' }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [editing])

  const handleEdit = (s: MediaMtxServerDto) => setEditing({ ...s })
  const handleNew = () => setEditing({ ...EMPTY })
  const handleCancel = () => setEditing(null)

  const handleSaveInternal = async (): Promise<boolean> => {
    if (!editing) return true
    const result = await dispatch(upsertMediaMtxServer(editing))
    if (!upsertMediaMtxServer.rejected.match(result)) {
      await dispatch(loadMediaMtxServers())
      setEditing(null)
      return true
    }
    return false
  }

  const handleSave = () => void handleSaveInternal()

  useImperativeHandle(ref, () => ({
    applyIfDirty: () =>
      editing?.name && editing.host
        ? handleSaveInternal()
        : Promise.resolve(true),
  }))

  const handleRemove = async (id: string) => {
    const result = await dispatch(removeMediaMtxServer(id))
    if (!removeMediaMtxServer.rejected.match(result)) {
      await dispatch(loadMediaMtxServers())
    }
  }

  const setPort = (field: 'rtspPort' | 'webrtcPort' | 'apiPort', raw: string) => {
    if (!editing) return
    const v = parseInt(raw, 10)
    if (!Number.isNaN(v) && v > 0 && v < 65536)
      setEditing({ ...editing, [field]: v })
  }

  return (
    <Stack spacing={2}>
      <Stack direction="row" alignItems="center" justifyContent="space-between">
        <Typography variant="h6">{t('mediaMtx.title')}</Typography>
        <Button variant="outlined" size="small" startIcon={<AddIcon />} onClick={handleNew} disabled={isBusy}>
          {t('mediaMtx.addServer')}
        </Button>
      </Stack>

      {status !== 'Ready' && (
        <Alert severity={status.startsWith('Error') || status.startsWith('Fail') ? 'error' : 'success'} sx={{ flexShrink: 0 }}>
          {status}
        </Alert>
      )}

      {editing && (
        <Paper variant="outlined" sx={{ p: 2 }}>
          <Typography variant="subtitle2" gutterBottom>
            {editing.id ? t('mediaMtx.editServer', { name: editing.name }) : t('mediaMtx.newServer')}
          </Typography>
          <Stack spacing={1.5} sx={{ maxWidth: 420 }}>
            <TextField
              label={t('common.name')}
              value={editing.name}
              onChange={(e) => setEditing({ ...editing, name: e.target.value })}
              size="small"
              required
            />
            <TextField
              label={t('mediaMtx.host')}
              placeholder="192.168.1.100"
              value={editing.host}
              onChange={(e) => setEditing({ ...editing, host: e.target.value })}
              size="small"
              required
            />
            <TextField
              label={t('mediaMtx.webrtcHost')}
              placeholder="192.168.1.100"
              value={editing.webrtcHost}
              onChange={(e) => setEditing({ ...editing, webrtcHost: e.target.value })}
              size="small"
              helperText={t('mediaMtx.webrtcHostHelper')}
            />
            <Stack direction="row" spacing={1}>
              <TextField
                label={t('mediaMtx.rtspPort')}
                value={editing.rtspPort}
                onChange={(e) => setPort('rtspPort', e.target.value)}
                size="small"
                sx={{ width: 120 }}
                type="number"
              />
              <TextField
                label={t('mediaMtx.webrtcPort')}
                value={editing.webrtcPort}
                onChange={(e) => setPort('webrtcPort', e.target.value)}
                size="small"
                sx={{ width: 120 }}
                type="number"
              />
              <TextField
                label={t('mediaMtx.apiPort')}
                value={editing.apiPort}
                onChange={(e) => setPort('apiPort', e.target.value)}
                size="small"
                sx={{ width: 120 }}
                type="number"
              />
            </Stack>
            <Stack direction="row" spacing={1}>
              <Button variant="outlined" size="small" onClick={handleSave} disabled={isBusy}>
                {t('common.apply')}
              </Button>
              <Button variant="text" size="small" onClick={handleCancel} disabled={isBusy}>
                {t('common.cancel')}
              </Button>
            </Stack>
          </Stack>
        </Paper>
      )}

      <TableContainer component={Paper} variant="outlined">
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>{t('mediaMtx.colName')}</TableCell>
              <TableCell>{t('mediaMtx.colHost')}</TableCell>
              <TableCell>{t('mediaMtx.colRtsp')}</TableCell>
              <TableCell>{t('mediaMtx.colWebrtc')}</TableCell>
              <TableCell>{t('mediaMtx.colApi')}</TableCell>
              <TableCell align="right" sx={{ width: 80 }} />
            </TableRow>
          </TableHead>
          <TableBody>
            {servers.length === 0 && (
              <TableRow>
                <TableCell colSpan={5}>
                  <Box sx={{ color: 'text.secondary', py: 1 }}>{t('mediaMtx.noServers')}</Box>
                </TableCell>
              </TableRow>
            )}
            {servers.map((s) => (
              <TableRow key={s.id}>
                <TableCell>{s.name}</TableCell>
                <TableCell sx={{ fontFamily: 'monospace', fontSize: '0.8rem' }}>
                  {s.host}{s.webrtcHost && s.webrtcHost !== s.host ? ` / ${s.webrtcHost}` : ''}
                </TableCell>
                <TableCell sx={{ fontFamily: 'monospace', fontSize: '0.8rem' }}>{s.rtspPort}</TableCell>
                <TableCell sx={{ fontFamily: 'monospace', fontSize: '0.8rem' }}>{s.webrtcPort}</TableCell>
                <TableCell sx={{ fontFamily: 'monospace', fontSize: '0.8rem' }}>{s.apiPort}</TableCell>
                <TableCell align="right">
                  <IconButton size="small" onClick={() => handleEdit(s)}>
                    <EditIcon fontSize="small" />
                  </IconButton>
                  <IconButton size="small" color="error" onClick={() => void handleRemove(s.id)} disabled={isBusy}>
                    <DeleteIcon fontSize="small" />
                  </IconButton>
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </TableContainer>
    </Stack>
  )
}
