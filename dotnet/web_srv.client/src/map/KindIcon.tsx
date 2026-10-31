import CircleOutlinedIcon from '@mui/icons-material/CircleOutlined'
import CategoryIcon from '@mui/icons-material/Category'
import MapIcon from '@mui/icons-material/Map'
import PlaceIcon from '@mui/icons-material/Place'
import SchemaIcon from '@mui/icons-material/Schema'
import VideocamIcon from '@mui/icons-material/Videocam'
import type { MapNodeKind } from '../dto/mapDtos'

/** The icon that says what a node is; the side row behind `nodeKind` is the only type there is. */
export default function KindIcon({ kind, size = 16 }: { kind?: MapNodeKind; size?: number }) {
  const sx = { fontSize: size, color: 'text.secondary' }
  switch (kind) {
    case 'space': return <MapIcon sx={sx} />
    case 'diagram': return <SchemaIcon sx={sx} />
    case 'landmark': return <PlaceIcon sx={sx} />
    case 'camera': return <VideocamIcon sx={sx} />
    case 'figure': return <CategoryIcon sx={sx} />
    default: return <CircleOutlinedIcon sx={sx} />
  }
}
