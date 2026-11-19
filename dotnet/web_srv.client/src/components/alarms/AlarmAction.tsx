import { useState } from 'react'
import {
  Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, Radio,
  RadioGroup, TextField, Tooltip, Typography,
} from '@mui/material'
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutline'
import { useTranslation } from 'react-i18next'
import type { MapAlarm } from '../../dto/mapDtos'
import { useAppDispatch } from '../../store'
import { acknowledgeAlarm, closeAlarm } from '../../store/alarmsSlice'
import { errorText } from '../../api/requestJson'
import {
  acknowledgeNeedsComment, ackBlockedKey, canCloseAlarm, closeNeedsComment,
} from './alarmActions'

type CloseReason = 'false_alarm' | 'other'

/**
 * The one acknowledgement control: a labelled button that says why it is unavailable, and the
 * dialog behind it. The map's alarm list, the object panel and an event's details all show this
 * exact control, so "accept" reads the same wherever the operator meets it.
 */
export function AlarmActionButton({ alarm, onDone }: { alarm: MapAlarm; onDone?: () => void }) {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)
  const blocked = ackBlockedKey(alarm)

  return (
    <>
      <Tooltip title={blocked ? t(blocked) : t('alarms.acknowledgeHint')}>
        <span>
          <Button
            size="small"
            startIcon={<CheckCircleOutlineIcon fontSize="small" />}
            disabled={!!blocked}
            onClick={(event) => { event.stopPropagation(); setOpen(true) }}
          >
            {t('alarms.acknowledge')}
          </Button>
        </span>
      </Tooltip>
      {open ? (
        <AlarmHandleDialog alarm={alarm} onClose={() => setOpen(false)} onDone={onDone} />
      ) : null}
    </>
  )
}

/**
 * The comment-and-reason dialog. A live condition on an armed node needs a comment, and so does a
 * close of a part its device no longer reports; everything else keeps the comment optional.
 */
export function AlarmHandleDialog({ alarm, onClose, onDone }: {
  alarm: MapAlarm
  onClose: () => void
  onDone?: () => void
}) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const [action, setAction] = useState<'acknowledge' | 'close'>('acknowledge')
  const [comment, setComment] = useState('')
  const [reason, setReason] = useState<CloseReason>('false_alarm')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const requiresComment =
    action === 'acknowledge' ? acknowledgeNeedsComment(alarm) : closeNeedsComment(alarm)
  const missingComment = requiresComment && comment.trim() === ''

  const submit = async () => {
    if (!alarm.id) return

    setBusy(true)
    setError(null)
    try {
      if (action === 'close') {
        await dispatch(closeAlarm({ id: alarm.id, reason, comment })).unwrap()
      } else {
        await dispatch(acknowledgeAlarm({ id: alarm.id, comment })).unwrap()
      }
      onDone?.()
      onClose()
    } catch (e) {
      // The server's own sentence: a missing comment, a closed row, a lost right.
      setError(errorText(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Dialog open onClose={onClose} maxWidth="xs" fullWidth>
      <DialogTitle>{t('alarms.handle')}</DialogTitle>
      <DialogContent>
        <Typography variant="body2" sx={{ mb: 2 }}>
          {alarm.objectName || alarm.objectId} · {t(`alarms.status.${alarm.status || 'active'}`)}
        </Typography>

        {alarm.deviceAbsent ? (
          <Alert severity="warning" sx={{ mb: 2 }}>{t('alarms.deviceAbsent')}</Alert>
        ) : null}

        {action === 'close' ? (
          <RadioGroup
            row
            value={reason}
            onChange={(_, value) => setReason(value as CloseReason)}
          >
            <FormControlLabel value="false_alarm" control={<Radio size="small" />} label={t('alarms.reason.falseAlarm')} />
            <FormControlLabel value="other" control={<Radio size="small" />} label={t('alarms.reason.other')} />
          </RadioGroup>
        ) : null}

        <TextField
          multiline
          minRows={2}
          fullWidth
          required={requiresComment}
          label={requiresComment ? t('alarms.commentRequired') : t('alarms.comment')}
          value={comment}
          onChange={(event) => setComment(event.target.value)}
          sx={{ mt: action === 'close' ? 2 : 0 }}
        />

        {error ? <Alert severity="error" sx={{ mt: 2 }}>{error}</Alert> : null}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>{t('common.cancel')}</Button>
        {action === 'acknowledge' && canCloseAlarm(alarm) ? (
          <Button onClick={() => setAction('close')}>{t('alarms.close')}</Button>
        ) : null}
        <Button
          variant="contained"
          disabled={busy || missingComment}
          onClick={() => void submit()}
        >
          {action === 'close' ? t('alarms.close') : t('alarms.acknowledge')}
        </Button>
      </DialogActions>
    </Dialog>
  )
}
