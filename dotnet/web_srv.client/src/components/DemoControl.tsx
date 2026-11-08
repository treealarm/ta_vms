import { useEffect, useState } from 'react'
import {
  Alert, Button, CircularProgress, Dialog, DialogActions, DialogContent, DialogTitle, Stack,
  Typography,
} from '@mui/material'
import CloudDownloadIcon from '@mui/icons-material/CloudDownload'
import DeleteSweepIcon from '@mui/icons-material/DeleteSweep'
import { useTranslation } from 'react-i18next'
import { mapApi } from '../api/mapApi'
import { errorText } from '../api/requestJson'
import type { MapDemoReport } from '../dto/mapDtos'
import { useAppDispatch } from '../store'
import { loadPlacements, loadSpaces } from '../store/spatialSlice'

/**
 * The demo world's switch, in the footer of Administration → Deployment: the demo is content of the
 * installation — cameras in VmsCfg, the world, its landmarks and calibrations in VmsMap, and the
 * video wall — not a property of a map. Installing twice is safe — each side owns what it wrote and
 * leaves everything else alone — so the same button reinstalls once the cameras exist.
 */
export default function DemoControl() {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const [installed, setInstalled] = useState(false)
  const [loaded, setLoaded] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [report, setReport] = useState<MapDemoReport | null>(null)

  useEffect(() => {
    void mapApi.getDemoStatus()
      .then((status) => setInstalled(!!status.installed))
      .catch((e) => setError(errorText(e)))
      .finally(() => setLoaded(true))
  }, [])

  const run = async (action: 'install' | 'remove') => {
    const question = action === 'install' ? t('deployment.demo.confirmInstall') : t('deployment.demo.confirmRemove')
    if (!window.confirm(question)) return

    setBusy(true)
    setError(null)
    try {
      const result = action === 'install'
        ? await mapApi.installDemo()
        : await mapApi.removeDemo()

      setReport(result)
      setInstalled(!!result.installed)
      // The tree behind this footer changed: the lists have to show it.
      void dispatch(loadSpaces())
      void dispatch(loadPlacements())
    } catch (e) {
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  if (!loaded) return null

  return (
    <Stack direction="row" alignItems="center" spacing={1}>
      {error ? <Typography variant="caption" color="error">{error}</Typography> : null}

      <Button
        size="small"
        startIcon={busy ? <CircularProgress size={16} /> : installed ? <DeleteSweepIcon fontSize="small" /> : <CloudDownloadIcon fontSize="small" />}
        disabled={busy}
        color={installed ? 'inherit' : 'primary'}
        onClick={() => void run(installed ? 'remove' : 'install')}
      >
        {installed ? t('deployment.demo.remove') : t('deployment.demo.install')}
      </Button>

      <Dialog open={!!report} onClose={() => setReport(null)} maxWidth="sm" fullWidth>
        <DialogTitle>{report?.installed ? t('deployment.demo.installed') : t('deployment.demo.removed')}</DialogTitle>
        <DialogContent>
          <Stack spacing={1}>
            <Typography variant="body2">
              {t('deployment.demo.summary', {
                created: report?.created ?? 0,
                updated: report?.updated ?? 0,
                removed: report?.removed ?? 0,
              })}
            </Typography>

            <Typography variant="body2">
              {report?.monitorCameras?.length
                ? t('deployment.demo.wall', { cameras: report.monitorCameras.join(', ') })
                : t('deployment.demo.noWall')}
            </Typography>

            {report?.skippedCameras?.length ? (
              <Alert severity="info">
                <Typography variant="subtitle2">{t('deployment.demo.skippedTitle')}</Typography>
                <Typography variant="body2">{report.skippedCameras.join(', ')}</Typography>
                <Typography variant="caption" color="text.secondary">{t('deployment.demo.skippedHint')}</Typography>
              </Alert>
            ) : null}

            {report?.wallError ? (
              <Alert severity="warning">
                <Typography variant="subtitle2">{t('deployment.demo.wallErrorTitle')}</Typography>
                <Typography variant="body2">{t('deployment.demo.wallErrorHint')}</Typography>
                <Typography variant="caption" color="text.secondary">{report.wallError}</Typography>
              </Alert>
            ) : null}
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setReport(null)}>{t('common.close')}</Button>
        </DialogActions>
      </Dialog>
    </Stack>
  )
}
