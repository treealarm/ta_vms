import { useEffect, useState } from 'react'
import {
  Alert, Box, Button, Chip, Dialog, DialogActions, DialogContent, DialogContentText, DialogTitle,
  FormControlLabel, List, Stack, Switch, TextField, Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import {
  downloadNodePackage, loadDeployment, loadNodes, removeNode, setEnrollmentOpen,
} from '../store/deploymentSlice'
import type { DeploymentNodeDto } from '../dto/dtos'
import DetailToolbar from './settings/DetailToolbar'
import MasterDetailLayout from './settings/MasterDetailLayout'
import SettingsListItem from './settings/SettingsListItem'
import type { ApplyIfDirtyHandle } from './settings/useDirtyGuard'
import { useMasterDetailSelection } from './settings/useMasterDetailSelection'

function StatusAlert() {
  const status = useAppSelector((s) => s.deployment.status)
  if (status === 'Ready') return null
  return <Alert severity={status.startsWith('Error') ? 'error' : 'success'}>{status}</Alert>
}

function NodeDetail({ node, onRemoved }: { node: DeploymentNodeDto; onRemoved: () => void }) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const isBusy = useAppSelector((s) => s.deployment.isBusy)

  const handleRemove = async () => {
    const result = await dispatch(removeNode(node.serverId))
    // A refusal keeps the node on screen, with VmsCfg's reason above it.
    if (removeNode.rejected.match(result)) return
    await dispatch(loadNodes())
    onRemoved()
  }

  const row = (label: string, value: React.ReactNode) => (
    <Stack direction="row" spacing={2}>
      <Typography variant="body2" color="text.secondary" sx={{ width: 140, flexShrink: 0 }}>{label}</Typography>
      <Typography variant="body2" component="div">{value}</Typography>
    </Stack>
  )

  return (
    <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      <DetailToolbar
        title={node.name}
        subtitle={node.serverId}
        dirty={false}
        busy={isBusy}
        hideApply
        onDelete={node.configured ? undefined : () => void handleRemove()}
        deleteLabel={t('deployment.removeNode')}
        extra={<Chip size="small" label={node.status} color={node.status === 'online' ? 'success' : 'default'} />}
      />
      <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', p: 2 }}>
        <Stack spacing={1.5} sx={{ maxWidth: 520 }}>
          <StatusAlert />
          {row(t('deployment.serverId'), node.serverId)}
          {row(t('deployment.address'), node.address || '—')}
          {row(t('deployment.mediaMtx'), node.mediaMtx ? t('deployment.yes') : t('deployment.no'))}
          {!node.configured && row(t('deployment.enrolledAt'), new Date(node.enrolledAt).toLocaleString())}
          {node.configured && <Alert severity="info">{t('deployment.configuredHelper')}</Alert>}
          {!node.configured && (
            <Typography variant="caption" color="text.secondary">{t('deployment.removeHelper')}</Typography>
          )}
        </Stack>
      </Box>
    </Box>
  )
}

// "+": a node that cannot reach this web by itself, or is installed with nobody at it, joins with a
// package made here instead.
function NewNodeForm() {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const isBusy = useAppSelector((s) => s.deployment.isBusy)
  const [name, setName] = useState('')
  const [address, setAddress] = useState('')
  const [mediaMtx, setMediaMtx] = useState(true)

  const valid = /^[a-z0-9]([a-z0-9-]*[a-z0-9])?$/.test(name) && name !== 'core'
    && /^\d{1,3}(\.\d{1,3}){3}$/.test(address)

  const handleDownload = async () => {
    const result = await dispatch(downloadNodePackage({ name, address, mediaMtx }))
    if (!downloadNodePackage.rejected.match(result)) await dispatch(loadNodes())
  }

  return (
    <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      <DetailToolbar
        title={t('deployment.newNode')}
        dirty={valid}
        busy={isBusy}
        applyLabel={t('deployment.downloadPackage')}
        onApply={() => void handleDownload()}
      />
      <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', p: 2 }}>
        <Stack spacing={2} sx={{ maxWidth: 420 }}>
          <StatusAlert />
          <Typography variant="body2" color="text.secondary">{t('deployment.newNodeHelper')}</Typography>
          <TextField label={t('common.name')} value={name} size="small" required
            onChange={(e) => setName(e.target.value.toLowerCase())} helperText={t('deployment.nameHelper')} />
          <TextField label={t('deployment.address')} value={address} size="small" required placeholder="192.168.1.20"
            onChange={(e) => setAddress(e.target.value.trim())} helperText={t('deployment.addressHelper')} />
          <FormControlLabel
            control={<Switch checked={mediaMtx} onChange={(e) => setMediaMtx(e.target.checked)} />}
            label={t('deployment.nodeMediaMtx')}
          />
          <Alert severity="warning">{t('deployment.packageWarning')}</Alert>
        </Stack>
      </Box>
    </Box>
  )
}

