import { Alert } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppSelector } from '../store'

/**
 * The canvas was read cut: the server stopped at its limit, so what is drawn is a prefix of it,
 * not the whole canvas. Said beside the canvas itself, because a clipped canvas otherwise looks
 * exactly like a small one. Not dismissible -- it is a property of the current read, and hiding
 * it would let the page pass a partial canvas for a whole one again.
 */
export default function CanvasTruncatedNotice({ canvasId }: { canvasId?: string | null }) {
  const { t } = useTranslation()
  const truncated = useAppSelector((s) => (canvasId ? s.objects.canvases[canvasId]?.truncated : false))

  if (!truncated) return null
  return <Alert severity="warning" sx={{ borderRadius: 0 }}>{t('maps.canvas.truncated')}</Alert>
}
