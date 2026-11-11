import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import {
  Alert, Box, Chip, IconButton, List, ListItemButton, ListItemText, Stack, Tab, Tabs, Tooltip, Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import ChevronRightIcon from '@mui/icons-material/ChevronRight'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import PlaceIcon from '@mui/icons-material/Place'
import DashboardCustomizeIcon from '@mui/icons-material/DashboardCustomize'
import { useTranslation } from 'react-i18next'
import { apiFetch } from '../../api/apiFetch'
import { enc } from '../../api/requestJson'
import type { DigitalInputDto, RelayOutputDto, SpaceDto } from '../../dto/dtos'
import type { MapMarker } from '../../dto/mapDtos'
import { useAppDispatch, useAppSelector } from '../../store'
import { childrenOf, loadChildren } from '../../store/objectsSlice'
import { createDiagramNode } from '../../store/mapEditsSlice'
import { useMapChildren } from '../../hooks/useMapChildren'
import { cameraMapStatus, STATUS_COLOR } from '../../map/cameraStatus'
import KindIcon from '../../map/KindIcon'
import { treeRows } from '../../map/nodeTree'
import { NODE_DRAG_TYPE } from '../../map/dragTypes'
import SettingsListItem from '../settings/SettingsListItem'
import MapObjectList, { type MapObjectSection } from './MapObjectList'
import { useEditorState } from './editorState'
import { useDeviceDiagram } from './useDeviceDiagram'

type SpaceTab = 'devices' | 'nodes' | 'landmarks'

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
  const [tab, setTab] = useState<SpaceTab>('devices')
  const cameraCount = useAppSelector((s) => Object.keys(s.live.cameraStatesById).length)
  const placedHere = useAppSelector((s) => new Set(
    s.spatial.placements.filter((p) => p.spaceId === space?.id).map((p) => p.cameraId)).size)
  const landmarkCount = useAppSelector((s) => s.spatial.landmarksBySpace[space?.id ?? '']?.length ?? 0)
  const { markers } = useMapChildren(space?.id ?? null)

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
              <Tab value="devices" label={<TabLabel label={t('maps.tabDevices')} count={`${placedHere}/${cameraCount}`} />} />
              <Tab value="nodes" label={<TabLabel label={t('maps.editor.tabNodes')} count={String(markers.length)} />} />
              <Tab value="landmarks" label={<TabLabel label={t('maps.tabLandmarks')} count={String(landmarkCount)} />} />
            </Tabs>
            {tab === 'landmarks' && <AddLandmarkButton />}
          </Stack>
          <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
            {tab === 'devices' ? (
              <DevicesList space={space} />
            ) : tab === 'nodes' ? (
              <NodesTree markers={markers} space={space} onOpenSpace={onSelectSpace} />
            ) : (
              <LandmarksList space={space} />
            )}
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
  // Picking a space means its map; a diagram opened over it steps aside. The editor state resets
  // only when another space is opened, so the same space picked again has to say it itself.
  const { openDiagram } = useEditorState()
  return (
    <List dense disablePadding>
      {tree.map(({ space, depth }) => (
        <SettingsListItem
          key={space.id}
          depth={depth}
          selected={selectedId === space.id}
          onClick={() => {
            if (!space.id) return
            openDiagram(null)
            onSelect(space.id)
          }}
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

type DeviceParts = {
  loading: boolean
  loaded: boolean
  error?: string
  inputs: DigitalInputDto[]
  relays: RelayOutputDto[]
}

/**
 * The placement palette: every physical device, by server, each saying where it is with respect to
 * this space, and — once opened — the parts it declares. One placed here is selected; one placed
 * elsewhere, or nowhere, is armed for the next click on the map, which makes another picture (the
 * service makes the first one the camera's reference). Parts are shown to be found and are not
 * placed in M2.4; their replicas are M2.6.
 */
function DevicesList({ space }: { space: SpaceDto }) {
  const { t } = useTranslation()
  const camerasById = useAppSelector((s) => s.live.cameraStatesById)
  const servers = useAppSelector((s) => s.live.servers)
  const placements = useAppSelector((s) => s.spatial.placements)
  const spaces = useAppSelector((s) => s.spatial.spaces)
  const { selection, pendingCameraId, setPendingCameraId, setMode, select } = useEditorState()
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [parts, setParts] = useState<Record<string, DeviceParts>>({})

  // One request per device, and only when the reader opens it: at a hundred thousand cameras a
  // part list fetched for the whole palette would be a hundred thousand requests.
  const loadParts = useCallback(async (cameraId: string) => {
    if (parts[cameraId]?.loading || parts[cameraId]?.loaded) return
    setParts((prev) => ({ ...prev, [cameraId]: { loading: true, loaded: false, inputs: [], relays: [] } }))
    try {
      const [inputs, relays] = await Promise.all([
        apiFetch<DigitalInputDto[]>(`/api/DeviceIo/cameras/${enc(cameraId)}/digital-inputs`),
        apiFetch<RelayOutputDto[]>(`/api/DeviceIo/cameras/${enc(cameraId)}/relay-outputs`),
      ])
      setParts((prev) => ({ ...prev, [cameraId]: { loading: false, loaded: true, inputs, relays } }))
    } catch (e) {
      setParts((prev) => ({
        ...prev,
        [cameraId]: {
          loading: false, loaded: true, inputs: [], relays: [],
          error: e instanceof Error ? e.message : String(e),
        },
      }))
    }
  }, [parts])

  const toggle = useCallback((cameraId: string) => {
    const next = new Set(expanded)
    if (next.has(cameraId)) {
      next.delete(cameraId)
    } else {
      next.add(cameraId)
      void loadParts(cameraId)
    }
    setExpanded(next)
  }, [expanded, loadParts])

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

    const inactive = { disabled: true, onClick: () => undefined }

    return groups.filter((g) => g.cameras.length > 0).map((g) => {
      const items: MapObjectSection['items'] = []
      for (const c of [...g.cameras].sort((a, b) => rank(a.cameraId) - rank(b.cameraId))) {
        const { here, elsewhere } = where(c.cameraId)
        const open = expanded.has(c.cameraId)
        items.push({
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
          action: {
            title: open ? t('maps.devices.collapse') : t('maps.devices.expand'),
            icon: open ? <ExpandMoreIcon fontSize="small" /> : <ChevronRightIcon fontSize="small" />,
            onClick: () => toggle(c.cameraId),
          },
        })

        if (!open) continue

        // Streams come with the camera's own configuration; the I/O parts come from the camera
        // through VmsOnvif, because the browser does not subscribe to the device topic.
        for (const stream of c.streams ?? []) {
          items.push({
            key: `stream:${c.cameraId}:${stream.streamName}`,
            primary: stream.streamName,
            secondary: t('maps.devices.stream'),
            indent: true,
            ...inactive,
          })
        }

        const own = parts[c.cameraId]
        if (!own || own.loading) {
          items.push({ key: `parts:${c.cameraId}`, primary: t('maps.devices.loading'), indent: true, ...inactive })
        } else if (own.error) {
          items.push({ key: `parts:${c.cameraId}`, primary: own.error, indent: true, ...inactive })
        } else {
          for (const input of own.inputs) {
            items.push({
              key: `input:${c.cameraId}:${input.token}`,
              primary: input.token,
              secondary: t('maps.devices.input'),
              indent: true,
              ...inactive,
            })
          }
          for (const relay of own.relays) {
            items.push({
              key: `relay:${c.cameraId}:${relay.token}`,
              primary: relay.token,
              secondary: t('maps.devices.relay'),
              indent: true,
              ...inactive,
            })
          }
          if (own.inputs.length === 0 && own.relays.length === 0 && (c.streams ?? []).length === 0)
            items.push({ key: `parts:${c.cameraId}`, primary: t('maps.devices.noParts'), indent: true, ...inactive })
        }
      }

      return { title: g.title, items }
    })
  }, [camerasById, servers, placements, spaces, space.id, selection, pendingCameraId, select, setMode, setPendingCameraId, t, expanded, parts, toggle])

  return (
    <>
      <Typography variant="caption" color="text.secondary" sx={{ px: 1.5, pt: 1, display: 'block' }}>
        {t('maps.devicesHint')}
      </Typography>
      <MapObjectList sections={sections} />
    </>
  )
}

/**
 * The space's tree, a level at a time: what is drawn on this canvas and, under a container or a
 * diagram, what the tree holds there even when it is drawn elsewhere. A node dragged from here
 * onto the map becomes a replica of itself where it is dropped — the way a camera's picture is
 * made on another plan.
 */
function NodesTree({ markers, space, onOpenSpace }: {
  markers: MapMarker[]; space: SpaceDto; onOpenSpace: (spaceId: string) => void
}) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const { selection, setSelection, openDiagram } = useEditorState()
  const known = useAppSelector((s) => s.objects.markers)
  const childrenByParent = useAppSelector((s) => s.objects.childrenByParent)
  const { showDeviceDiagram: openDeviceDiagram, busy: diagramBusy } = useDeviceDiagram(space.id)
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)

  // The space's own page is what the tree grows from; a row's page is read when it is opened.
  useEffect(() => {
    if (space.id && !childrenByParent[space.id]?.loaded) {
      void dispatch(loadChildren({ parentId: space.id }))
    }
  }, [space.id, childrenByParent, dispatch])

  const roots = space.id ? childrenOf(known, childrenByParent, space.id) : []
  const rows = treeRows(roots, childrenByParent, known, expanded)
  const rootsLoaded = !space.id || !!childrenByParent[space.id]?.loaded

  const toggle = (id: string) => {
    const next = new Set(expanded)
    if (next.has(id)) {
      next.delete(id)
    } else {
      next.add(id)
      if (!childrenByParent[id]?.loaded) void dispatch(loadChildren({ parentId: id }))
    }
    setExpanded(next)
  }

  /**
   * A new diagram stands on this canvas, in the middle of the view, whatever is selected: the
   * button belongs to the canvas, and a diagram nested in another is made from inside it, with
   * that diagram's own "New diagram". The point stays in sight, so it can be dragged into place
   * before the diagram is opened.
   */
  const addDiagram = async () => {
    if (!space.id) return

    setBusy(true)
    try {
      const created = await dispatch(createDiagramNode({
        parentId: space.id,
        name: `${t('diagramEditor.diagram')} ${markers.length + 1}`,
        geometry: { top: 0, left: 0, width: 800, height: 600, rotation: 0 },
        at: {
          lat: (space.view.southWest.lat + space.view.northEast.lat) / 2,
          lon: (space.view.southWest.lon + space.view.northEast.lon) / 2,
        },
      })).unwrap()
      setSelection({ kind: 'node', id: created.id })
      // The point is on the canvas, not on whatever diagram was open over it.
      openDiagram(null)
    } catch {
      // The store carries the message.
    } finally {
      setBusy(false)
    }
  }

  /**
   * The device's diagram on this canvas: drawn if it is not there yet, opened if it is. It hangs
   * under the camera's picture here, and asking again is safe — the server keeps one per device.
   */
  const showDeviceDiagram = async (marker: MapMarker) => {
    if (!marker.cameraId) return

    setNotice(null)
    const shown = await openDeviceDiagram(marker.cameraId)
    if (!shown) setNotice(t('diagramEditor.diagramNotCreated'))
  }

  return (
    <>
      <Stack direction="row" alignItems="center" sx={{ px: 1.5, pt: 1 }}>
        <Typography variant="caption" color="text.secondary" sx={{ flex: 1 }}>
          {t('maps.editor.nodesHint')}
        </Typography>
        <Tooltip title={t('diagramEditor.addDiagram')}>
          <span>
            <IconButton
              size="small"
              title={t('diagramEditor.addDiagram')}
              aria-label={t('diagramEditor.addDiagram')}
              disabled={busy || !space.id}
              onClick={() => void addDiagram()}
            >
              <AddIcon fontSize="small" />
            </IconButton>
          </span>
        </Tooltip>
      </Stack>
      {notice && (
        <Alert severity="info" onClose={() => setNotice(null)} sx={{ mx: 1, mb: 1, py: 0 }}>
          {notice}
        </Alert>
      )}
      <List dense disablePadding>
        {rows.map(({ marker, depth }) => {
          const open = expanded.has(marker.id)
          // A row whose page is already in the tree can be folded even when the read that put it
          // there did not say it has children.
          const hasChildren = marker.hasChildren || !!childrenByParent[marker.id]?.loaded

          return (
            <ListItemButton
              key={marker.id}
              draggable
              onDragStart={(event) => {
                event.dataTransfer.setData(NODE_DRAG_TYPE, marker.id)
                event.dataTransfer.effectAllowed = 'copy'
              }}
              selected={selection?.kind === 'node' && selection.id === marker.id}
              onClick={() => {
                // A space is a world of its own, not a shape on this canvas: open it.
                if (marker.nodeKind === 'space') {
                  onOpenSpace(marker.id)
                  return
                }
                // The row is one node: of two pictures of one camera — the one the placement
                // draws, and its readings — only the row picked is selected, and the canvas marks
                // that one. What the camera is, the panel beside shows from the node itself.
                setSelection({ kind: 'node', id: marker.id })
                // A diagram is its own canvas: picking it opens it in place of the map.
                openDiagram(marker.nodeKind === 'diagram' ? marker.id : null)
              }}
              sx={{ cursor: 'grab', pl: 0.5 + depth * 1.5 }}
            >
              <Box sx={{ width: 24, flexShrink: 0, display: 'flex', justifyContent: 'center' }}>
                {hasChildren ? (
                  <IconButton
                    size="small"
                    sx={{ p: 0.1 }}
                    onClick={(event) => { event.stopPropagation(); toggle(marker.id) }}
                  >
                    {open
                      ? <ExpandMoreIcon sx={{ fontSize: 16 }} />
                      : <ChevronRightIcon sx={{ fontSize: 16 }} />}
                  </IconButton>
                ) : null}
              </Box>
              <KindIcon kind={marker.nodeKind} />
              <ListItemText
                primary={marker.name || marker.id}
                // A narrow sidebar cuts long names; the tooltip is how two of them are told apart.
                primaryTypographyProps={{ noWrap: true, variant: 'body2', title: marker.name || marker.id }}
                secondary={t(`maps.node.kind.${marker.nodeKind ?? 'object'}`)}
                secondaryTypographyProps={{ variant: 'caption' }}
              />
              {/* A device's own actions on its own row: draw it from a template, or open the
                  diagram it already has. Both act on the device, so a picture of it offers them
                  too — the dialog names the device, not the picture. */}
              {marker.nodeKind === 'camera' && marker.cameraId ? (
                <Tooltip title={t('diagramEditor.deviceDiagram')}>
                  <IconButton
                    size="small"
                    aria-label={t('diagramEditor.deviceDiagram')}
                    disabled={busy || diagramBusy}
                    onClick={(event) => { event.stopPropagation(); void showDeviceDiagram(marker) }}
                  >
                    <DashboardCustomizeIcon fontSize="small" />
                  </IconButton>
                </Tooltip>
              ) : null}
            </ListItemButton>
          )
        })}
        {rootsLoaded && roots.length === 0 && (
          <Typography variant="body2" color="text.secondary" sx={{ p: 1.5 }}>{t('maps.editor.noNodes')}</Typography>
        )}
      </List>
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
