import { useMemo, useState, type ReactNode } from 'react'
import { Box, Chip, IconButton, List, Stack, Tab, Tabs, Tooltip, Typography } from '@mui/material'
import PlaceIcon from '@mui/icons-material/Place'
import { useTranslation } from 'react-i18next'
import type { SpaceDto } from '../../dto/dtos'
import { useAppSelector } from '../../store'
import { cameraMapStatus, STATUS_COLOR } from '../../map/cameraStatus'
import SettingsListItem from '../settings/SettingsListItem'
import MapObjectList, { type MapObjectSection } from './MapObjectList'
import { useEditorState } from './editorState'

type SpaceTab = 'cameras' | 'landmarks'

type Props = {
  tree: { space: SpaceDto; depth: number }[]
  /** Whether the spaces have been fetched at least once, so "nothing yet" is not shown while loading. */
  loaded: boolean
  /** The space open in the editor; null while none is, or one is being created. */
  space: SpaceDto | null
  onSelectSpace: (spaceId: string) => void
}

/**
 * The page's left column: what there is, always in view. Spaces as a tree on top, and under it what
 * belongs to the space picked there -- every camera of the system, by server, with where it stands
 * relative to that space, and the space's landmarks. The layout carries the dependency: the lower
 * half appears only once a space is picked, and the pick stays highlighted in the tree above it.
 * Picking from a list acts on the map -- it selects, or arms the next click to place -- and the
 * object's properties open in the column on the other side of the map, so a list never gives way
 * to a property panel.
 */
export default function MapsSidebar({ tree, loaded, space, onSelectSpace }: Props) {
  const { t } = useTranslation()
  const [tab, setTab] = useState<SpaceTab>('cameras')
  const cameraCount = useAppSelector((s) => Object.keys(s.live.cameraStatesById).length)
  const placedHere = useAppSelector((s) => new Set(
    s.spatial.placements.filter((p) => p.spaceId === space?.id).map((p) => p.cameraId)).size)
  const landmarkCount = useAppSelector((s) => s.spatial.landmarksBySpace[space?.id ?? '']?.length ?? 0)

  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', minHeight: 0, height: '100%' }}>
      <SectionTitle label={t('maps.tabSpaces')} count={String(tree.length)} />
      {/* As tall as the tree needs, up to a share of the column -- the space's own lists need the rest. */}
      <Box sx={{ flex: space ? '0 1 auto' : 1, maxHeight: space ? '40%' : undefined, minHeight: 0, overflowY: 'auto' }}>
        <SpacesTree tree={tree} loaded={loaded} selectedId={space?.id ?? undefined} onSelect={onSelectSpace} />
      </Box>
      {space ? (
        <>
          <Stack direction="row" alignItems="center" sx={{ flexShrink: 0, borderTop: 1, borderBottom: 1, borderColor: 'divider' }}>
            <Tabs
              value={tab}
              onChange={(_, v: SpaceTab) => setTab(v)}
              variant="fullWidth"
              sx={{ flex: 1, minHeight: 36, '& .MuiTab-root': { minHeight: 36, minWidth: 0, px: 1, textTransform: 'none' } }}
            >
              <Tab value="cameras" label={<TabLabel label={t('maps.tabCameras')} count={`${placedHere}/${cameraCount}`} />} />
              <Tab value="landmarks" label={<TabLabel label={t('maps.tabLandmarks')} count={String(landmarkCount)} />} />
            </Tabs>
            {tab === 'landmarks' && <AddLandmarkButton />}
          </Stack>
          <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
            {tab === 'cameras' ? <CamerasList space={space} /> : <LandmarksList space={space} />}
          </Box>
        </>
      ) : (
        <Typography variant="caption" color="text.secondary" sx={{ p: 1.5, borderTop: 1, borderColor: 'divider' }}>
          {t('maps.pickSpace')}
        </Typography>
      )}
    </Box>
  )
}

function SectionTitle({ label, count }: { label: string; count?: string }) {
  return (
    <Stack
      direction="row"
      alignItems="center"
      sx={{ flexShrink: 0, px: 1.5, py: 0.75, borderTop: 1, borderBottom: 1, borderColor: 'divider', bgcolor: 'action.hover' }}
    >
      <Typography variant="subtitle2" noWrap sx={{ flex: 1 }}>{label}</Typography>
      {count !== undefined && <Typography variant="caption" color="text.secondary">{count}</Typography>}
    </Stack>
  )
}

function TabLabel({ label, count }: { label: string; count: string }): ReactNode {
  return (
    <Stack direction="row" spacing={0.75} alignItems="baseline">
      <span>{label}</span>
      <Typography component="span" variant="caption" color="text.secondary">{count}</Typography>
    </Stack>
  )
}

/** Adding to the list is done on the map, so this only arms the tool. */
function AddLandmarkButton() {
  const { t } = useTranslation()
  const { mode, setMode, setPendingCameraId } = useEditorState()
  return (
    <Tooltip title={t('maps.addLandmark')}>
      <IconButton
        size="small"
        sx={{ mx: 0.5 }}
        color={mode === 'landmark' ? 'primary' : 'default'}
        onClick={() => { setPendingCameraId(null); setMode(mode === 'landmark' ? 'select' : 'landmark') }}
      >
        <PlaceIcon fontSize="small" />
      </IconButton>
    </Tooltip>
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
    const where = (cameraId: string) => {
      const here = placements.find((p) => p.cameraId === cameraId && p.spaceId === space.id)
      const reference = placements.find((p) => p.cameraId === cameraId && p.isReference)
      const elsewhere = reference && reference.spaceId !== space.id ? spaces.find((s) => s.id === reference.spaceId) : undefined
      return { here, elsewhere }
    }
    // What is on this space first, then what is still to be placed, and last what stands elsewhere.
    const rank = (cameraId: string) => {
      const { here, elsewhere } = where(cameraId)
      return here ? 0 : elsewhere ? 2 : 1
    }
    return groups.filter((g) => g.cameras.length > 0).map((g) => ({
      title: g.title,
      items: [...g.cameras].sort((a, b) => rank(a.cameraId) - rank(b.cameraId)).map((c) => {
        const { here, elsewhere } = where(c.cameraId)
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
  const { selection, select } = useEditorState()

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

  return <MapObjectList sections={sections} />
}
