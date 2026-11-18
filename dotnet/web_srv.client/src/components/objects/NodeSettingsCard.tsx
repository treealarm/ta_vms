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
  // The answer keeps the node it belongs to: the previous node's mode is not this one's, and the
  // card stays hidden until this node answers.
  const [answer, setAnswer] = useState<{ nodeId: string; kind: string | null } | null>(null)

  useEffect(() => {
    let live = true
    void getControlObject(nodeId)
      .then((row) => { if (live) setAnswer({ nodeId, kind: row?.kind ?? null }) })
      .catch(() => { if (live) setAnswer({ nodeId, kind: null }) })
    return () => { live = false }
  }, [nodeId])

  const kind = answer?.nodeId === nodeId ? answer.kind : undefined

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
