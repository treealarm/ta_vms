import { Button, IconButton, Stack, Typography } from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteOutlineIcon from '@mui/icons-material/DeleteOutline'
import { useTranslation } from 'react-i18next'
import type { GeoPointDto } from '../../dto/dtos'
import NumberField from './NumberField'
import { withAddedPoint, withPointCoordinate } from './spatialUtils'

/** Two corners and a line are not an outline: the server refuses fewer than three. */
const MIN_POINTS = 3

/**
 * Seven decimals of a degree is about a centimetre — as fine as a corner is worth placing, and
 * fine enough that showing fewer would move a corner the administrator never touched.
 */
const shown = (degrees: number) => Number(degrees.toFixed(7))

type Props = {
  /** The ring as it stands, the vertex drag's draft included. */
  points: GeoPointDto[]
  /** The whole ring, once: a point changed, added or removed. */
  onChange: (points: GeoPointDto[]) => void
}

/**
 * The corners of a nested space's outline as numbers, for the times a mouse cannot say what is
 * meant: a footprint copied off a plan, a corner that must sit on a known coordinate. Every field
 * is committed when it is left, like every other number on a map panel.
 */
export default function FootprintPoints({ points, onChange }: Props) {
  const { t } = useTranslation()

  return (
    <Stack spacing={1}>
      <Typography variant="caption" color="text.secondary">
        {t('maps.footprintPoints', { count: points.length })}
      </Typography>
      {points.length === 0 ? (
        <Typography variant="caption" color="text.secondary">{t('maps.footprintEmpty')}</Typography>
      ) : (
        <Stack spacing={0.75}>
          {points.map((point, index) => (
            <Stack key={index} direction="row" spacing={0.5} alignItems="center">
              <Typography variant="caption" color="text.secondary" sx={{ width: 14, flexShrink: 0 }}>
                {index + 1}
              </Typography>
              <NumberField
                value={shown(point.lat)}
                min={-90}
                max={90}
                step={1e-7}
                placeholder={t('maps.lat')}
                sx={{ flex: 1, minWidth: 0 }}
                onCommit={(lat) => onChange(withPointCoordinate(points, index, 'lat', lat))}
              />
              <NumberField
                value={shown(point.lon)}
                min={-180}
                max={180}
                step={1e-7}
                placeholder={t('maps.lon')}
                sx={{ flex: 1, minWidth: 0 }}
                onCommit={(lon) => onChange(withPointCoordinate(points, index, 'lon', lon))}
              />
              <IconButton
                size="small"
                disabled={points.length <= MIN_POINTS}
                title={points.length <= MIN_POINTS ? t('maps.footprintMinPoints') : t('maps.removePoint')}
                aria-label={t('maps.removePoint')}
                onClick={() => onChange(points.filter((_, i) => i !== index))}
              >
                <DeleteOutlineIcon fontSize="small" />
              </IconButton>
            </Stack>
          ))}
        </Stack>
      )}
      {points.length >= MIN_POINTS ? (
        <Button size="small" startIcon={<AddIcon />} onClick={() => onChange(withAddedPoint(points))}>
          {t('maps.addPoint')}
        </Button>
      ) : null}
      <Typography variant="caption" color="text.secondary">{t('maps.footprintPointsHint')}</Typography>
    </Stack>
  )
}
