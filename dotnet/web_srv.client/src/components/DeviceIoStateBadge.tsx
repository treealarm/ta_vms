import { Box, Stack, Tooltip, Typography, alpha, useTheme } from '@mui/material'
import { useTranslation } from 'react-i18next'
import type { DigitalInputDto, RelayOutputDto } from '../dto/dtos'

const WHITE = '#fff'

/**
 * The wall's glance at a camera's devices: one dot per relay (filled while energised) and one digit
 * per digital input, over the picture's bottom-right corner. The names and the exact wording live
 * in the tooltips — a tile has room for the state, not for the tokens.
 */
export default function DeviceIoStateBadge({ relays, inputs }: {
  relays: RelayOutputDto[]
  inputs: DigitalInputDto[]
}) {
  const { t } = useTranslation()
  const theme = useTheme()

  if (relays.length === 0 && inputs.length === 0) return null

  const relayLabel = (relay: RelayOutputDto) =>
    relay.liveActive === null
      ? t('onvifSettings.deviceIoStateUnknown')
      : relay.liveActive
        ? t('onvifSettings.deviceIoActive')
        : t('onvifSettings.deviceIoInactive')

  return (
    <Stack
      direction="row"
      spacing={0.75}
      alignItems="center"
      sx={{
        position: 'absolute',
        bottom: 4,
        right: 4,
        zIndex: 2,
        bgcolor: alpha('#000', 0.45),
        px: 0.75,
        py: 0.35,
        borderRadius: 0.5,
        maxWidth: 'calc(100% - 8px)',
      }}
    >
      {relays.map((relay) => (
        <Tooltip key={relay.token} title={`${relay.token}: ${relayLabel(relay)}`}>
          <Box
            component="span"
            sx={{
              width: 9,
              height: 9,
              flexShrink: 0,
              borderRadius: '50%',
              border: `1px solid ${alpha(WHITE, relay.liveActive === null ? 0.35 : 0.85)}`,
              bgcolor: relay.liveActive === true ? theme.palette.success.main : 'transparent',
            }}
          />
        </Tooltip>
      ))}
      {inputs.map((input) => (
        <Tooltip
          key={input.token}
          title={`${input.token}: ${input.liveActive === null ? t('onvifSettings.deviceIoStateUnknown') : input.liveActive ? '1' : '0'}`}
        >
          <Typography
            component="span"
            sx={{
              fontSize: '0.62rem',
              lineHeight: 1,
              color: alpha(WHITE, input.liveActive === null ? 0.45 : 0.85),
            }}
          >
            {input.liveActive === null ? '—' : input.liveActive ? '1' : '0'}
          </Typography>
        </Tooltip>
      ))}
    </Stack>
  )
}
