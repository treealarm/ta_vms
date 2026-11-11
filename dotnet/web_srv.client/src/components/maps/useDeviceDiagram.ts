import { useCallback, useState } from 'react'
import { useAppDispatch } from '../../store'
import { attachDeviceDiagram } from '../../store/mapEditsSlice'
import { useEditorState } from './editorState'

/**
 * Draws the device's diagram on the canvas in front of the reader and opens it. The server keeps one
 * diagram per device and hangs it under the camera's picture on the canvas it is asked for, so
 * asking again for a camera already drawn here returns the node it has rather than making a second
 * one — the caller never has to look for "is it there yet".
 *
 * Answers whether it happened. A failure is the store's to say; the caller decides where to show it.
 */
export function useDeviceDiagram(spaceId: string | null | undefined) {
  const dispatch = useAppDispatch()
  const { setSelection, openDiagram } = useEditorState()
  const [busy, setBusy] = useState(false)

  const showDeviceDiagram = useCallback(async (cameraId: string): Promise<boolean> => {
    if (!spaceId) return false

    setBusy(true)
    try {
      const marker = await dispatch(attachDeviceDiagram({ cameraId, spaceId })).unwrap()
      if (!marker.id) return false
      setSelection({ kind: 'node', id: marker.id })
      openDiagram(marker.id)
      return true
    } catch {
      return false
    } finally {
      setBusy(false)
    }
  }, [dispatch, spaceId, setSelection, openDiagram])

  return { showDeviceDiagram, busy }
}
