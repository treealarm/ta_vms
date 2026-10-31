import { useEffect, useState } from 'react'
import {
  Box, Button, Dialog, DialogActions, DialogContent, DialogContentText, DialogTitle,
  FormControlLabel, Switch, Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../../store'
import { loadRightsMode, saveRightsMode } from '../../store/rightsSlice'

/**
 * The one open/enforced switch of the whole deployment: in the open, a node with no rights is
 * visible to everyone; enforced, only the subjects with a row reach it. It changes what every
 * page means, so it sits in the page footer of Admin → Maps, not beside one node.
 */
export default function RightsModeSwitch() {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const mode = useAppSelector((s) => s.rights.mode)
  const [confirming, setConfirming] = useState(false)

  useEffect(() => {
    void dispatch(loadRightsMode())
  }, [dispatch])

  const enforced = mode === 'RIGHTS_MODE_ENFORCED'

  return (
    <Box sx={{ px: 1 }}>
      <FormControlLabel
        control={(
          <Switch
            checked={enforced}
            // Turning it on can lock everyone out at once, so it asks first; turning it off only
            // widens access and needs no ceremony.
            onChange={(event) => (event.target.checked ? setConfirming(true)
              : void dispatch(saveRightsMode('RIGHTS_MODE_OPEN')))}
          />
        )}
        label={t('rights.enforce')}
      />
      <Typography variant="caption" color="text.secondary" display="block">
        {enforced ? t('rights.enforceOnHelper') : t('rights.enforceOffHelper')}
      </Typography>

      <Dialog open={confirming} onClose={() => setConfirming(false)}>
        <DialogTitle>{t('rights.enforceConfirm')}</DialogTitle>
        <DialogContent>
          <DialogContentText>{t('rights.enforceOnWarning')}</DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setConfirming(false)}>{t('common.cancel')}</Button>
          <Button
            color="warning"
            variant="contained"
            onClick={() => { setConfirming(false); void dispatch(saveRightsMode('RIGHTS_MODE_ENFORCED')) }}
          >
            {t('common.apply')}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  )
}
