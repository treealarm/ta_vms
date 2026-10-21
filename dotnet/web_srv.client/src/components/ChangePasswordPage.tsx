import { useState } from 'react'
import type { FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { Alert, Box, Button, Paper, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { decodeRoles, logout, setTokens, PASSWORD_CHANGE_REQUIRED } from '../store/authSlice'
import PasswordField from './PasswordField'

// The server's own rule (AuthController.MinPasswordLength), checked here first so the common mistake
// is answered in the user's language without a round trip.
const MIN_PASSWORD_LENGTH = 8

/**
 * Sets a new password. Reached on purpose from the view controls, and forced on an account that
 * holds password-change-required — the initial admin/admin of a standalone install — which the
 * server lets do nothing else until this succeeds.
 */
export default function ChangePasswordPage() {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const navigate = useNavigate()
  const token = useAppSelector((s) => s.auth.token)
  const forced = decodeRoles(token).includes(PASSWORD_CHANGE_REQUIRED)
  const [current, setCurrent] = useState('')
  const [next, setNext] = useState('')
  const [repeat, setRepeat] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    if (next.length < MIN_PASSWORD_LENGTH) return setError(t('auth.changePassword.tooShort', { min: MIN_PASSWORD_LENGTH }))
    if (next !== repeat) return setError(t('auth.changePassword.mismatch'))

    setBusy(true)
    try {
      const response = await fetch('/api/auth/change-password', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ currentPassword: current, newPassword: next }),
      })
      if (!response.ok) {
        const body = (await response.json().catch(() => null)) as { code?: string; error?: string } | null
        const known = body?.code ? `auth.changePassword.errors.${body.code}` : null
        setError(known ? t(known, { defaultValue: body?.error ?? response.statusText }) : (body?.error ?? response.statusText))
        return
      }
      const data = (await response.json()) as { access_token: string; refresh_token: string }
      dispatch(setTokens({ token: data.access_token, refreshToken: data.refresh_token }))
      navigate(decodeRoles(data.access_token).includes('admin') ? '/admin' : '/operator/monitors', { replace: true })
    } finally {
      setBusy(false)
    }
  }

  return (
    <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100dvh' }}>
      <Paper component="form" onSubmit={handleSubmit} sx={{ p: 4, width: 360, display: 'flex', flexDirection: 'column', gap: 2 }}>
        <Typography variant="h6">{t('auth.changePassword.title')}</Typography>
        {forced && <Alert severity="info">{t('auth.changePassword.forced')}</Alert>}
        {error && <Alert severity="error">{error}</Alert>}
        <PasswordField pass={current} setPass={setCurrent} label={t('auth.changePassword.current')} />
        <PasswordField pass={next} setPass={setNext} label={t('auth.changePassword.new')}
          helperText={t('auth.changePassword.rule', { min: MIN_PASSWORD_LENGTH })} />
        <PasswordField pass={repeat} setPass={setRepeat} label={t('auth.changePassword.repeat')} />
        <Button type="submit" variant="contained" fullWidth disabled={busy || !current || !next || !repeat}>
          {t('auth.changePassword.submit')}
        </Button>
        {forced ? (
          <Button variant="text" size="small" onClick={() => { dispatch(logout()); navigate('/login', { replace: true }) }}>
            {t('nav.logout')}
          </Button>
        ) : (
          <Button variant="text" size="small" onClick={() => navigate(-1)}>{t('common.cancel')}</Button>
        )}
      </Paper>
    </Box>
  )
}
