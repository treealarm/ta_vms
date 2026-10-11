import { useEffect, useState } from 'react'
import { CircleMarker, Tooltip } from 'react-leaflet'
import type { CameraPlacementDto, GroundMatrix, GroundProjectionDto } from '../dto/dtos'
import { useAppSelector } from '../store'
import { spatialRequest } from '../store/spatialSlice'
import { useLiveBoxes } from '../hooks/useLiveBoxes'
import { shortObjectId, trackColor } from '../components/bbox/bboxDraw'
import { footOf, project } from './groundProjection'
import { distance } from './geo'

/** Marks are changed by an administrator, rarely; the same pace as placements on the operator's map. */
const PROJECTION_POLL_MS = 30000

/**
 * What the fixed cameras calibrated against the ground see right now, as points on the map of a
 * space: every camera whose reference placement shares geometry with it, as the server decides.
 * The detections are the ones the video overlay draws, from the same live stream.
 */
export default function DetectionsLayer({ spaceId }: { spaceId: string }) {
  const [projections, setProjections] = useState<GroundProjectionDto[]>([])
  const placements = useAppSelector((s) => s.spatial.placements)
  const camerasById = useAppSelector((s) => s.live.cameraStatesById)

  useEffect(() => {
    let stale = false
    const load = () => spatialRequest<GroundProjectionDto[]>(`/api/ground-geo?spaceId=${encodeURIComponent(spaceId)}`)
      .then((list) => { if (!stale) setProjections(list) })
      // A map without detections is still a map; the next poll tries again.
      .catch(() => undefined)
    void load()
    const timer = window.setInterval(() => void load(), PROJECTION_POLL_MS)
    return () => {
      stale = true
      clearInterval(timer)
    }
  }, [spaceId])

  return projections.map((p) => (
    <CameraDetections
      key={p.cameraId}
      cameraId={p.cameraId}
      matrix={p.matrix}
      reference={placements.find((r) => r.cameraId === p.cameraId && r.isReference)}
      label={camerasById[p.cameraId]?.name || p.cameraId}
    />
  ))
}

type CameraProps = {
  cameraId: string
  matrix: GroundMatrix
  /** Where the camera really stands. Points beyond its range are not drawn. */
  reference?: CameraPlacementDto
  label: string
}

/**
 * One camera's detections where they stand: the bottom middle of each box — feet, wheels — taken
 * through the camera's ground mapping. Faces and plates are left out: they sit on a person or a
 * vehicle that is already there. Coloured as the box is on the video, so the two can be matched.
 */
export function CameraDetections({ cameraId, matrix, reference, label }: CameraProps) {
  const boxes = useLiveBoxes(cameraId)

  return boxes.map((box) => {
    const foot = footOf(box)
    const at = foot && project(matrix, foot.x, foot.y)
    // Near the horizon a pixel spans kilometres; past the range the camera is set to see, a point
    // says more about the fit than about the object.
    if (!at || (reference && distance(reference, at) > reference.rangeM)) return null
    const color = trackColor(box.trackId)
    return (
      <CircleMarker
        key={`${box.source}|${box.trackId}|${box.kind}`}
        center={[at.lat, at.lon]}
        radius={6}
        pathOptions={{ color: '#fff', weight: 1.5, fillColor: color, fillOpacity: 0.9 * box.opacity, opacity: box.opacity }}
      >
        <Tooltip direction="top" offset={[0, -6]}>
          {label} · {box.label || box.kind}{box.objectId ? ` · ${shortObjectId(box.objectId)}` : ''}
        </Tooltip>
      </CircleMarker>
    )
  })
}
