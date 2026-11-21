import { useCallback, useEffect, useRef, useState } from 'react'
import {
  Alert,
  Button,
  Stack,
  ToggleButton,
  ToggleButtonGroup,
  Tooltip,
  Typography,
} from '@mui/material'
import TimerOutlinedIcon from '@mui/icons-material/TimerOutlined'
import { useTranslation } from 'react-i18next'

import { getNodeArming, setNodeArming } from '../../api/controlApi'
import { errorText } from '../../api/requestJson'
import BypassUntilDialog from './BypassUntilDialog'
import { formatUntil } from './bypassTime'
import { guardState } from './guardState'
import type { ArmMode, NodeArmingDto } from '../../dto/dtos'

type Props = {
  /** The node to guard: a space, a zone, a camera or one of its parts. */
  objectId: string
  /** Re-read the surroundings after a write — a zone's arming changes every sensor under it. */
  onChanged?: () => void
  /** How often to re-read while mounted. `Watch` does not stream properties, so this is the
   * only way another operator's change reaches this screen. */
  refreshMs?: number
}

const MODE_LABEL: Record<ArmMode, string> = {
  inherit: 'control.guardInherit',
  armed: 'control.guardOn',
  disarmed: 'control.guardOff',
}

/**
 * The guard switch of one node, whatever kind it is. Three modes for everyone — armed, disarmed and
 * "inherit", which hands the node back to the container it stands in. An explicit on or off is the
 * operator's own decision and outranks that container; `inherit` takes the override back.
 *
 * There is no optimistic toggle on purpose: the effective arming is a property of the whole
 * ancestor chain. A device left in `inherit` follows the zone it stands in; an explicit on or off
 * is the operator's own decision and outranks that zone, so only the server's answer says what
 * really happened. The answer paints the control; the parent re-reads its own rows because a
 * zone's arming changes every device beneath it that still inherits.
 */
export default function NodeGuard({ objectId, onChanged, refreshMs = 5000 }: Props) {
  const { t, i18n } = useTranslation()
  const [arming, setArming] = useState<NodeArmingDto | null>(null)
  const [loaded, setLoaded] = useState(false)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [bypassOpen, setBypassOpen] = useState(false)
  const alive = useRef(true)

  const load = useCallback(async () => {
    try {
      const next = await getNodeArming(objectId)
      if (!alive.current) return
      setArming(next)
      setLoaded(true)
    } catch (e) {
      if (alive.current) setError(errorText(e))
    }
  }, [objectId])

  useEffect(() => {
    alive.current = true
    setArming(null)
    setLoaded(false)
    setError(null)
    setBypassOpen(false)
    void load()
    return () => {
      alive.current = false
    }
  }, [load])

  useEffect(() => {
    if (refreshMs <= 0) return
    const timer = setInterval(() => void load(), refreshMs)
    return () => clearInterval(timer)
  }, [load, refreshMs])

  const apply = async (mode: ArmMode, until?: string | null) => {
    if (pending || arming === null) return
    setPending(true)
    setError(null)
    try {
      const updated = await setNodeArming(objectId, mode, until)
      setArming(updated)
      onChanged?.()
    } catch (e) {
      setError(errorText(e))
      // The server refused: show what is really there rather than the click.
      void load()
    } finally {
      setPending(false)
    }
  }

  if (!loaded && arming === null)
    return <Typography variant="caption" color="text.secondary">{t('control.guardLoading')}</Typography>

  if (arming === null || !arming.armable)
    return (
      <Typography variant="caption" color="text.secondary">
        {arming === null ? t('control.guardNotANode') : t('control.guardNotArmable')}
      </Typography>
    )

  const state = guardState({
    armed: arming.armed,
    source: arming.source,
    bypassed: arming.bypassed,
    bypassUntil: arming.bypassUntil,
    surface: 'container',
  })
  const label = state.timed && arming.bypassUntil
    ? t(state.labelKey, { time: formatUntil(arming.bypassUntil, i18n.language) })
    : t(state.labelKey)

  const blocked = !arming.configured
    ? t('control.notConfigured')
    : pending
      ? t('control.guardPending')
      : null

  const group = (
    <ToggleButtonGroup
      size="small"
      exclusive
      value={arming.modes.includes(arming.mode as ArmMode) ? arming.mode : ''}
      onChange={(_, value: ArmMode | null) => {
        if (value === null) return
        void apply(value)
      }}
    >
      {arming.modes.map((mode) => (
        <ToggleButton key={mode} value={mode} disabled={pending || !arming.configured}>
          {t(MODE_LABEL[mode])}
        </ToggleButton>
      ))}
    </ToggleButtonGroup>
  )

  return (
    <Stack spacing={0.75}>
      <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap">
        {blocked ? <Tooltip title={blocked}><span>{group}</span></Tooltip> : group}
        <Typography
          variant="caption"
          // Green when set here, purple when an ancestor settled it, amber when an armed ancestor
          // excludes this node, grey when nothing is armed.
          color={state.color}
        >
          {label}
        </Typography>
      </Stack>
      <Button
        size="small"
        variant="outlined"
        startIcon={<TimerOutlinedIcon />}
        disabled={pending || !arming.configured}
        onClick={() => setBypassOpen(true)}
        sx={{ alignSelf: 'flex-start' }}
      >
        {t('control.bypassUntil')}
      </Button>
      {error && <Alert severity="error" sx={{ fontSize: '0.8rem' }}>{error}</Alert>}
      <BypassUntilDialog
        open={bypassOpen}
        busy={pending}
        onClose={() => setBypassOpen(false)}
        onApply={(until) => {
          setBypassOpen(false)
          void apply('disarmed', until)
        }}
      />
    </Stack>
  )
}
