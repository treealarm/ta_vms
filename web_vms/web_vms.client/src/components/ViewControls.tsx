import { Button, Stack } from '@mui/material'
import { useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import { LangToggle } from '../App'

export default function ViewControls({ target }: { target: 'admin' | 'operator' }) {
  const { t } = useTranslation()
  const navigate = useNavigate()

  return (
    <Stack direction="row" alignItems="center" spacing={0.5}>
      <Button
        size="small"
        variant="text"
        onClick={() => navigate(target === 'admin' ? '/admin' : '/operator')}
        sx={{ minWidth: 0, px: 1, py: 0.5, fontSize: '0.7rem', color: 'text.secondary' }}
      >
        {t(target === 'admin' ? 'nav.goAdmin' : 'nav.goOperator')}
      </Button>
      <LangToggle />
    </Stack>
  )
}
