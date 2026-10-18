import { IconButton, Tooltip } from '@mui/material'
import ArticleIcon from '@mui/icons-material/Article'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { toggleLogPanel } from '../store/logSlice'

export default function LogToggleButton() {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const open = useAppSelector((s) => s.logs.panelOpen)

  return (
    <Tooltip title={t('logs.title')} placement="right">
      <IconButton
        onClick={() => dispatch(toggleLogPanel())}
        sx={{ color: open ? 'primary.main' : 'text.secondary' }}
      >
        <ArticleIcon />
      </IconButton>
    </Tooltip>
  )
}
