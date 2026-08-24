import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  Alert, Box, CircularProgress, IconButton, Stack, Switch, Tooltip, Typography,
} from '@mui/material'
import OpenInNewIcon from '@mui/icons-material/OpenInNew'
import { useTranslation } from 'react-i18next'
import { apiFetch } from '../api/apiFetch'
import { invalidateAnalyticsSources, useCameraAnalyticsSources } from '../hooks/useCameraAnalyticsSources'
import { usePendingEdits } from './settings/PendingEdits'

type Props = {
  cameraId: string
  /** Owned by the camera form, so it saves with Apply like every other camera field. The other
   *  switches here act on other resources and take effect immediately — the same split
   *  CameraStoragesControl and CameraSchedulesControl already use on the Main tab. */
  onvifMetadataDisabled: boolean
  setOnvifMetadataDisabled: (v: boolean) => void
}

/** One source's row: a switch, a line saying what it is doing, and a way to its own settings. */
function SourceRow({ label, checked, disabled, busy, detail, note, linkTo, linkTitle, onToggle }: {
  label: string
  checked: boolean
  disabled?: boolean
  busy?: boolean
  detail: React.ReactNode
  /** A caveat that is true of this source itself, shown only while it is on. */
  note?: React.ReactNode
  linkTo: string
  linkTitle: string
  onToggle: (v: boolean) => void
}) {
  const applyPending = usePendingEdits()
  const navigate = useNavigate()

  // Leaving for another settings page is routing, so the camera form's unsaved edits have to be
  // offered a save first -- the same guard a tab switch goes through.
  const go = async () => { if (await applyPending()) navigate(linkTo) }

  return (
    <Stack direction="row" spacing={1} alignItems="flex-start">
      <Switch
        size="small"
        checked={checked}
        disabled={disabled || busy}
        onChange={(e) => onToggle(e.target.checked)}
        sx={{ mt: -0.25 }}
      />
      <Box sx={{ flex: 1, minWidth: 0 }}>
        <Typography variant="body2">{label}</Typography>
        <Typography variant="caption" color="text.secondary" component="div">
          {busy ? <CircularProgress size={12} /> : detail}
        </Typography>
        {note && checked && !busy && (
          <Typography variant="caption" color="warning.main" component="div">
            {note}
          </Typography>
        )}
      </Box>
      <Tooltip title={linkTitle}>
        <IconButton size="small" onClick={() => void go()}>
          <OpenInNewIcon fontSize="small" />
        </IconButton>
      </Tooltip>
    </Stack>
  )
}

/**
 * Which of this camera's detection sources are currently live.
 *
 * The sources themselves are configured elsewhere -- the CV service on the Analytics page, the
 * device's own metadata under ONVIF Cfg, the event subscription under ONVIF Events -- and those are
 * genuinely separate things, so they stay there. What belongs to the camera is which of them are
 * switched on right now, which is the one question you cannot answer without visiting all three.
 *
 * Arming is deliberately absent: it is not a property of a source, and it is set from the monitor,
 * where it sits next to the video an operator is judging when they decide to arm.
 */
export default function CameraDetectionsSection({ cameraId, onvifMetadataDisabled, setOnvifMetadataDisabled }: Props) {
  const { t } = useTranslation()
  const sources = useCameraAnalyticsSources(cameraId)
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  const run = async (key: string, fn: () => Promise<unknown>) => {
    setBusy(key); setError(null)
    try {
      await fn()
      invalidateAnalyticsSources(cameraId)
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e))
    } finally {
      setBusy(null)
    }
  }

  if (!sources) {
    return <Typography variant="caption" color="text.secondary">{t('common.loading')}</Typography>
  }

  const { cv, onvifMetadata, onvifEvents } = sources

  const toggleCv = (enabled: boolean) => void run('cv', () => apiFetch('/api/analytics-watches', {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    // The whole watch goes back, not a patch: the endpoint is an upsert of the record as a whole.
    body: JSON.stringify({ cameraId, ...cv, enabled }),
  }))

  const toggleEvents = (subscribed: boolean) => void run('events', () =>
    apiFetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/subscription`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ subscribed }),
    }))

  const cvDetail = cv === null
    ? t('detections.cvNotConfigured')
    : cv.classes.length > 0
      ? `${cv.classes.map((c) => t(`analytics.class.${c}`)).join(', ')} · ${cv.sampleFps} fps`
      : t('detections.cvNoClasses')

  const metadataDetail = onvifMetadata.deviceEmits === null
    ? t('detections.deviceUnknown')
    : onvifMetadata.deviceEmits
      ? t('detections.deviceEmits')
      : t('detections.deviceSilent')

  const eventsDetail = !onvifEvents.managed
    ? t('detections.eventsNotManaged')
    : t('detections.eventsFilters', { count: onvifEvents.filterCount })

  return (
    <Stack spacing={2}>
      <Typography variant="caption" color="text.secondary">{t('detections.intro')}</Typography>

      <Stack spacing={1.5}>
        <SourceRow
          label={t('detections.sourceCv')}
          checked={cv?.enabled === true}
          disabled={cv === null}
          busy={busy === 'cv'}
          detail={cvDetail}
          linkTo={`/admin/analytics?camera=${encodeURIComponent(cameraId)}`}
          linkTitle={t('detections.openAnalytics')}
          onToggle={toggleCv}
        />
        <SourceRow
          label={t('detections.sourceOnvifBoxes')}
          checked={!onvifMetadataDisabled}
          detail={metadataDetail}
          // A property of this source, not of arming: its boxes are drawn and archived like any
          // other, but they are the one kind that never opens an episode.
          note={t('detections.onvifBoxesDoNotRetain')}
          linkTo={`/admin/onvif-settings?camera=${encodeURIComponent(cameraId)}&tab=metadata`}
          linkTitle={t('detections.openOnvifCfg')}
          // Inverted on purpose: the stored flag says what we drop, the switch says what we use.
          onToggle={(v) => setOnvifMetadataDisabled(!v)}
        />
        <SourceRow
          label={t('detections.sourceOnvifEvents')}
          checked={onvifEvents.subscribed}
          disabled={!onvifEvents.managed}
          busy={busy === 'events'}
          detail={eventsDetail}
          linkTo={`/admin/onvif?camera=${encodeURIComponent(cameraId)}`}
          linkTitle={t('detections.openOnvifEvents')}
          onToggle={toggleEvents}
        />
      </Stack>

      {error && <Alert severity="error" onClose={() => setError(null)}>{error}</Alert>}
    </Stack>
  )
}
