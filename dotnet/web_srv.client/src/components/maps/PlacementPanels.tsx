import { useState } from 'react'
import { Box, Button, Collapse, Divider, FormControlLabel, Stack, Switch, TextField, Typography } from '@mui/material'
import ExpandLessIcon from '@mui/icons-material/ExpandLess'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import { useTranslation } from 'react-i18next'
import type { CameraPlacementDto, CameraSetupDto, LandmarkDto, SpaceDto } from '../../dto/dtos'
import NumberField from './NumberField'
import { formatLatLon } from './spatialUtils'

type CameraProps = {
  camera: CameraSetupDto | undefined
  placement: CameraPlacementDto
  /** Where the camera really stands, when that is another space; null when it stands nowhere. */
  referenceSpace: SpaceDto | null
  onChange: (placement: CameraPlacementDto) => void
  onRemove: () => void
  onCalibrate: () => void
  onGoToReference: () => void
  onClose: () => void
}

/** The selected camera's placement in this space. Each field saves when left; drags on the map save on release. */
export function CameraPlacementPanel({
  camera, placement, referenceSpace, onChange, onRemove, onCalibrate, onGoToReference, onClose,
}: CameraProps) {
  const { t } = useTranslation()
  // The sector is drawing only -- aiming never reads it -- so it stays folded until asked for.
  const [sectorOpen, setSectorOpen] = useState(false)
  const set = <K extends keyof CameraPlacementDto>(key: K) => (value: CameraPlacementDto[K]) =>
    onChange({ ...placement, [key]: value })

  return (
    <Stack spacing={1.5} sx={{ p: 1.5 }}>
      <Box>
        <Typography variant="subtitle2" noWrap>{camera?.name || placement.cameraId}</Typography>
        <Typography variant="caption" color="text.secondary">{formatLatLon(placement.lat, placement.lon)}</Typography>
      </Box>
      <Box>
        <FormControlLabel
          control={<Switch size="small" checked={placement.isReference} onChange={(e) => set('isReference')(e.target.checked)} />}
          label={t('maps.reference')}
        />
        <Typography variant="caption" color="text.secondary" display="block">
          {placement.isReference
            ? t('maps.referenceHint')
            : referenceSpace ? t('maps.pictureOf', { name: referenceSpace.name }) : t('maps.noReference')}
        </Typography>
        {!placement.isReference && referenceSpace && (
          <Button size="small" onClick={onGoToReference} sx={{ px: 0 }}>{t('maps.goToReference')}</Button>
        )}
      </Box>
      <NumberField label={t('maps.cameraHeight')} value={placement.heightM} onCommit={set('heightM')} helperText={t('maps.cameraHeightHint')} />
      <NumberField label={t('maps.heading')} value={placement.headingDeg} onCommit={set('headingDeg')} helperText={t('maps.headingHint')} />
      <Box>
        <Button
          size="small"
          color="inherit"
          onClick={() => setSectorOpen((v) => !v)}
          startIcon={sectorOpen ? <ExpandLessIcon /> : <ExpandMoreIcon />}
          sx={{ px: 0, color: 'text.secondary', textTransform: 'none' }}
        >
          {t('maps.sector')}
        </Button>
        <Collapse in={sectorOpen}>
          <Typography variant="caption" color="text.secondary" display="block" sx={{ mb: 1 }}>
            {t('maps.sectorHint')}
          </Typography>
          <Stack direction="row" spacing={1}>
            <NumberField label={t('maps.hfov')} value={placement.hfovDeg} min={1} max={360} onCommit={set('hfovDeg')} />
            <NumberField label={t('maps.range')} value={placement.rangeM} min={1} onCommit={set('rangeM')} />
          </Stack>
        </Collapse>
      </Box>
      {camera && placement.isReference && (
        <>
          <Divider />
          <Typography variant="caption" color="text.secondary">
            {camera.hasTelemetry ? t('ptzGeo.panelHint') : t('ground.panelHint')}
          </Typography>
          <Button variant="outlined" size="small" onClick={onCalibrate}>{t('ptzGeo.open')}</Button>
        </>
      )}
      <Divider />
      <Stack direction="row" spacing={1}>
        <Button color="error" size="small" onClick={onRemove}>{t('maps.removeFromSpace')}</Button>
        <Button size="small" onClick={onClose} sx={{ ml: 'auto' }}>{t('common.close')}</Button>
      </Stack>
    </Stack>
  )
}

type LandmarkProps = {
  landmark: LandmarkDto
  onChange: (landmark: LandmarkDto) => void
  onRemove: () => void
  onClose: () => void
}

/** A landmark's name and height. Its position is changed by dragging it. */
export function LandmarkPanel({ landmark, onChange, onRemove, onClose }: LandmarkProps) {
  const { t } = useTranslation()
  const [name, setName] = useState(landmark.name)

  return (
    <Stack spacing={1.5} sx={{ p: 1.5 }}>
      <Box>
        <Typography variant="subtitle2">{t('maps.landmark')}</Typography>
        <Typography variant="caption" color="text.secondary">{formatLatLon(landmark.lat, landmark.lon)}</Typography>
      </Box>
      <TextField
        size="small"
        label={t('common.name')}
        value={name}
        onChange={(e) => setName(e.target.value)}
        onBlur={() => { if (name.trim() && name !== landmark.name) onChange({ ...landmark, name: name.trim() }) }}
        onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur() }}
      />
      <NumberField
        label={t('maps.landmarkHeight')}
        value={landmark.heightM}
        onCommit={(heightM) => onChange({ ...landmark, heightM })}
        helperText={t('maps.landmarkHeightHint')}
      />
      <Typography variant="caption" color="text.secondary">{t('maps.landmarkHint')}</Typography>
      <Divider />
      <Stack direction="row" spacing={1}>
        <Button color="error" size="small" onClick={onRemove}>{t('common.delete')}</Button>
        <Button size="small" onClick={onClose} sx={{ ml: 'auto' }}>{t('common.close')}</Button>
      </Stack>
    </Stack>
  )
}

type FootprintProps = {
  space: SpaceDto
  onOpen: () => void
  onRemoveFootprint: () => void
  onClose: () => void
}

/** A nested space selected by its footprint on this one: open it, or reshape the outline on the map. */
export function FootprintPanel({ space, onOpen, onRemoveFootprint, onClose }: FootprintProps) {
  const { t } = useTranslation()
  return (
    <Stack spacing={1.5} sx={{ p: 1.5 }}>
      <Box>
        <Typography variant="subtitle2" noWrap>{space.name}</Typography>
        <Typography variant="caption" color="text.secondary">{t('maps.footprintOf')}</Typography>
      </Box>
      <Button variant="contained" size="small" onClick={onOpen}>{t('maps.openSpace')}</Button>
      <Typography variant="caption" color="text.secondary">{t('maps.footprintHint')}</Typography>
      <Divider />
      <Stack direction="row" spacing={1}>
        <Button color="error" size="small" onClick={onRemoveFootprint}>{t('maps.removeFootprint')}</Button>
        <Button size="small" onClick={onClose} sx={{ ml: 'auto' }}>{t('common.close')}</Button>
      </Stack>
    </Stack>
  )
}
