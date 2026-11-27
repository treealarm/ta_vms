import { useCallback, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import {
  Button, Dialog, DialogActions, DialogContent, DialogContentText, DialogTitle,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import { ConfirmContext, type ConfirmOptions } from './confirmContext'

type PendingConfirm = ConfirmOptions & { resolve: (confirmed: boolean) => void }

/**
 * One confirmation dialog for the whole application.
 *
 * Requests are queued: a second action asking while the dialog is open gets its own answer after
 * the first, rather than replacing the visible question and losing a resolver. Cancel is focused
 * first, so Enter or Esc on a fresh dialog does nothing destructive.
 */
export function ConfirmProvider({ children }: { children: ReactNode }) {
  const { t } = useTranslation()
  const [queue, setQueue] = useState<PendingConfirm[]>([])
  const current = queue[0] ?? null
  // The request this render is answering. A fast second click on the same dialog must not
  // resolve it twice and skip the request behind it; the next request is a different object.
  const answering = useRef<PendingConfirm | null>(null)

  const ask = useCallback(
    (options: ConfirmOptions) => new Promise<boolean>((resolve) => {
      setQueue((q) => [...q, { ...options, resolve }])
    }),
    [],
  )

  const finish = (confirmed: boolean) => {
    if (!current || answering.current === current) return
    answering.current = current
    current.resolve(confirmed)
    setQueue((q) => q.slice(1))
  }

  return (
    <ConfirmContext.Provider value={ask}>
      {children}
      <Dialog open={current !== null} onClose={() => finish(false)} maxWidth="xs" fullWidth>
        {current && (
          <>
            <DialogTitle>{current.title}</DialogTitle>
            <DialogContent>
              <DialogContentText component="div">{current.message}</DialogContentText>
            </DialogContent>
            <DialogActions>
              <Button autoFocus onClick={() => finish(false)}>
                {current.cancelLabel ?? t('common.cancel')}
              </Button>
              <Button
                variant="contained"
                color={current.danger === false ? 'primary' : 'error'}
                onClick={() => finish(true)}
              >
                {current.confirmLabel ?? t('common.confirmDelete')}
              </Button>
            </DialogActions>
          </>
        )}
      </Dialog>
    </ConfirmContext.Provider>
  )
}
