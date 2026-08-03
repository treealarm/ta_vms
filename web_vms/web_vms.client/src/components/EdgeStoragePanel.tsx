import { useEffect, useState } from 'react'
import {
  Alert,
  Box,
  Button,
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
import { fetchEdgeStorage, formatEdgeStorage } from '../store/edgeArchiveSlice'

/**
 * The camera's own storage (SD card). Formatting goes through a vendor auxiliary command — ONVIF
 * standardises the transport but not the command string — so the exact command that will be sent is
 * shown before confirming, and the action is irreversible.
 */
export default function EdgeStoragePanel({ cameraId }: { cameraId: string }) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const storage = useAppSelector((s) => s.edgeArchive.storage)
  const isBusy = useAppSelector((s) => s.edgeArchive.isBusy)
  const [confirmFormat, setConfirmFormat] = useState(false)

  useEffect(() => {
    void dispatch(fetchEdgeStorage(cameraId))
  }, [dispatch, cameraId])

  const configurations = storage?.configurations ?? []

  return (
    <Stack spacing={2} sx={{ p: 2, overflowY: 'auto' }}>
      {storage?.message && <Alert severity="warning">{storage.message}</Alert>}

      <Box>
        <Typography variant="subtitle2" gutterBottom>{t('edge.storage')}</Typography>
        {configurations.length === 0 ? (
          <Typography variant="body2" color="text.secondary">
            {storage?.supported ? t('edge.noStorageConfigurations') : t('edge.storageUnsupported')}
          </Typography>
        ) : (
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>{t('edge.storageToken')}</TableCell>
                <TableCell>{t('edge.storageType')}</TableCell>
                <TableCell>{t('edge.storagePath')}</TableCell>
                <TableCell>{t('edge.storageUri')}</TableCell>
              </TableRow>
            </TableHead>
            <TableBody>
              {configurations.map((cfg) => (
                <TableRow key={cfg.token}>
                  <TableCell>{cfg.token}</TableCell>
                  <TableCell>{cfg.type}</TableCell>
                  <TableCell>{cfg.localPath}</TableCell>
                  <TableCell>{cfg.storageUri}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </Box>

      <Box>
        <Typography variant="subtitle2" gutterBottom>{t('edge.formatStorage')}</Typography>
        {storage?.formatSupported ? (
          <Stack spacing={1} alignItems="flex-start">
            <Typography variant="body2" color="text.secondary">
              {t('edge.formatCommandIs', { command: storage.formatCommand })}
            </Typography>
            <Button color="error" variant="outlined" size="small" disabled={isBusy} onClick={() => setConfirmFormat(true)}>
              {t('edge.formatStorage')}
            </Button>
          </Stack>
        ) : (
          <Typography variant="body2" color="text.secondary">{t('edge.formatUnsupported')}</Typography>
        )}
      </Box>

      <Dialog open={confirmFormat} onClose={() => setConfirmFormat(false)}>
        <DialogTitle>{t('edge.formatStorageTitle')}</DialogTitle>
        <DialogContent>
          <DialogContentText>
            {t('edge.formatStorageWarning', { command: storage?.formatCommand ?? '' })}
          </DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setConfirmFormat(false)}>{t('common.cancel')}</Button>
          <Button
            color="error"
            onClick={() => {
              void dispatch(formatEdgeStorage({ cameraId, command: storage?.formatCommand ?? '' }))
              setConfirmFormat(false)
            }}
          >
            {t('edge.formatStorage')}
          </Button>
        </DialogActions>
      </Dialog>
    </Stack>
  )
}
