import { useEffect, useMemo, useState } from 'react'
import {
  Alert, Box, Button, Chip, Dialog, DialogContent, DialogTitle, IconButton, List,
  ListItemButton, ListItemText, Stack, TextField, Tooltip, Typography,
} from '@mui/material'
import ChevronRightIcon from '@mui/icons-material/ChevronRight'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import RefreshIcon from '@mui/icons-material/Refresh'
import { useTranslation } from 'react-i18next'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { mapApi } from '../../api/mapApi'
import { errorText } from '../../api/requestJson'
import type { MapMarker } from '../../dto/mapDtos'
import { useAppDispatch, useAppSelector } from '../../store'
import { loadCameras } from '../../store/liveSlice'
import { loadPlacements, loadSpaces } from '../../store/spatialSlice'
import { childrenOf, loadChildren, loadNodes, loadRoots, revealNode } from '../../store/objectsSlice'
import { loadVisualStates } from '../../store/statesSlice'
import { treeRows } from '../../map/nodeTree'
import KindIcon from '../../map/KindIcon'
import DetailToolbar from '../settings/DetailToolbar'
import MasterDetailLayout from '../settings/MasterDetailLayout'
import CameraUnifiedCard from '../CameraUnifiedCard'
import NodeStateChips from '../objects/NodeStateChips'
import DiagramViewer from '../diagrams/DiagramViewer'

/**
 * Admin → Nodes: the whole logical tree — server → camera → parts — loaded a level at a time and
 * searched on the server, because at a hundred thousand cameras "show everything" and "fetch
 * everything" are different things. It is not the map's palette (what stands on a space) and not
 * the camera page (a device's hardware): here a node's place in the tree is the subject.
 */
