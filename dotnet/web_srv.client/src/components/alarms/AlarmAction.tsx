import { useState } from 'react'
import {
  Alert, Button, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, Radio,
  RadioGroup, TextField, Tooltip, Typography,
} from '@mui/material'
import CheckCircleOutlineIcon from '@mui/icons-material/CheckCircleOutline'
import RestartAltIcon from '@mui/icons-material/RestartAlt'
import { useTranslation } from 'react-i18next'
import type { MapAlarm } from '../../dto/mapDtos'
import { useAppDispatch } from '../../store'
import { acknowledgeAlarm, closeAlarm } from '../../store/alarmsSlice'
import { errorText } from '../../api/requestJson'
import {
  acknowledgeNeedsComment, ackBlockedKey, canCloseAlarm, closeNeedsComment, isFault, resetBlockedKey,
} from './alarmActions'

type CloseReason = 'false_alarm' | 'other' | 'reset'

/** A returned fault is settled by a reset; everything else by an acknowledgement. */
const offeredReset = (alarm: MapAlarm) => isFault(alarm) && alarm.status === 'returned'

/**
 * The one acknowledgement control: a labelled button that says why it is unavailable, and the
 * dialog behind it. The map's alarm list, the object panel and an event's details all show this
 * exact control, so "accept" reads the same wherever the operator meets it. A returned fault gets
 * the same control with the other verb — "Reset fault" — because acknowledging it is refused.
 */
export function AlarmActionButton({ alarm, onDone }: { alarm: MapAlarm; onDone?: () => void }) {
  const { t } = useTranslation()
  const [open, setOpen] = useState(false)
  const reset = offeredReset(alarm)
  const blocked = reset ? resetBlockedKey(alarm) : ackBlockedKey(alarm)

  return (
    <>
      <Tooltip title={blocked ? t(blocked) : t(reset ? 'alarms.resetHint' : 'alarms.acknowledgeHint')}>
        <span>
          <Button
            size="small"
            startIcon={reset
              ? <RestartAltIcon fontSize="small" />
              : <CheckCircleOutlineIcon fontSize="small" />}
            disabled={!!blocked}
            onClick={(event) => { event.stopPropagation(); setOpen(true) }}
          >
            {t(reset ? 'alarms.reset' : 'alarms.acknowledge')}
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
 * close of a part its device no longer reports; everything else keeps the comment optional. A
 * returned fault opens straight on its reset: the reason is not the operator's to choose, and the
 * comment is what the record keeps.
 */
export function AlarmHandleDialog({ alarm, onClose, onDone }: {
  alarm: MapAlarm
  onClose: () => void
  onDone?: () => void
}) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const fault = isFault(alarm)
  const reset = offeredReset(alarm)
  const [action, setAction] = useState<'acknowledge' | 'close'>(reset ? 'close' : 'acknowledge')
  const [comment, setComment] = useState('')
  const [reason, setReason] = useState<CloseReason>(reset ? 'reset' : 'false_alarm')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const requiresComment =
    action === 'acknowledge' ? acknowledgeNeedsComment(alarm) : closeNeedsComment(alarm)
  const missingComment = requiresComment && comment.trim() === ''
  // A fault is reset, not closed with a chosen reason; an ordinary alarm closes with one.
  const closeReason: CloseReason = fault ? 'reset' : reason
  const blocked = action === 'acknowledge'
    ? ackBlockedKey(alarm)
    : fault ? resetBlockedKey(alarm) : null

  const submit = async () => {
    if (!alarm.id) return

    setBusy(true)
    setError(null)
    try {
      if (action === 'close') {
        await dispatch(closeAlarm({ id: alarm.id, reason: closeReason, comment })).unwrap()
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
      <DialogTitle>{t(fault ? 'alarms.handleFault' : 'alarms.handle')}</DialogTitle>
      <DialogContent>
        <Typography variant="body2" sx={{ mb: 2 }}>
          {alarm.objectName || alarm.objectId} · {t(`alarms.status.${alarm.status || 'active'}`)}
        </Typography>

        {alarm.deviceAbsent ? (
          <Alert severity="warning" sx={{ mb: 2 }}>{t('alarms.deviceAbsent')}</Alert>
        ) : null}

        {fault && action === 'acknowledge' ? (
          <Alert severity="info" sx={{ mb: 2 }}>{t('alarms.faultStillPresent')}</Alert>
        ) : null}

        {action === 'close' && !fault ? (
          <RadioGroup
            row
            value={reason}
            onChange={(_, value) => setReason(value as CloseReason)}
          >
            <FormControlLabel value="false_alarm" control={<Radio size="small" />} label={t('alarms.reason.falseAlarm')} />
            <FormControlLabel value="other" control={<Radio size="small" />} label={t('alarms.reason.other')} />
          </RadioGroup>
        ) : null}

        {action === 'close' && fault ? (
          <Typography variant="body2" color="text.secondary">{t('alarms.resetHint')}</Typography>
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

        {blocked ? <Alert severity="info" sx={{ mt: 2 }}>{t(blocked)}</Alert> : null}
        {error ? <Alert severity="error" sx={{ mt: 2 }}>{error}</Alert> : null}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>{t('common.cancel')}</Button>
        {action === 'acknowledge' && canCloseAlarm(alarm) ? (
          <Button onClick={() => setAction('close')}>{t('alarms.close')}</Button>
        ) : null}
        <Button
          variant="contained"
          disabled={busy || missingComment || !!blocked}
          onClick={() => void submit()}
        >
          {action === 'close' ? t(fault ? 'alarms.reset' : 'alarms.close') : t('alarms.acknowledge')}
        </Button>
      </DialogActions>
    </Dialog>
  )
}
