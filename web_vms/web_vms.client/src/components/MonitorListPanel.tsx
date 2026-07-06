import { List, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppSelector } from '../store'
import MasterDetailLayout from './settings/MasterDetailLayout'
import MonitorSetupPanel from './MonitorSetupPanel'
import SettingsListItem from './settings/SettingsListItem'
import type { ApplyIfDirtyHandle } from './settings/useDirtyGuard'
import { useMasterDetailSelection } from './settings/useMasterDetailSelection'

export default function MonitorListPanel({ ref }: { ref?: React.Ref<ApplyIfDirtyHandle> }) {
  const { t } = useTranslation()
  const monitors = useAppSelector((s) => s.monitors.monitors)

  const { isNew, selected, detailRef, detailKey, select, selectNew, reset, setId } =
    useMasterDetailSelection(ref, monitors, (m) => m.monitorId)

  const list = (
    <List dense disablePadding>
      {monitors.map((m) => (
        <SettingsListItem
          key={m.monitorId}
          selected={selected?.monitorId === m.monitorId}
          onClick={() => select(m.monitorId)}
          primary={m.name}
          secondary={`${m.layout} · ${m.cameraIds.length} ${t('monitors.cameras', { count: m.cameraIds.length })}`}
        />
      ))}
      {monitors.length === 0 && (
        <Typography variant="body2" color="text.secondary" sx={{ p: 1.5 }}>
          {t('monitors.noMonitors')}
        </Typography>
      )}
    </List>
  )

  return (
    <MasterDetailLayout
      listTitle={t('monitors.title')}
      onAdd={selectNew}
      addTooltip={t('monitors.newMonitor')}
      list={list}
    >
      {isNew || selected ? (
        <MonitorSetupPanel
          ref={detailRef}
          key={detailKey}
          monitor={isNew ? null : selected}
          onSaved={setId}
          onRemoved={reset}
        />
      ) : (
        <Typography color="text.secondary" sx={{ p: 2 }}>
          {t('monitors.selectHint')}
        </Typography>
      )}
    </MasterDetailLayout>
  )
}