// Changes what joining means for every machine, so it belongs to the page, not to a node.
function EnrollmentSwitch() {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const open = useAppSelector((s) => s.deployment.deployment?.enrollmentOpen ?? false)
  const [confirming, setConfirming] = useState(false)

  const apply = async (next: boolean) => {
    const result = await dispatch(setEnrollmentOpen(next))
    if (!setEnrollmentOpen.rejected.match(result)) await dispatch(loadDeployment())
  }

  return (
    <Box sx={{ px: 1 }}>
      <FormControlLabel
        control={(
          <Switch
            checked={open}
            // Opening lets any machine on the network in, so it asks first; closing only narrows.
            onChange={(e) => (e.target.checked ? setConfirming(true) : void apply(false))}
          />
        )}
        label={t('deployment.enrollmentOpen')}
      />
      <Typography variant="caption" color="text.secondary" display="block">
        {open ? t('deployment.enrollmentOpenHelper') : t('deployment.enrollmentClosedHelper')}
      </Typography>

      <Dialog open={confirming} onClose={() => setConfirming(false)}>
        <DialogTitle>{t('deployment.enrollmentConfirm')}</DialogTitle>
        <DialogContent>
          <DialogContentText>{t('deployment.enrollmentOpenWarning')}</DialogContentText>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setConfirming(false)}>{t('common.cancel')}</Button>
          <Button color="warning" variant="contained" onClick={() => { setConfirming(false); void apply(true) }}>
            {t('common.apply')}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  )
}

export default function DeploymentPanel({ ref }: { ref?: React.Ref<ApplyIfDirtyHandle> }) {
  const dispatch = useAppDispatch()
  const { t } = useTranslation()
  const { deployment, nodes } = useAppSelector((s) => s.deployment)

  // Nothing here is edited in place, so there is no form to apply before switching.
  const { isNew, selected, detailKey, select, selectNew, reset } =
    useMasterDetailSelection(ref, nodes, (n) => n.serverId)

  useEffect(() => {
    void dispatch(loadDeployment())
    void dispatch(loadNodes())
  }, [dispatch])

  const list = (
    <List dense disablePadding>
      {nodes.map((n) => (
        <SettingsListItem
          key={n.serverId}
          selected={selected?.serverId === n.serverId}
          onClick={() => select(n.serverId)}
          primary={n.name}
          secondary={`${n.address || n.serverId} · ${n.status}`}
        />
      ))}
    </List>
  )

  return (
    <MasterDetailLayout
      listTitle={t('deployment.title')}
      onAdd={deployment?.distributed ? selectNew : undefined}
      addTooltip={t('deployment.addNode')}
      list={list}
      footer={deployment?.distributed ? <EnrollmentSwitch /> : undefined}
    >
      <Box key={detailKey} sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
        {isNew ? (
          <NewNodeForm />
        ) : selected ? (
          <NodeDetail node={selected} onRemoved={reset} />
        ) : (
          <Typography color="text.secondary" sx={{ p: 2 }}>
            {deployment && !deployment.distributed ? t('deployment.notDistributed') : t('deployment.selectHint')}
          </Typography>
        )}
      </Box>
    </MasterDetailLayout>
  )
}
