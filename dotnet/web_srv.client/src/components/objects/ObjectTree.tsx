import { createContext, useContext, useEffect, useMemo, useRef } from 'react'
import {
  Box, Checkbox, CircularProgress, IconButton, List, ListItemButton, Stack, Tooltip, Typography,
} from '@mui/material'
import ChevronRightIcon from '@mui/icons-material/ChevronRight'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import { useTranslation } from 'react-i18next'
import type { MapAlarmState, MapMarker } from '../../dto/mapDtos'
import { useAppDispatch, useAppSelector } from '../../store'
import { loadChildren, loadRoots, type ChildrenPage } from '../../store/objectsSlice'
import { loadVisualStates } from '../../store/statesSlice'
import KindIcon from '../../map/KindIcon'
import { sortMarkers } from '../../map/nodeKind'
import { ALARM_COLOR } from '../../map/nodeStyle'

type Props = {
  selectedId: string | null
  onSelect: (marker: MapMarker) => void
  checkedIds: Set<string>
  onCheckedChange: (ids: Set<string>) => void
  /** The open rows. The page owns them, so a search result can unfold the path down to itself. */
  expanded: Set<string>
  onExpandedChange: (ids: Set<string>) => void
  /**
   * A counter bumped when the tree is asked to show what is selected: the row scrolls into view.
   * The selection alone does not move the tree — clicking a row must not shift the one clicked.
   */
  reveal?: number
}

type TreeContextValue = {
  markers: Record<string, MapMarker>
  childrenByParent: Record<string, ChildrenPage>
  alarms: Record<string, MapAlarmState>
  selectedId: string | null
  checkedIds: Set<string>
  expanded: Set<string>
  reveal: number
  onToggle: (id: string) => void
  onSelect: (marker: MapMarker) => void
  onCheck: (id: string, checked: boolean) => void
  onMore: (id: string, startId: string) => void
}

const TreeContext = createContext<TreeContextValue | null>(null)

/**
 * One tree of nodes: spaces, cameras, diagrams, figures and plain objects, loaded a level at a
 * time. Alarm points climb: a node with an alarmed descendant is marked even when it is collapsed,
 * because the counter lives on the node itself (`children_alarms`).
 */
export default function ObjectTree({
  selectedId, onSelect, checkedIds, onCheckedChange, expanded, onExpandedChange, reveal = 0,
}: Props) {
  const { t } = useTranslation()
  const dispatch = useAppDispatch()
  const roots = useAppSelector((s) => s.objects.roots)
  const markers = useAppSelector((s) => s.objects.markers)
  const childrenByParent = useAppSelector((s) => s.objects.childrenByParent)
  const alarms = useAppSelector((s) => s.states.alarms)
  const states = useAppSelector((s) => s.states.states)
  const busy = useAppSelector((s) => s.objects.busy)

  useEffect(() => {
    if (roots === null) void dispatch(loadRoots())
  }, [roots, dispatch])

  // Load the states of everything the tree has shown so far; the watch stream keeps them fresh
  // afterwards. Requested ids are remembered even when no state comes back, so a node without
  // states is not asked for on every render.
  const markerIds = useMemo(() => Object.keys(markers), [markers])
  const requested = useRef(new Set<string>())
  useEffect(() => {
    const missing = markerIds.filter(
      (id) => !requested.current.has(id) && !states[id] && !alarms[id],
    )
    if (missing.length === 0) return

    for (const id of missing) requested.current.add(id)
    void dispatch(loadVisualStates(missing))
  }, [markerIds, states, alarms, dispatch])

  const toggle = (id: string) => {
    const opening = !expanded.has(id)
    const next = new Set(expanded)
    if (opening) next.add(id)
    else next.delete(id)
    onExpandedChange(next)

    if (opening && !childrenByParent[id]?.loaded) void dispatch(loadChildren({ parentId: id }))
  }

  const check = (id: string, checked: boolean) => {
    const next = new Set(checkedIds)
    if (checked) next.add(id)
    else next.delete(id)
    onCheckedChange(next)
  }

  const more = (id: string, startId: string) => {
    void dispatch(loadChildren({ parentId: id, startId }))
  }

  const context: TreeContextValue = {
    markers,
    childrenByParent,
    alarms,
    selectedId,
    checkedIds,
    expanded,
    reveal,
    onToggle: toggle,
    onSelect,
    onCheck: check,
    onMore: more,
  }

  if (roots === null) {
    return (
      <Stack alignItems="center" justifyContent="center" sx={{ p: 2 }}>
        {busy > 0 ? <CircularProgress size={20} /> : null}
      </Stack>
    )
  }

  if (roots.length === 0) {
    return <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>{t('maps.tree.empty')}</Typography>
  }

  return (
    <TreeContext.Provider value={context}>
      <List dense disablePadding>
        {sortMarkers(roots.map((id) => markers[id]).filter((marker): marker is MapMarker => !!marker))
          .map((marker) => <TreeRow key={marker.id} marker={marker} depth={0} />)}
      </List>
    </TreeContext.Provider>
  )
}

