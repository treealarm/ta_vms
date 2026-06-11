import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Box,
  Button,
  Chip,
  Divider,
  FormControlLabel,
  List,
  ListItem,
  ListItemButton,
  ListItemText,
  Stack,
  Switch,
  Typography,
} from '@mui/material'
import { useTranslation } from 'react-i18next'
import { useAppDispatch, useAppSelector } from '../store'
import { fetchEntityStates } from '../store/entityStateSlice'
import type { EntityStateDto } from '../dto/dtos'
import { toLocalDatetimeInputValue } from '../dto/dtos'

function entityKey(e: EntityStateDto): string {
  return `${e.service}::${e.entityType}::${e.entityId}`
}

function groupEntities(entities: EntityStateDto[]): Map<string, Map<string, EntityStateDto[]>> {
  const tree = new Map<string, Map<string, EntityStateDto[]>>()
  for (const e of [...entities].sort((a, b) => a.entityId.localeCompare(b.entityId))) {
    let byType = tree.get(e.service)
    if (!byType) {
      byType = new Map()
      tree.set(e.service, byType)
    }
    let list = byType.get(e.entityType)
    if (!list) {
      list = []
      byType.set(e.entityType, list)
    }
    list.push(e)
  }
  return tree
}

function formatStateJson(stateJson: string): string {
  try {
    return JSON.stringify(JSON.parse(stateJson), null, 2)
  } catch {
    return stateJson
  }
}

export default function EntityStatePanel() {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const entities = useAppSelector((s) => s.entityState.entities)

  const [selectedKey, setSelectedKey] = useState<string | null>(null)
  const [autoRefresh, setAutoRefresh] = useState(true)
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null)

  const refresh = useCallback(() => {
    void dispatch(fetchEntityStates())
  }, [dispatch])

  useEffect(() => { refresh() }, [refresh])

  useEffect(() => {
    if (autoRefresh) {
      timerRef.current = setInterval(refresh, 2000)
    } else if (timerRef.current) {
      clearInterval(timerRef.current)
    }
    return () => { if (timerRef.current) clearInterval(timerRef.current) }
  }, [autoRefresh, refresh])

  const tree = groupEntities(entities)
  const selected = entities.find((e) => entityKey(e) === selectedKey) ?? null

  return (
    <Box sx={{ display: 'flex', height: '100%', overflow: 'hidden' }}>
      {/* ── Left: tree ── */}
      <Box sx={{ width: 300, flexShrink: 0, display: 'flex', flexDirection: 'column', borderRight: 1, borderColor: 'divider' }}>
        <Stack direction="row" spacing={1} alignItems="center" sx={{ px: 1, py: 0.5, flexShrink: 0 }}>
          <FormControlLabel
            control={<Switch size="small" checked={autoRefresh} onChange={(_, v) => setAutoRefresh(v)} />}
            label={<Typography variant="caption">{t('entityState.auto')}</Typography>}
            sx={{ mr: 0 }}
          />
          <Button size="small" variant="outlined" onClick={refresh} sx={{ minWidth: 0, py: 0.25, px: 1, fontSize: '0.7rem' }}>
            {t('common.refresh')}
          </Button>
          <Typography variant="caption" color="text.secondary">{entities.length}</Typography>
        </Stack>
        <Divider />

        <Box sx={{ flex: 1, overflowY: 'auto' }}>
          {tree.size === 0 && (
            <Typography variant="body2" color="text.secondary" sx={{ p: 1 }}>{t('entityState.noEntities')}</Typography>
          )}
          <List dense disablePadding>
            {Array.from(tree.entries()).sort(([a], [b]) => a.localeCompare(b)).map(([service, byType]) => (
              <Box key={service}>
                <ListItem dense sx={{ pl: 2 }}>
                  <ListItemText primary={service} primaryTypographyProps={{ variant: 'subtitle2', fontWeight: 'bold' }} />
                </ListItem>
                {Array.from(byType.entries()).sort(([a], [b]) => a.localeCompare(b)).map(([entityType, list]) => (
                  <Box key={entityType}>
                    <ListItem dense sx={{ pl: 4 }}>
                      <ListItemText primary={entityType} primaryTypographyProps={{ variant: 'caption', color: 'text.secondary' }} />
                    </ListItem>
                    {list.map((e) => (
                      <ListItemButton
                        key={entityKey(e)}
                        selected={selectedKey === entityKey(e)}
                        onClick={() => setSelectedKey(entityKey(e))}
                        sx={{ pl: 6 }}
                      >
                        <Box
                          className="vms-entity-line"
                          data-entity-status={e.stale ? 'stale' : 'alive'}
                          sx={{ width: 8, height: 8, borderRadius: '50%', mr: 1, flexShrink: 0 }}
                        />
                        <ListItemText primary={e.entityId} primaryTypographyProps={{ variant: 'body2', noWrap: true }} />
                      </ListItemButton>
                    ))}
                  </Box>
                ))}
              </Box>
            ))}
          </List>
        </Box>
      </Box>

      {/* ── Right: details ── */}
      <Box sx={{ flex: 1, minWidth: 0, overflow: 'hidden', display: 'flex', flexDirection: 'column' }}>
        {!selected ? (
          <Box sx={{ height: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
            <Typography variant="body2" color="text.secondary">{t('entityState.selectHint')}</Typography>
          </Box>
        ) : (
          <Stack spacing={1} sx={{ p: 1.5, overflow: 'hidden', height: '100%' }}>
            <Stack direction="row" spacing={1} alignItems="center">
              <Chip
                label={selected.stale ? t('entityState.stale') : t('entityState.alive')}
                color={selected.stale ? 'default' : 'success'}
                size="small"
              />
              <Typography variant="subtitle2">{selected.entityId}</Typography>
            </Stack>

            <Stack direction="row" spacing={3}>
              <Box>
                <Typography variant="caption" color="text.secondary">{t('entityState.service')}</Typography>
                <Typography variant="body2">{selected.service}</Typography>
              </Box>
              <Box>
                <Typography variant="caption" color="text.secondary">{t('entityState.entityType')}</Typography>
                <Typography variant="body2">{selected.entityType}</Typography>
              </Box>
              <Box>
                <Typography variant="caption" color="text.secondary">{t('entityState.lastSeen')}</Typography>
                <Typography variant="body2">{toLocalDatetimeInputValue(new Date(selected.lastSeen)).replace('T', ' ')}</Typography>
              </Box>
            </Stack>

            <Box component="pre" className="vms-entity-state-body" sx={{ flex: 1, m: 0 }}>
              {formatStateJson(selected.stateJson)}
            </Box>
          </Stack>
        )}
      </Box>
    </Box>
  )
}