export default function NodesAdminPanel() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  // `?node=` is a way in from the map: the same link the camera's placement panel offers.
  const [searchParams] = useSearchParams()
  const linkedNode = searchParams.get('node')
  const dispatch = useAppDispatch()
  const markers = useAppSelector((s) => s.objects.markers)
  const pages = useAppSelector((s) => s.objects.childrenByParent)
  const roots = useAppSelector((s) => s.objects.roots)
  const camerasById = useAppSelector((s) => s.live.cameraStatesById)
  const placements = useAppSelector((s) => s.spatial.placements)
  const spaces = useAppSelector((s) => s.spatial.spaces)
  const states = useAppSelector((s) => s.states.states)
  const alarms = useAppSelector((s) => s.states.alarms)
  const descriptions = useAppSelector((s) => s.states.descriptions)

  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const [results, setResults] = useState<string[] | null>(null)
  const [searching, setSearching] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // The diagram open in a read-only viewer over the tree, and which one it is.
  const [viewer, setViewer] = useState<{ id: string; name: string } | null>(null)

  useEffect(() => {
    if (roots === null) void dispatch(loadRoots())
  }, [roots, dispatch])

  // Server-side, two characters at least: a lazy tree cannot find a token inside an unopened
  // server, and a client-side filter would only search what the reader happened to open.
  useEffect(() => {
    const trimmed = query.trim()
    if (trimmed.length < 2) return

    let cancelled = false
    const timer = window.setTimeout(() => {
      if (cancelled) return
      setSearching(true)
      void mapApi.searchDeviceNodes(trimmed)
        .then(async (response) => {
          if (cancelled) return
          const found = (response.markers ?? []).map((marker) => marker.id)
          // The parents come along so each result can say whose part it is; without them a token
          // that appears on two cameras would be two identical lines.
          const parents = (response.markers ?? []).map((m) => m.parentId).filter((id): id is string => !!id)
          if (found.length > 0)
            await dispatch(loadNodes([...new Set([...found, ...parents])])).unwrap()
          if (!cancelled) {
            setResults(found)
            setError(null)
          }
        })
        .catch((e) => { if (!cancelled) setError(errorText(e)) })
        .finally(() => { if (!cancelled) setSearching(false) })
    }, 300)

    return () => { cancelled = true; window.clearTimeout(timer) }
  }, [query, dispatch])

  const rootMarkers = useMemo(
    () => (roots ?? []).map((id) => markers[id]).filter((marker): marker is MapMarker => !!marker),
    [roots, markers],
  )
  const rows = useMemo(
    () => treeRows(rootMarkers, pages, markers, expanded),
    [rootMarkers, pages, markers, expanded],
  )

  // Clearing the field hides the results at once, without an effect writing state back.
  const shownResults = query.trim().length >= 2 ? results : null
  const shown: MapMarker[] = shownResults !== null
    ? shownResults.map((id) => markers[id]).filter((marker): marker is MapMarker => !!marker)
    : rows.map((row) => row.marker)
  const depthOf = (id: string) => rows.find((row) => row.marker.id === id)?.depth ?? 0

  const toggle = (id: string) => {
    const next = new Set(expanded)
    if (next.has(id)) {
      next.delete(id)
    } else {
      next.add(id)
      if (!pages[id]?.loaded) void dispatch(loadChildren({ parentId: id }))
    }
    setExpanded(next)
  }

  const openInTree = async (marker: MapMarker) => {
    setQuery('')
    setResults(null)
    const chain = await dispatch(revealNode(marker)).unwrap().catch(() => [] as MapMarker[])
    const next = new Set(expanded)
    for (const ancestor of chain) next.add(ancestor.id)
    if (marker.parentId) next.add(marker.parentId)
    setExpanded(next)
    setSelectedId(marker.id)
  }

  // A link from the map names a node the page has not read yet: read it, then unfold the path.
  // Re-running on every marker change would fight the reader's own navigation.
  useEffect(() => {
    if (!linkedNode) return
    void (async () => {
      const loaded = await dispatch(loadNodes([linkedNode])).unwrap().catch(() => null)
      const marker = loaded?.markers.find((candidate) => candidate.id === linkedNode)
      if (marker) await openInTree(marker)
    })()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [linkedNode, dispatch])

  /**
   * The diagram of a device hangs under the picture of its camera on the canvas, so opening it from
   * that row means reading the row's own children first. A diagram node opens as itself.
   */
  const openDiagram = async (marker: MapMarker) => {
    if (marker.nodeKind === 'diagram') {
      setViewer({ id: marker.id, name: marker.name || marker.id })
      return
    }
    const parentId = marker.id
    try {
      const page = pages[parentId]?.loaded
        ? null
        : await dispatch(loadChildren({ parentId })).unwrap()
      const children = page?.children ?? childrenOf(markers, pages, parentId)
      const diagram = children.find((child) => child.nodeKind === 'diagram')
      if (diagram) setViewer({ id: diagram.id, name: diagram.name || diagram.id })
      else setError(t('diagramEditor.noDeviceDiagram'))
    } catch (e) {
      setError(errorText(e))
    }
  }

  const selected = selectedId ? markers[selectedId] : null

  // The row is a device like any other: the camera behind it is shown here as the map shows it,
  // and its states are the device's own, not the tree's.
  useEffect(() => {
    if (Object.keys(camerasById).length === 0) void dispatch(loadCameras())
    void dispatch(loadPlacements())
    void dispatch(loadSpaces())
  }, [camerasById, dispatch])

  useEffect(() => {
    if (selectedId) void dispatch(loadVisualStates([selectedId]))
  }, [selectedId, dispatch])

  const selectedCamera = selected?.cameraId ? camerasById[selected.cameraId] : undefined
  const reference = selected?.cameraId
    ? placements.find((placement) => placement.cameraId === selected.cameraId && placement.isReference)
    : undefined
  const referenceSpace = spaces.find((space) => space.id === reference?.spaceId)

  // A part is its own node kind now: the server resolves it from device_node, not from a guess
  // about the parent.
  const isPart = selected?.nodeKind === 'part'

  return (
    <>
    <MasterDetailLayout
      listTitle={t('nodes.title')}
      listWidth={300}
      listActions={(
        <Tooltip title={t('common.refresh')}>
          <IconButton
            size="small"
            onClick={() => {
              setQuery('')
              setResults(null)
              void dispatch(loadRoots())
              for (const id of expanded) void dispatch(loadChildren({ parentId: id }))
            }}
          >
            <RefreshIcon fontSize="small" />
          </IconButton>
        </Tooltip>
      )}
      list={(
        <Box>
          <Box sx={{ p: 1.5, borderBottom: 1, borderColor: 'divider' }}>
            <TextField
              fullWidth size="small" label={t('nodes.search')}
              placeholder={t('nodes.searchHint')}
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              helperText={shownResults !== null ? t('nodes.results', { count: shown.length }) : undefined}
            />
          </Box>
          <List dense disablePadding>
            {shown.map((marker) => {
              const hasChildren = marker.hasChildren || !!pages[marker.id]?.loaded
              const depth = shownResults !== null ? Math.min(depthOf(marker.id), 1) : depthOf(marker.id)
              return (
                <ListItemButton
                  key={marker.id}
                  selected={selectedId === marker.id}
                  onClick={() => setSelectedId(marker.id)}
                  sx={{ pl: 0.5 + depth * 1.5 }}
                >
                  <Box sx={{ width: 24, flexShrink: 0, display: 'flex', justifyContent: 'center' }}>
                    {shownResults === null && hasChildren ? (
                      <IconButton
                        size="small" sx={{ p: 0.1 }}
                        onClick={(event) => { event.stopPropagation(); toggle(marker.id) }}
                      >
                        {expanded.has(marker.id)
                          ? <ExpandMoreIcon sx={{ fontSize: 16 }} />
                          : <ChevronRightIcon sx={{ fontSize: 16 }} />}
                      </IconButton>
                    ) : null}
                  </Box>
                  <KindIcon kind={marker.nodeKind ?? 'object'} />
                  <ListItemText
                    primary={marker.name || marker.id}
                    secondary={shownResults !== null
                      ? `${markers[marker.parentId ?? '']?.name ?? ''} · ${marker.nodeKind ?? 'object'}`
                      : t(`maps.node.kind.${marker.nodeKind ?? 'object'}`)}
                    primaryTypographyProps={{ noWrap: true, variant: 'body2' }}
                    secondaryTypographyProps={{ noWrap: true, variant: 'caption' }}
                  />
                </ListItemButton>
              )
            })}
            {shownResults === null && roots !== null && rows.length === 0 && (
              <Typography variant="body2" color="text.secondary" sx={{ p: 1.5 }}>
                {t('nodes.empty')}
              </Typography>
            )}
            {shownResults !== null && !searching && shown.length === 0 && (
              <Typography variant="body2" color="text.secondary" sx={{ p: 1.5 }}>
                {t('nodes.noResults')}
              </Typography>
            )}
          </List>
        </Box>
      )}
    >
      {!selected ? (
        <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
          {t('nodes.pick')}
        </Typography>
      ) : (
        <Box sx={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 }}>
          <DetailToolbar
            title={selected.name || selected.id}
            subtitle={`${selected.id} · ${selected.nodeKind ?? 'object'}`}
            dirty={false}
            hideApply
            extra={(
              <>
                {(selected.nodeKind === 'diagram'
                  || (selected.nodeKind === 'camera' && selected.cameraId)) && (
                  <Button size="small" onClick={() => void openDiagram(selected)}>
                    {selected.nodeKind === 'diagram'
                      ? t('nodes.openDiagram')
                      : t('diagramEditor.openDeviceDiagram')}
                  </Button>
                )}
                <Button size="small" onClick={() => void openInTree(selected)}>
                  {t('nodes.openInTree')}
                </Button>
              </>
            )}
          />
          <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', p: 2 }}>
            <Stack spacing={2}>
              {error && <Alert severity="error" onClose={() => setError(null)}>{error}</Alert>}

              <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap>
                <Chip size="small" variant="outlined" label={selected.nodeKind ?? 'object'} />
                {selected.cameraId && (
                  <Chip size="small" variant="outlined" label={`camera ${selected.cameraId}`} />
                )}
                {selected.ownerId && (
                  <Chip size="small" variant="outlined" color="info" label={t('nodes.replica')} />
                )}
              </Stack>

              {selected.nodeKind === 'part' || selectedCamera ? (
                <Box>
                  <Typography variant="subtitle2" sx={{ mb: 1 }}>{t('maps.node.states')}</Typography>
                  {alarms[selected.id]?.alarm || (states[selected.id]?.states?.length ?? 0) > 0 ? (
                    <NodeStateChips
                      state={states[selected.id]}
                      alarm={alarms[selected.id]}
                      descriptions={descriptions}
                    />
                  ) : (
                    <Typography variant="body2" color="text.secondary">{t('maps.node.noStates')}</Typography>
                  )}
                </Box>
              ) : null}

              {selected?.cameraId ? (
                <Stack direction="row" spacing={1} alignItems="center" sx={{ flexWrap: 'wrap' }} useFlexGap>
                  <Typography variant="body2" color="text.secondary">
                    {reference
                      ? t('nodes.standsIn', { name: referenceSpace?.name ?? reference.spaceId })
                      : t('nodes.standsNowhere')}
                  </Typography>
                  {reference ? (
                    <Button
                      size="small"
                      onClick={() => navigate(`/admin/maps?camera=${encodeURIComponent(selected.cameraId!)}`)}
                    >
                      {t('nodes.showOnMap')}
                    </Button>
                  ) : null}
                </Stack>
              ) : null}

              {selectedCamera ? (
                <Box sx={{ height: 340, border: 1, borderColor: 'divider', borderRadius: 1, overflow: 'hidden' }}>
                  <CameraUnifiedCard camera={selectedCamera} slotKey={`nodes:${selectedCamera.cameraId}`} />
                </Box>
              ) : null}

              {isPart && (
                <Box>
                  <Typography variant="subtitle2">{t('nodes.part')}</Typography>
                  <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
                    {t('nodes.partHint')}
                  </Typography>
                  <Button
                    variant="outlined" size="small"
                    onClick={() => navigate(`/operator/map?node=${encodeURIComponent(selected.id)}`)}
                  >
                    {t('nodes.statesEvents')}
                  </Button>
                </Box>
              )}
            </Stack>
          </Box>
        </Box>
      )}
    </MasterDetailLayout>
    <Dialog open={viewer !== null} onClose={() => setViewer(null)} maxWidth="lg" fullWidth>
      <DialogTitle>{viewer?.name}</DialogTitle>
      <DialogContent sx={{ height: '70vh', display: 'flex', flexDirection: 'column', pt: 1 }}>
        {viewer && (
          <DiagramViewer
            diagramId={viewer.id}
            selectedId={null}
            onPick={() => undefined}
            onOpen={(marker) => {
              if (marker.nodeKind === 'diagram') {
                setViewer({ id: marker.id, name: marker.name || marker.id })
                return
              }
              // Out of the diagram and onto where it hangs: the camera's picture, or the space.
              setViewer(null)
              void openInTree(marker)
            }}
            editing={false}
          />
        )}
      </DialogContent>
    </Dialog>
    </>
  )
}
