import { useEffect, useState } from 'react'
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
import { useAppDispatch, useAppSelector } from '../store'
import { loadMediaMtxServers, removeMediaMtxServer, upsertMediaMtxServer } from '../store/mediaMtxSlice'
import type { MediaMtxServerDto } from '../dto/dtos'

const EMPTY: MediaMtxServerDto = { id: '', name: '', rtspUrl: '', webrtcUrl: '' }

export default function MediaMtxServersPanel() {
  const dispatch = useAppDispatch()
  const { servers, isBusy, status } = useAppSelector((s) => s.mediaMtx)
  const [editing, setEditing] = useState<MediaMtxServerDto | null>(null)

  useEffect(() => {
    dispatch(loadMediaMtxServers())
  }, [dispatch])

  const handleEdit = (s: MediaMtxServerDto) => setEditing({ ...s })
  const handleNew = () => setEditing({ ...EMPTY })
  const handleCancel = () => setEditing(null)

  const handleSave = async () => {
    if (!editing) return
    const result = await dispatch(upsertMediaMtxServer(editing))
    if (!upsertMediaMtxServer.rejected.match(result)) {
      await dispatch(loadMediaMtxServers())
      setEditing(null)
    }
  }

  const handleRemove = async (id: string) => {
    const result = await dispatch(removeMediaMtxServer(id))
    if (!removeMediaMtxServer.rejected.match(result)) {
      await dispatch(loadMediaMtxServers())
    }
  }

  return (
    <Stack spacing={2}>
      <Stack direction="row" alignItems="center" justifyContent="space-between">
        <Typography variant="h6">MediaMTX Servers</Typography>
        <Button variant="outlined" size="small" startIcon={<AddIcon />} onClick={handleNew} disabled={isBusy}>
          Add server
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
            {editing.id ? `Edit: ${editing.name}` : 'New server'}
          </Typography>
          <Stack spacing={1.5}>
            <TextField
              label="Name"
              value={editing.name}
              onChange={(e) => setEditing({ ...editing, name: e.target.value })}
              size="small"
              required
            />
            <TextField
              label="RTSP push URL (incoming)"
              placeholder="rtsp://mediamtx:8554"
              value={editing.rtspUrl}
              onChange={(e) => setEditing({ ...editing, rtspUrl: e.target.value })}
              size="small"
              required
              helperText="Used by the media server to push streams into MediaMTX"
            />
            <TextField
              label="WebRTC client URL (outgoing)"
              placeholder="http://hostname:8889"
              value={editing.webrtcUrl}
              onChange={(e) => setEditing({ ...editing, webrtcUrl: e.target.value })}
              size="small"
              required
              helperText="Used by the browser to receive WebRTC streams (WHEP)"
            />
            <Stack direction="row" spacing={1}>
              <Button variant="outlined" size="small" onClick={handleSave} disabled={isBusy}>
                Save
              </Button>
              <Button variant="text" size="small" onClick={handleCancel} disabled={isBusy}>
                Cancel
              </Button>
            </Stack>
          </Stack>
        </Paper>
      )}

      <TableContainer component={Paper} variant="outlined">
        <Table size="small">
          <TableHead>
            <TableRow>
              <TableCell>Name</TableCell>
              <TableCell>RTSP push URL</TableCell>
              <TableCell>WebRTC client URL</TableCell>
              <TableCell align="right" sx={{ width: 80 }} />
            </TableRow>
          </TableHead>
          <TableBody>
            {servers.length === 0 && (
              <TableRow>
                <TableCell colSpan={4}>
                  <Box sx={{ color: 'text.secondary', py: 1 }}>No servers configured</Box>
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
                  <IconButton size="small" color="error" onClick={() => handleRemove(s.id)} disabled={isBusy}>
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
