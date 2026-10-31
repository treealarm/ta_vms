import { useEffect, useMemo } from 'react'
import {
  Alert, Box, Checkbox, Chip, Stack, Table, TableBody, TableCell, TableHead, TableRow, Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../../store'
import {
  grantRight, loadNodeRights, loadRightActions, loadRightSubjects, loadRightsMode, revokeRight,
} from '../../store/rightsSlice'
import DetailToolbar from '../settings/DetailToolbar'

/**
 * The rights of one tree node: a row per subject and action. They inherit downwards, so a right
 * on a building covers its floors and their cameras; an administrator has everything without a
 * row. Writing is immediate — the server is the one that expands the set, and there is nothing to
 * batch about one checkbox.
 */
export default function ObjectRights({ nodeId }: { nodeId: string }) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const marker = useAppSelector((s) => s.objects.markers[nodeId])
  const state = useAppSelector((s) => s.rights)

  useEffect(() => {
    void dispatch(loadNodeRights(nodeId))
  }, [nodeId, dispatch])

  useEffect(() => {
    if (Object.keys(state.actionsByKind).length === 0) void dispatch(loadRightActions())
  }, [state.actionsByKind, dispatch])

  useEffect(() => {
    if (state.users.length === 0) void dispatch(loadRightSubjects())
  }, [state.users.length, dispatch])

  useEffect(() => {
    void dispatch(loadRightsMode())
  }, [dispatch])

  const kind = marker?.nodeKind ?? 'object'
  const actions = state.actionsByKind[kind] ?? []
  const rows = state.nodeId === nodeId ? state.rows : []

  // Roles first — a right given to a role covers every holder of it — then the user names.
  const subjects = useMemo(() => {
    const roles = new Set<string>()
    for (const user of state.users) for (const role of user.roles ?? []) roles.add(role)

    return [
      ...[...roles].sort((a, b) => a.localeCompare(b)).map((role) => ({ subject: role, kind: 'role' as const })),
      ...state.users.map((user) => ({ subject: user.username, kind: 'user' as const })),
    ]
  }, [state.users])

  const granted = (subject: string, action: string) =>
    rows.some((row) => row.subject === subject && row.action === action)

  const toggle = (subject: string, action: string, on: boolean) => {
    if (on) void dispatch(grantRight({ objectId: nodeId, subject, action }))
    else void dispatch(revokeRight({ objectId: nodeId, subject, action }))
  }

  return (
    <Stack sx={{ flex: 1, minHeight: 0 }}>
      <DetailToolbar
        title={t('rights.title', { name: marker?.name || nodeId })}
        subtitle={t(`maps.node.kind.${kind}`)}
        dirty={false}
        hideApply
      />

      <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', p: 1.5 }}>
        <Stack direction="row" spacing={1} alignItems="center" sx={{ mb: 1 }}>
          <Chip
            size="small"
            variant="outlined"
            label={state.mode === 'RIGHTS_MODE_ENFORCED' ? t('rights.modeEnforced') : t('rights.modeOpen')}
          />
          <Typography variant="caption" color="text.secondary">{t('rights.hint')}</Typography>
        </Stack>

        {state.error ? <Alert severity="error" sx={{ mb: 1 }}>{state.error}</Alert> : null}

        <Box sx={{ overflowX: 'auto' }}>
          <Table size="small">
            <TableHead>
              <TableRow>
                <TableCell>{t('rights.subject')}</TableCell>
                {actions.map((action) => (
                  <TableCell key={action} align="center" sx={{ whiteSpace: 'nowrap' }}>
                    {t(`rights.action.${action}`, { defaultValue: action })}
                  </TableCell>
                ))}
              </TableRow>
            </TableHead>
            <TableBody>
              {subjects.map(({ subject, kind: subjectKind }) => (
                <TableRow key={`${subjectKind}:${subject}`} hover>
                  <TableCell>
                    <Stack direction="row" spacing={0.75} alignItems="baseline">
                      <span>{subject}</span>
                      <Typography component="span" variant="caption" color="text.disabled">
                        {subjectKind === 'role' ? t('rights.role') : t('rights.user')}
                      </Typography>
                    </Stack>
                  </TableCell>
                  {actions.map((action) => (
                    <TableCell key={action} align="center" padding="none">
                      <Checkbox
                        size="small"
                        checked={granted(subject, action)}
                        onChange={(event) => toggle(subject, action, event.target.checked)}
                      />
                    </TableCell>
                  ))}
                </TableRow>
              ))}
              {subjects.length === 0 ? (
                <TableRow>
                  <TableCell colSpan={actions.length + 1}>
                    <Typography variant="body2" color="text.secondary">{t('rights.noSubjects')}</Typography>
                  </TableCell>
                </TableRow>
              ) : null}
            </TableBody>
          </Table>
        </Box>
      </Box>
    </Stack>
  )
}
