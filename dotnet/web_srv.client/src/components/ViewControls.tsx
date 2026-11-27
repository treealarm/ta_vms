import { Button, Stack, ToggleButton, ToggleButtonGroup } from '@mui/material'
import { useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { LangToggle } from '../App'
import { useAppDispatch, useAppSelector } from '../store'
import { decodeRoles, logout } from '../store/authSlice'
import { usePendingEdits } from './settings/pendingEditsContext'

type Props = {
  /** The view on screen; the other segment is where a click leads. */
  current: 'admin' | 'operator'
}

export default function ViewControls({ current }: Props) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const dispatch = useAppDispatch()
  const token = useAppSelector((s) => s.auth.token)
  const applyPending = usePendingEdits()

  // Same rule the /admin route and the server's admin policy apply, so the Admin segment is offered
  // only where it leads somewhere: without a token there is no Keycloak to ask and everything is
  // allowed, exactly as RequireRole treats it. Cosmetic — the route guard and the API still decide.
  const mayAdminister = !token || decodeRoles(token).includes('admin')

  const switchView = (view: string | null) => {
    if (!view || view === current) return
    void (async () => {
      // A settings form left dirty is saved on the way out, the same as when the tab or the
      // selection changes. A failed save keeps us here with the error on screen.
      if (!(await applyPending())) return
      navigate(view === 'admin' ? '/admin' : '/operator')
    })()
  }

  const actionSx = { minWidth: 0, px: 1, py: 0.5, fontSize: '0.7rem', color: 'text.secondary' }

  return (
    <Stack direction="row" alignItems="center" spacing={0.5}>
      {mayAdminister && (
        <ToggleButtonGroup
          size="small"
          exclusive
          value={current}
          onChange={(_event, view: string | null) => switchView(view)}
          sx={{
            mr: 0.5,
            '& .MuiToggleButton-root': { px: 1, py: 0.25, fontSize: '0.7rem', lineHeight: 1.4, textTransform: 'none' },
          }}
        >
          <ToggleButton value="operator">{t('nav.goOperator')}</ToggleButton>
          <ToggleButton value="admin">{t('nav.goAdmin')}</ToggleButton>
        </ToggleButtonGroup>
      )}
      <LangToggle />
      {token && (
        <Button
          size="small"
          variant="text"
          onClick={() => navigate('/change-password')}
          sx={actionSx}
        >
          {t('nav.changePassword')}
        </Button>
      )}
      {token && (
        <Button
          size="small"
          variant="text"
          onClick={() => void (async () => {
            if (!(await applyPending())) return
            dispatch(logout())
            navigate('/login')
          })()}
          sx={actionSx}
        >
          {t('nav.logout')}
        </Button>
      )}
    </Stack>
  )
}
