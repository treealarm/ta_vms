import { useMemo, useState } from 'react'
import { Box, Chip, IconButton, List, Stack, Tab, Tabs, Tooltip, Typography } from '@mui/material'
import PlaceIcon from '@mui/icons-material/Place'
import { useTranslation } from 'react-i18next'
import type { SpaceDto } from '../../dto/dtos'
import { useAppSelector } from '../../store'
import { cameraMapStatus, STATUS_COLOR } from '../../map/cameraStatus'
import SettingsListItem from '../settings/SettingsListItem'
import MapObjectList, { type MapObjectSection } from './MapObjectList'
import { useEditorState } from './editorState'

type SidebarTab = 'spaces' | 'cameras' | 'landmarks'

type Props = {
  tree: { space: SpaceDto; depth: number }[]
  /** Whether the spaces have been fetched at least once, so "nothing yet" is not shown while loading. */
  loaded: boolean
  /** The space open in the editor; null while none is, or one is being created. */
  space: SpaceDto | null
  onSelectSpace: (spaceId: string) => void
}

/**
 * The page's left column: what there is, always in view. Spaces as a tree; every camera of the
 * system, by server, with where it stands relative to the open space; the open space's landmarks.
 * Picking from a list acts on the map -- it selects, or arms the next click to place -- and the
 * object's properties open in the column on the other side of the map, so a list never gives way
 * to a property panel.
 */
export default function MapsSidebar({ tree, loaded, space, onSelectSpace }: Props) {
  const { t } = useTranslation()
  const [wanted, setWanted] = useState<SidebarTab>('spaces')
  // Cameras and landmarks are relative to the open space; without one there is only the tree.
  const tab: SidebarTab = space ? wanted : 'spaces'

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', minHeight: 0, height: '100%' }}>
      <Tabs
        value={tab}
        onChange={(_, v: SidebarTab) => setWanted(v)}
        variant="fullWidth"
        sx={{ borderBottom: 1, borderColor: 'divider', flexShrink: 0, minHeight: 40, '& .MuiTab-root': { minHeight: 40, minWidth: 0, px: 1 } }}
      >
        <Tab value="spaces" label={t('maps.tabSpaces')} />
        <Tab value="cameras" label={t('maps.tabCameras')} disabled={!space} />
        <Tab value="landmarks" label={t('maps.tabLandmarks')} disabled={!space} />
      </Tabs>
      <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
        {tab === 'spaces' && <SpacesTree tree={tree} loaded={loaded} selectedId={space?.id ?? undefined} onSelect={onSelectSpace} />}
        {tab === 'cameras' && space && <CamerasList space={space} />}
        {tab === 'landmarks' && space && <LandmarksList space={space} />}
      </Box>
    </Box>
  )
}

function SpacesTree({ tree, loaded, selectedId, onSelect }: {
  tree: Props['tree']; loaded: boolean; selectedId?: string; onSelect: (id: string) => void
}) {
  const { t } = useTranslation()
  return (
    <List dense disablePadding>
      {tree.map(({ space, depth }) => (
        <SettingsListItem
          key={space.id}
          depth={depth}
          selected={selectedId === space.id}
          onClick={() => space.id && onSelect(space.id)}
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
}

/**
 * Every camera, by server, each saying where it is with respect to this space. One placed here is
 * selected; one placed elsewhere, or nowhere, is armed for the next click on the map -- a picture
 * of it here, or its real position if it has none yet.
 */
function CamerasList({ space }: { space: SpaceDto }) {
  const { t } = useTranslation()
  const camerasById = useAppSelector((s) => s.live.cameraStatesById)
  const servers = useAppSelector((s) => s.live.servers)
  const placements = useAppSelector((s) => s.spatial.placements)
  const spaces = useAppSelector((s) => s.spatial.spaces)
  const { selection, pendingCameraId, setPendingCameraId, setMode, select } = useEditorState()

  const sections = useMemo<MapObjectSection[]>(() => {
    const cameras = Object.values(camerasById).sort((a, b) => (a.name || a.cameraId).localeCompare(b.name || b.cameraId))
    const known = new Set(servers.map((s) => s.serverId))
    const groups = [
      ...servers.map((s) => ({ title: s.serverId, cameras: cameras.filter((c) => c.serverId === s.serverId) })),
      { title: t('maps.noServer'), cameras: cameras.filter((c) => !c.serverId || !known.has(c.serverId)) },
    ]
    return groups.filter((g) => g.cameras.length > 0).map((g) => ({
      title: g.title,
      items: g.cameras.map((c) => {
        const here = placements.find((p) => p.cameraId === c.cameraId && p.spaceId === space.id)
        const reference = placements.find((p) => p.cameraId === c.cameraId && p.isReference)
        const elsewhere = reference && reference.spaceId !== space.id ? spaces.find((s) => s.id === reference.spaceId) : undefined
        return {
          key: c.cameraId,
          primary: c.name || c.cameraId,
          dot: STATUS_COLOR[cameraMapStatus(c)],
          secondary: here
            ? (here.isReference ? t('maps.hereReference') : t('maps.herePicture'))
            : (
              <Chip
                size="small"
                variant="outlined"
                label={elsewhere ? t('maps.standsIn', { name: elsewhere.name }) : t('maps.notPlaced')}
                sx={{ mt: 0.25 }}
              />
            ),
          selected: here ? selection?.kind === 'camera' && selection.id === c.cameraId : pendingCameraId === c.cameraId,
          onClick: () => {
            if (here) {
              select('camera', c.cameraId, here)
            } else {
              setMode('select')
              setPendingCameraId(c.cameraId)
            }
          },
        }
      }),
    }))
  }, [camerasById, servers, placements, spaces, space.id, selection, pendingCameraId, select, setMode, setPendingCameraId, t])

  return (
    <>
      <Typography variant="caption" color="text.secondary" sx={{ px: 1.5, pt: 1, display: 'block' }}>
        {t('maps.camerasHint')}
      </Typography>
      <MapObjectList sections={sections} />
    </>
  )
}

function LandmarksList({ space }: { space: SpaceDto }) {
  const { t } = useTranslation()
  const loaded = useAppSelector((s) => s.spatial.landmarksBySpace[space.id ?? ''])
  const landmarks = useMemo(() => loaded ?? [], [loaded])
  const { selection, mode, setMode, setPendingCameraId, select } = useEditorState()

  const sections: MapObjectSection[] = [{
    title: t('maps.landmarks'),
    hint: t('maps.landmarksPurpose'),
    empty: t('maps.noLandmarks'),
    items: landmarks.map((m) => ({
      key: m.id ?? m.name,
      primary: m.name,
      secondary: t('maps.landmarkHeightShort', { height: m.heightM }),
      selected: selection?.kind === 'landmark' && selection.id === m.id,
      onClick: () => m.id && select('landmark', m.id, m),
    })),
  }]

  return (
    <Box>
      {/* Adding to the list is done on the map, so the button here only arms the tool. */}
      <Stack direction="row" alignItems="center" justifyContent="flex-end" sx={{ px: 0.5, pt: 0.5 }}>
        <Tooltip title={t('maps.addLandmark')}>
          <IconButton
            size="small"
            color={mode === 'landmark' ? 'primary' : 'default'}
            onClick={() => { setPendingCameraId(null); setMode(mode === 'landmark' ? 'select' : 'landmark') }}
          >
            <PlaceIcon fontSize="small" />
          </IconButton>
        </Tooltip>
      </Stack>
      <MapObjectList sections={sections} />
    </Box>
  )
}
