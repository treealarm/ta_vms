import { Button, Stack } from '@mui/material'
import { useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { LangToggle } from '../App'
import { useAppDispatch, useAppSelector } from '../store'
import { decodeRoles, logout } from '../store/authSlice'
import { usePendingEdits } from './settings/PendingEdits'

export default function ViewControls({ target }: { target: 'admin' | 'operator' }) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const dispatch = useAppDispatch()
  const token = useAppSelector((s) => s.auth.token)
  const applyPending = usePendingEdits()

  // Same rule the /admin route and the server's admin policy apply, so the link is offered only
  // where it leads somewhere: without a token there is no Keycloak to ask and everything is
  // allowed, exactly as RequireRole treats it. Cosmetic — the route guard and the API still decide.
  const mayAdminister = !token || decodeRoles(token).includes('admin')

  return (
    <Stack direction="row" alignItems="center" spacing={0.5}>
      {(target === 'operator' || mayAdminister) && (
        <Button
          size="small"
          variant="text"
          onClick={() => void (async () => {
            // A settings form left dirty is saved on the way out, the same as when the tab or the
            // selection changes. A failed save keeps us here with the error on screen.
            if (!(await applyPending())) return
            navigate(target === 'admin' ? '/admin' : '/operator')
          })()}
          sx={{ minWidth: 0, px: 1, py: 0.5, fontSize: '0.7rem', color: 'text.secondary' }}
        >
          {t(target === 'admin' ? 'nav.goAdmin' : 'nav.goOperator')}
        </Button>
      )}
      <LangToggle />
      {token && (
        <Button
          size="small"
          variant="text"
          onClick={() => void (async () => {
            if (!(await applyPending())) return
            dispatch(logout())
            navigate('/login')
          })()}
          sx={{ minWidth: 0, px: 1, py: 0.5, fontSize: '0.7rem', color: 'text.secondary' }}
        >
          {t('nav.logout')}
        </Button>
      )}
    </Stack>
  )
}
