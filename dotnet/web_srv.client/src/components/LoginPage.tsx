import { useState } from 'react'
import type { FormEvent } from 'react'
import { useLocation, useNavigate } from 'react-router-dom'
import { Alert, Box, Button, Paper, TextField, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { decodeRoles, login, PASSWORD_CHANGE_REQUIRED } from '../store/authSlice'
import PasswordField from './PasswordField'

export default function LoginPage() {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const navigate = useNavigate()
  const location = useLocation()
  const loading = useAppSelector((s) => s.auth.loading)
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState(false)

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setError(false)
    const result = await dispatch(login({ username, password }))
    if (login.fulfilled.match(result)) {
      const from = (location.state as { from?: string } | null)?.from
      const roles = decodeRoles(result.payload.token)
      if (roles.includes(PASSWORD_CHANGE_REQUIRED)) {
        navigate('/change-password', { replace: true })
        return
      }
      navigate(from ?? (roles.includes('admin') ? '/admin' : '/operator/monitors'), { replace: true })
    } else {
      setError(true)
    }
  }

  return (
    <Box sx={{ display: 'flex', alignItems: 'center', justifyContent: 'center', height: '100dvh' }}>
      <Paper component="form" onSubmit={handleSubmit} sx={{ p: 4, width: 320, display: 'flex', flexDirection: 'column', gap: 2 }}>
        <Typography variant="h6">{t('auth.title')}</Typography>
        {error && <Alert severity="error">{t('auth.invalidCredentials')}</Alert>}
        <TextField
          label={t('auth.username')}
          value={username}
          onChange={(e) => setUsername(e.target.value)}
          fullWidth
          size="small"
          autoFocus
        />
        <PasswordField pass={password} setPass={setPassword} />
        <Button type="submit" variant="contained" fullWidth disabled={loading}>
          {loading ? t('auth.signingIn') : t('auth.signIn')}
        </Button>
      </Paper>
    </Box>
  )
}
