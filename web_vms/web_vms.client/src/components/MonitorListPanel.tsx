import { useImperativeHandle, useRef, useState } from 'react'
import { List, Typography } from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppSelector } from '../store'
import MasterDetailLayout from './settings/MasterDetailLayout'
import MonitorSetupPanel from './MonitorSetupPanel'
import SettingsListItem from './settings/SettingsListItem'
import type { ApplyIfDirtyHandle } from './settings/useDirtyGuard'

export default function MonitorListPanel({ ref }: { ref?: React.Ref<ApplyIfDirtyHandle> }) {
  const { t } = useTranslation()
  const monitors = useAppSelector((s) => s.monitors.monitors)
  const [selection, setSelection] = useState<string | null>(null)  // monitor id | 'new' | null

  const detailRef = useRef<ApplyIfDirtyHandle>(null)

  const setSelectionSafe = async (sel: string | null) => {
    if (detailRef.current && !(await detailRef.current.applyIfDirty())) return
    setSelection(sel)
  }

  useImperativeHandle(ref, () => ({
    applyIfDirty: () => detailRef.current ? detailRef.current.applyIfDirty() : Promise.resolve(true)
  }))

  const selected = selection !== null && selection !== 'new'
    ? monitors.find((m) => m.monitorId === selection) ?? null
    : null

  const list = (
    <List dense disablePadding>
      {monitors.map((m) => (
        <SettingsListItem
          key={m.monitorId}
          selected={selection === m.monitorId}
          onClick={() => void setSelectionSafe(m.monitorId)}
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
      onAdd={() => void setSelectionSafe('new')}
      addTooltip={t('monitors.newMonitor')}
      list={list}
    >
      {selection === 'new' || selected ? (
        <MonitorSetupPanel
          ref={detailRef}
          key={selection === 'new' ? 'new' : selected!.monitorId}
          monitor={selection === 'new' ? null : selected}
          onSaved={(monitorId) => setSelection(monitorId)}
          onRemoved={() => setSelection(null)}
        />
      ) : (
        <Typography color="text.secondary" sx={{ p: 2 }}>
          {t('monitors.selectHint')}
        </Typography>
      )}
    </MasterDetailLayout>
  )
}
