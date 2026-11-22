import type { GeoPointDto } from '../dto/dtos'
import type { MapGeometry } from '../dto/mapDtos'
import type { NodeDetails } from '../store/mapEditsSlice'
import type { VisualImage } from './visualImage'

/** The two free figures a plan gets that its shape tools do not make: a caption and a picture. */
export type FreeFigureKind = 'text' | 'picture'

/** The colour a new caption starts with; the administrator changes it on the Object tab. */
const TEXT_COLOUR = '#ffd54f'

/**
 * A caption or a picture laid on a space as an ordinary point node: the model already carries
 * `visual.text` and `visual.image`, so no new figure kind is needed — only a tool that writes them.
 */
export function freeFigureAt(
  kind: FreeFigureKind,
  at: GeoPointDto,
  id: string,
  parentId: string,
  name: string,
  text: string,
  image?: VisualImage,
): NodeDetails {
  const geometry: MapGeometry = { type: 'Point', coord: [at] }
  return {
    node: { id, parentId, name },
    properties: [],
    visual: kind === 'text'
      ? { objectId: id, color: TEXT_COLOUR, text }
      : {
          objectId: id,
          image: image?.data,
          imageWidth: image?.width,
          imageHeight: image?.height,
        },
    figure: { id, parentId, name, geometry },
  }
}
