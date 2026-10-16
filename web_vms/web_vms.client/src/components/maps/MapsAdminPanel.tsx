import { useEffect, useMemo, useState } from 'react'
import { Box, IconButton, List, Menu, MenuItem, Tooltip, Typography } from '@mui/material'
import RefreshIcon from '@mui/icons-material/Refresh'
import { useTranslation } from 'react-i18next'
import { useSearchParams } from 'react-router-dom'
import { useAppDispatch, useAppSelector } from '../../store'
import { loadPlacements, loadSpaces } from '../../store/spatialSlice'
import MasterDetailLayout from '../settings/MasterDetailLayout'
import SettingsListItem from '../settings/SettingsListItem'
import { useMasterDetailSelection } from '../settings/useMasterDetailSelection'
import type { ApplyIfDirtyHandle } from '../settings/useDirtyGuard'
import { SpaceForm, type NewSpace } from './SpaceForm'
import SpaceEditor from './SpaceEditor'
import { treeOf } from './spatialUtils'

/**
 * Admin → Maps: every space as a tree on the left, the selected one's settings and its map on the
 * right. Nested plans are reached from the tree, or from their footprints on the map.
 *
 * `?cameraId=` opens on the space that camera really stands in, with the camera selected: the
 * way in from the places that send an administrator here to calibrate it. A camera that stands
 * nowhere yet opens the page as it is.
 */
export default function MapsAdminPanel({ ref }: { ref?: React.Ref<ApplyIfDirtyHandle> }) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const spaces = useAppSelector((s) => s.spatial.spaces)
  const placements = useAppSelector((s) => s.spatial.placements)
  const loaded = useAppSelector((s) => s.spatial.loaded)
  const [params] = useSearchParams()
  const wantedCameraId = params.get('cameraId')
  const wantedSpaceId = useMemo(
    () => placements.find((p) => p.cameraId === wantedCameraId && p.isReference)?.spaceId ?? null,
    [placements, wantedCameraId],
  )
  const [addMenu, setAddMenu] = useState<HTMLElement | null>(null)
  const [creating, setCreating] = useState<NewSpace | null>(null)

  useEffect(() => {
    void dispatch(loadSpaces())
    void dispatch(loadPlacements())
  }, [dispatch])

  const tree = useMemo(() => treeOf(spaces), [spaces])
  const { isNew, selected, detailRef, detailKey, select, selectNew, reset, setId } =
    useMasterDetailSelection(ref, tree, (e) => e.space.id ?? '', wantedSpaceId)

  const startCreating = (what: NewSpace) => {
    setAddMenu(null)
    setCreating(what)
    selectNew()
  }

  const current = selected?.space ?? null

  const list = (
    <List dense disablePadding>
      {tree.map(({ space, depth }) => (
        <SettingsListItem
          key={space.id}
          depth={depth}
          selected={selected?.space.id === space.id}
          onClick={() => space.id && select(space.id)}
          primary={space.name}
          secondary={space.kind === 'map'
            ? t('maps.kindMap')
            : space.realCoordinates ? t('maps.kindPlanReal') : t('maps.kindPlan')}
        />
      ))}
      {loaded && tree.length === 0 && (
        <Typography variant="body2" color="text.secondary" sx={{ p: 1.5 }}>{t('maps.noSpaces')}</Typography>
      )}
    </List>
  )

  return (
    <MasterDetailLayout
      listTitle={t('maps.title')}
      listWidth={240}
      listActions={(
        <Tooltip title={t('common.refresh')}>
          <IconButton size="small" onClick={() => { void dispatch(loadSpaces()); void dispatch(loadPlacements()) }}>
            <RefreshIcon fontSize="small" />
          </IconButton>
        </Tooltip>
      )}
      onAdd={(e) => setAddMenu(e.currentTarget)}
      addTooltip={t('maps.add')}
      list={list}
    >
      <Menu anchorEl={addMenu} open={!!addMenu} onClose={() => setAddMenu(null)}>
        <MenuItem onClick={() => startCreating({ kind: 'map', parent: null })}>{t('maps.newMap')}</MenuItem>
        <MenuItem onClick={() => startCreating({ kind: 'plan', parent: null })}>{t('maps.newPlan')}</MenuItem>
        <MenuItem disabled={!current} onClick={() => current && startCreating({ kind: 'plan', parent: current })}>
          {current ? t('maps.newNestedPlan', { name: current.name }) : t('maps.newNestedPlanDisabled')}
        </MenuItem>
      </Menu>

      {isNew && creating ? (
        <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
          <SpaceForm
            ref={detailRef}
            key={`${detailKey}:${creating.kind}:${creating.parent?.id ?? ''}`}
            space={null}
            creating={creating}
            onSaved={(space, created) => { if (created && space.id) setId(space.id) }}
            onRemoved={reset}
          />
        </Box>
      ) : selected ? (
        <Box sx={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
          <SpaceForm ref={detailRef} key={detailKey} space={selected.space} onSaved={() => undefined} onRemoved={reset} />
          <SpaceEditor
            key={`editor:${selected.space.id}`}
            space={selected.space}
            initialCameraId={selected.space.id === wantedSpaceId ? wantedCameraId : null}
            onOpenSpace={select}
          />
        </Box>
      ) : (
        <Box sx={{ p: 2 }}>
          <Typography color="text.secondary">{t('maps.selectHint')}</Typography>
        </Box>
      )}
    </MasterDetailLayout>
  )
}