function TreeRow({ marker, depth }: { marker: MapMarker; depth: number }) {
  const ctx = useContext(TreeContext)
  const { t } = useTranslation()
  const rowRef = useRef<HTMLDivElement | null>(null)
  const selected = ctx?.selectedId === marker.id
  const reveal = ctx?.reveal ?? 0

  // The tree was asked to show this node: it may have just been unfolded above, so the answer is
  // read on every mount and whenever the ask is repeated.
  useEffect(() => {
    if (selected && reveal > 0) rowRef.current?.scrollIntoView({ block: 'center' })
  }, [selected, reveal])

  if (!ctx) return null

  const page = ctx.childrenByParent[marker.id]
  const open = ctx.expanded.has(marker.id)
  // A row whose page is already in the tree can be folded even when the read that put it there
  // did not say it has children — the parents chain behind a search result does not.
  const hasChildren = marker.hasChildren || !!page?.loaded
  const alarm = ctx.alarms[marker.id]
  const alarming = !!(alarm?.alarm || (alarm?.childrenAlarms ?? 0) > 0)
  const children = open && page?.loaded
    ? sortMarkers(page.ids.map((id) => ctx.markers[id]).filter((child): child is MapMarker => !!child))
    : []

  return (
    <>
      <ListItemButton
        ref={rowRef}
        selected={selected}
        onClick={() => ctx.onSelect(marker)}
        sx={{ pl: 0.5 + depth * 1.5, pr: 0.5, py: 0.1, minHeight: 30 }}
      >
        <Box sx={{ width: 24, flexShrink: 0, display: 'flex', justifyContent: 'center' }}>
          {hasChildren ? (
            <IconButton
              size="small"
              sx={{ p: 0.1 }}
              onClick={(event) => { event.stopPropagation(); ctx.onToggle(marker.id) }}
            >
              {open ? <ExpandMoreIcon sx={{ fontSize: 16 }} /> : <ChevronRightIcon sx={{ fontSize: 16 }} />}
            </IconButton>
          ) : null}
        </Box>
        <KindIcon kind={marker.nodeKind} />
        <Typography
          variant="body2"
          noWrap
          sx={{ flex: 1, minWidth: 0, ml: 0.75, fontWeight: selected ? 600 : 400 }}
        >
          {marker.name || marker.id}
        </Typography>
        {alarming ? (
          <Tooltip title={alarm?.alarm
            ? t('maps.node.alarm')
            : t('maps.tree.childAlarms', { count: alarm?.childrenAlarms ?? 0 })}>
            <Stack direction="row" spacing={0.25} alignItems="center" sx={{ mr: 0.25 }}>
              <Box
                sx={{
                  width: 8,
                  height: 8,
                  borderRadius: '50%',
                  bgcolor: alarm?.alarm ? ALARM_COLOR : 'transparent',
                  border: `2px solid ${ALARM_COLOR}`,
                  boxSizing: 'border-box',
                }}
              />
              {!alarm?.alarm && (alarm?.childrenAlarms ?? 0) > 0 ? (
                <Typography variant="caption" sx={{ color: ALARM_COLOR, lineHeight: 1 }}>
                  {alarm?.childrenAlarms}
                </Typography>
              ) : null}
            </Stack>
          </Tooltip>
        ) : null}
        <Checkbox
          size="small"
          checked={ctx.checkedIds.has(marker.id)}
          onClick={(event) => event.stopPropagation()}
          onChange={(event) => ctx.onCheck(marker.id, event.target.checked)}
          sx={{ p: 0.25, ml: 0.25 }}
        />
      </ListItemButton>
      {open && page && !page.loaded ? (
        <Stack alignItems="center" sx={{ py: 0.5 }}>
          <CircularProgress size={14} />
        </Stack>
      ) : null}
      {children.map((child) => <TreeRow key={child.id} marker={child} depth={depth + 1} />)}
      {open && page?.loaded && page.hasMore ? (
        <Box sx={{ pl: 4 + depth * 1.5, py: 0.25 }}>
          <Typography
            variant="caption"
            color="primary"
            sx={{ cursor: 'pointer' }}
            onClick={() => ctx.onMore(marker.id, page.ids[page.ids.length - 1] ?? '')}
          >
            {t('maps.tree.more')}
          </Typography>
        </Box>
      ) : null}
    </>
  )
}
