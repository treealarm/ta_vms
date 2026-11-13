import { Fragment, useEffect, useState } from 'react'
import { Divider } from '@mui/material'
import { getControlObject } from '../../api/controlApi'
import ObjectSettingsCard from './ObjectSettingsCard'

/**
 * The settings card for a node an administrator selected — in the logical tree, on the map, or as
 * a picture. Only a device answers: a camera and a digital input have an arming mode, a relay and
 * a space do not, and the card is not drawn for them rather than failing on a click.
 *
 * `divider` is for a panel that holds other forms: the separator belongs to the card, so it comes
 * and goes with it rather than standing over nothing.
 */
export default function NodeSettingsCard(
  { nodeId, divider }: { nodeId: string; divider?: boolean }) {
  const [kind, setKind] = useState<string | null | undefined>(undefined)

  useEffect(() => {
    let live = true
    setKind(undefined)
    void getControlObject(nodeId)
      .then((row) => { if (live) setKind(row?.kind ?? null) })
      .catch(() => { if (live) setKind(null) })
    return () => { live = false }
  }, [nodeId])

  if (kind === undefined || kind === null)
    return null

  if (kind !== 'camera' && kind !== 'digital_input')
    return null

  return (
    <Fragment>
      {divider ? <Divider /> : null}
      <ObjectSettingsCard nodeId={nodeId} />
    </Fragment>
  )
}
