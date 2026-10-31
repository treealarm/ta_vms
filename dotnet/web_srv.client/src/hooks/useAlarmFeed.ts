import { useEffect, useMemo } from 'react'
import { useAppDispatch, useAppSelector } from '../store'
import { loadNodes } from '../store/objectsSlice'
import { loadAlarmedObjects } from '../store/statesSlice'
import { RECENT_ALARM_MINUTES, loadRecentAlarms } from '../store/eventsSlice'

/** Running alarms are a state, not an event; a slow poll keeps the list right across replicas. */
const ALARM_POLL_MS = 15_000
/** Recently ended alarms change far less often. */
const RECENT_POLL_MS = 60_000

/**
 * The operator's alarm feed: every alarmed node the caller may see, plus the alarm events of the
 * last minutes for the rings of alarms that have already ended. Both live in Redux; this hook only
 * refreshes them and loads the names of the nodes it mentions.
 */
export function useAlarmFeed(): void {
  const dispatch = useAppDispatch()
  const all = useAppSelector((s) => s.states.all)
  const recent = useAppSelector((s) => s.events.recentAlarms)
  const markers = useAppSelector((s) => s.objects.markers)

  useEffect(() => {
    void dispatch(loadAlarmedObjects())
    void dispatch(loadRecentAlarms(RECENT_ALARM_MINUTES))

    const alarmTimer = window.setInterval(() => void dispatch(loadAlarmedObjects()), ALARM_POLL_MS)
    const recentTimer = window.setInterval(
      () => void dispatch(loadRecentAlarms(RECENT_ALARM_MINUTES)), RECENT_POLL_MS)
    return () => {
      window.clearInterval(alarmTimer)
      window.clearInterval(recentTimer)
    }
  }, [dispatch])

  const idsKey = useMemo(() => {
    const ids = [
      ...all.map((alarm) => alarm.id),
      ...recent.map((event) => event.objectId),
    ].filter((id): id is string => !!id)

    return [...new Set(ids)].sort().join(',')
  }, [all, recent])

  useEffect(() => {
    const ids = idsKey ? idsKey.split(',') : []
    const missing = ids.filter((id) => !markers[id])
    if (missing.length > 0) void dispatch(loadNodes(missing))
  }, [idsKey, markers, dispatch])
}
