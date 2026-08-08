import { useEffect, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Chip,
  Dialog,
  DialogActions,
  DialogContent,
  DialogContentText,
  DialogTitle,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableRow,
  Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'

import { useAppDispatch, useAppSelector } from '../store'
import {
  deleteEdgeRecording,
  fetchEdgeRecordingJobs,
  setEdgeRecordingJobMode,
} from '../store/edgeArchiveSlice'

/**
 * The camera's own recording jobs and configuration. Everything here changes state on the device
 * itself, so destructive actions are confirmed and nothing is applied optimistically.
 */
export default function EdgeCameraRecordingPanel({ cameraId }: { cameraId: string }) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const data = useAppSelector((s) => s.edgeArchive.recordingJobs)
  const isBusy = useAppSelector((s) => s.edgeArchive.isBusy)
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null)

  useEffect(() => {
    void dispatch(fetchEdgeRecordingJobs(cameraId))
  }, [dispatch, cameraId])

  const jobs = data?.jobs ?? []
  const configurations = data?.configurations ?? []

  return (
    <Stack spacing={2} sx={{ p: 2, overflowY: 'auto' }}>
      {data?.message && <Alert severity="warning">{data.message}</Alert>}

      <Box>
        <Typography variant="subtitle2" gutterBottom>{t('edge.recordingJobs')}</Typography>
        {jobs.length === 0 ? (
          <Typography variant="body2" color="text.secondary">{t('edge.noRecordingJobs')}</Typography>
        ) : (
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>{t('edge.jobToken')}</TableCell>
                <TableCell>{t('edge.recording')}</TableCell>
                <TableCell>{t('edge.mode')}</TableCell>
                <TableCell>{t('edge.source')}</TableCell>
                <TableCell />
              </TableRow>
            </TableHead>
            <TableBody>
              {jobs.map((job) => {
                const active = job.mode.toLowerCase() === 'active'
                return (
                  <TableRow key={job.jobToken}>
                    <TableCell>{job.jobToken}</TableCell>
                    <TableCell>{job.recordingToken}</TableCell>
                    <TableCell>
                      <Chip size="small" label={job.mode} color={active ? 'success' : 'default'} />
                    </TableCell>
                    <TableCell>{job.sourceToken}</TableCell>
                    <TableCell>
                      <Button
                        size="small"
                        disabled={isBusy}
                        onClick={() => void dispatch(setEdgeRecordingJobMode({
                          cameraId, jobToken: job.jobToken, mode: active ? 'Idle' : 'Active',
                        }))}
                      >
                        {active ? t('edge.stopJob') : t('edge.startJob')}
                      </Button>
                    </TableCell>
                  </TableRow>
                )
              })}
            </TableBody>
          </Table>
        )}
      </Box>

      <Box>
        <Typography variant="subtitle2" gutterBottom>{t('edge.recordings')}</Typography>
        {configurations.length === 0 ? (
          <Typography variant="body2" color="text.secondary">{t('edge.noRecordings')}</Typography>
        ) : (
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>{t('edge.recording')}</TableCell>
                <TableCell>{t('edge.source')}</TableCell>
                <TableCell>{t('edge.content')}</TableCell>
                <TableCell>{t('edge.retention')}</TableCell>
                <TableCell />
              </TableRow>
            </TableHead>
            <TableBody>
              {configurations.map((cfg) => (
                <TableRow key={cfg.recordingToken}>
                  <TableCell>{cfg.recordingToken}</TableCell>
                  <TableCell>{cfg.sourceName || cfg.sourceId}</TableCell>
                  <TableCell>{cfg.content}</TableCell>
                  <TableCell>
                    {/* PT0S is the ONVIF way of saying "keep until the card fills up". */}
                    {cfg.maximumRetentionTime === 'PT0S'
                      ? t('edge.retentionUnlimited')
                      : cfg.maximumRetentionTime}
                  </TableCell>
                  <TableCell>
                    <Button
                      size="small"
                      color="error"
                      disabled={isBusy}
                      onClick={() => setConfirmDelete(cfg.recordingToken)}
                    >
                      {t('common.delete')}
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Box>

      <Dialog open={confirmDelete !== null} onClose={() => setConfirmDelete(null)}>
        <DialogTitle>{t('edge.deleteRecordingTitle')}</DialogTitle>
        <DialogContent>
          <DialogContentText>
            {t('edge.deleteRecordingWarning', { token: confirmDelete ?? '' })}
          </DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setConfirmDelete(null)}>{t('common.cancel')}</Button>
          <Button
            color="error"
            onClick={() => {
              if (confirmDelete) {
                void dispatch(deleteEdgeRecording({ cameraId, recordingToken: confirmDelete }))
              }
              setConfirmDelete(null)
            }}
          >
            {t('common.delete')}
          </Button>
        </DialogActions>
      </Dialog>
    </Stack>
  )
}
