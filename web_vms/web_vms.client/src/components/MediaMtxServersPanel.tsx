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

export type MediaMtxServersHandle = { applyIfDirty: () => Promise<void> }

const EMPTY: MediaMtxServerDto = { id: '', name: '', rtspUrl: '', webrtcUrl: '' }

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

  const handleSaveInternal = async (): Promise<void> => {
    if (!editing) return
    const result = await dispatch(upsertMediaMtxServer(editing))
    if (!upsertMediaMtxServer.rejected.match(result)) {
      await dispatch(loadMediaMtxServers())
      setEditing(null)
    }
  }

  const handleSave = () => void handleSaveInternal()

  useImperativeHandle(ref, () => ({
    applyIfDirty: async () => {
      if (editing?.name && editing.rtspUrl && editing.webrtcUrl) await handleSaveInternal()
    }
  }))

  const handleRemove = async (id: string) => {
    const result = await dispatch(removeMediaMtxServer(id))
    if (!removeMediaMtxServer.rejected.match(result)) {
      await dispatch(loadMediaMtxServers())
    }
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
          <Stack spacing={1.5}>
            <TextField
              label={t('common.name')}
              value={editing.name}
              onChange={(e) => setEditing({ ...editing, name: e.target.value })}
              size="small"
              required
            />
            <TextField
              label={t('mediaMtx.rtspPushUrl')}
              placeholder={t('mediaMtx.rtspPlaceholder')}
              value={editing.rtspUrl}
              onChange={(e) => setEditing({ ...editing, rtspUrl: e.target.value })}
              size="small"
              required
              helperText={t('mediaMtx.rtspHelper')}
            />
            <TextField
              label={t('mediaMtx.webrtcUrl')}
              placeholder={t('mediaMtx.webrtcPlaceholder')}
              value={editing.webrtcUrl}
              onChange={(e) => setEditing({ ...editing, webrtcUrl: e.target.value })}
              size="small"
              required
              helperText={t('mediaMtx.webrtcHelper')}
            />
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
              <TableCell>{t('mediaMtx.colRtsp')}</TableCell>
              <TableCell>{t('mediaMtx.colWebrtc')}</TableCell>
              <TableCell align="right" sx={{ width: 80 }} />
            </TableRow>
          </TableHead>
          <TableBody>
            {servers.length === 0 && (
              <TableRow>
                <TableCell colSpan={4}>
                  <Box sx={{ color: 'text.secondary', py: 1 }}>{t('mediaMtx.noServers')}</Box>
                </TableCell>
              </TableRow>
            )}
            {servers.map((s) => (
              <TableRow key={s.id}>
                <TableCell>{s.name}</TableCell>
                <TableCell sx={{ fontFamily: 'monospace', fontSize: '0.8rem' }}>{s.rtspUrl}</TableCell>
                <TableCell sx={{ fontFamily: 'monospace', fontSize: '0.8rem' }}>{s.webrtcUrl}</TableCell>
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
