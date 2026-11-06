import { useEffect, useMemo, useState } from 'react'
import {
  Alert, Box, Button, Chip, Divider, IconButton, List, ListItemButton, ListItemText, MenuItem,
  Stack, TextField, Tooltip, Typography,
} from '@mui/material'
import AddLocationAltIcon from '@mui/icons-material/AddLocationAlt'
import ChevronRightIcon from '@mui/icons-material/ChevronRight'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import RefreshIcon from '@mui/icons-material/Refresh'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router-dom'
import { mapApi } from '../../api/mapApi'
import { errorText } from '../../api/requestJson'
import type { MapMarker } from '../../dto/mapDtos'
import type { CameraPlacementDto, SpaceDto } from '../../dto/dtos'
import { useAppDispatch, useAppSelector } from '../../store'
import { loadChildren, loadNodes, loadRoots, revealNode } from '../../store/objectsSlice'
import { loadSpaces, upsertPlacement } from '../../store/spatialSlice'
import { treeRows } from '../../map/nodeTree'
import KindIcon from '../../map/KindIcon'
import DetailToolbar from '../settings/DetailToolbar'
import MasterDetailLayout from '../settings/MasterDetailLayout'

/**
 * Admin → Nodes: the whole logical tree — server → camera → parts — loaded a level at a time and
 * searched on the server, because at a hundred thousand cameras "show everything" and "fetch
 * everything" are different things. It is not the map's palette (what stands on a space) and not
 * the camera page (a device's hardware): here a node's place in the tree is the subject.
 */
export default function NodesAdminPanel() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const dispatch = useAppDispatch()
  const markers = useAppSelector((s) => s.objects.markers)
  const pages = useAppSelector((s) => s.objects.childrenByParent)
  const roots = useAppSelector((s) => s.objects.roots)
  const spaces = useAppSelector((s) => s.spatial.spaces)

  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const [results, setResults] = useState<string[] | null>(null)
  const [searching, setSearching] = useState(false)
  const [spaceId, setSpaceId] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  useEffect(() => {
    if (roots === null) void dispatch(loadRoots())
    if (spaces.length === 0) void dispatch(loadSpaces())
  }, [roots, spaces.length, dispatch])

  // Derived, not written back by an effect: a chosen space wins, otherwise the first one is the
  // one the form starts on.
  const chosenSpaceId = spaceId || spaces[0]?.id || ''

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

  const selected = selectedId ? markers[selectedId] : null

  const placeOnMap = async () => {
    if (!selected?.cameraId || !chosenSpaceId) return
    const space = spaces.find((s) => s.id === chosenSpaceId)
    const lat = space ? (space.view.southWest.lat + space.view.northEast.lat) / 2 : 0
    const lon = space ? (space.view.southWest.lon + space.view.northEast.lon) / 2 : 0

    const placement: CameraPlacementDto = {
      cameraId: selected.cameraId,
      spaceId: chosenSpaceId,
      // The first placement of a camera becomes its reference in the service; a later one is a
      // picture, and the reference is never moved from here.
      isReference: false,
      lat,
      lon,
      heightM: 3,
      headingDeg: 0,
      hfovDeg: 60,
      rangeM: 30,
    }

    try {
      await dispatch(upsertPlacement(placement)).unwrap()
      setNotice(t('nodes.placed', { name: space?.name ?? '' }))
      setError(null)
    } catch (e) {
      setError(errorText(e))
    }
  }

  // A part is its own node kind now: the server resolves it from device_node, not from a guess
  // about the parent.
  const isPart = selected?.nodeKind === 'part'

  return (
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
              <Button size="small" onClick={() => void openInTree(selected)}>
                {t('nodes.openInTree')}
              </Button>
            )}
          />
          <Box sx={{ flex: 1, minHeight: 0, overflowY: 'auto', p: 2 }}>
            <Stack spacing={2}>
              {error && <Alert severity="error" onClose={() => setError(null)}>{error}</Alert>}
              {notice && <Alert severity="success" onClose={() => setNotice(null)}>{notice}</Alert>}

              <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap>
                <Chip size="small" variant="outlined" label={selected.nodeKind ?? 'object'} />
                {selected.cameraId && (
                  <Chip size="small" variant="outlined" label={`camera ${selected.cameraId}`} />
                )}
                {selected.ownerId && (
                  <Chip size="small" variant="outlined" color="info" label={t('nodes.replica')} />
                )}
              </Stack>

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

              {selected.nodeKind === 'camera' && selected.cameraId && (
                <>
                  <Divider />
                  <Box>
                    <Typography variant="subtitle2">{t('nodes.placeOnMap')}</Typography>
                    <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
                      {t('nodes.placeHint')}
                    </Typography>
                    <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap" useFlexGap>
                      <TextField
                        select size="small" label={t('nodes.space')}
                        value={chosenSpaceId}
                        onChange={(e) => setSpaceId(e.target.value)}
                        sx={{ minWidth: 240 }}
                      >
                        {spaces.map((space: SpaceDto) => (
                          <MenuItem key={space.id} value={space.id ?? ''}>{space.name}</MenuItem>
                        ))}
                      </TextField>
                      <Button
                        variant="contained" size="small"
                        startIcon={<AddLocationAltIcon fontSize="small" />}
                        disabled={!chosenSpaceId}
                        onClick={() => void placeOnMap()}
                      >
                        {t('nodes.place')}
                      </Button>
                    </Stack>
                  </Box>
                </>
              )}
            </Stack>
          </Box>
        </Box>
      )}
    </MasterDetailLayout>
  )
}
