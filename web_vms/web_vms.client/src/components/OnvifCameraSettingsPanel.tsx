import { useCallback, useEffect, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Card,
  CardContent,
  Chip,
  CircularProgress,
  Collapse,
  Divider,
  IconButton,
  MenuItem,
  Skeleton,
  Slider,
  Stack,
  Tab,
  Tabs,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import ExpandLessIcon from '@mui/icons-material/ExpandLess'
import ExpandMoreIcon from '@mui/icons-material/ExpandMore'
import DeleteIcon from '@mui/icons-material/Delete'
import { useTranslation } from 'react-i18next'
import { useAppSelector } from '../store'
import type {
  AudioEncoderConfigDto,
  AudioEncoderConfigOptionsDto,
  CameraSetupDto,
  DeviceCapabilitiesDto,
  DeviceInfoDto,
  FloatRangeDto,
  ImagingOptionsDto,
  ImagingSettingsDto,
  PtzPresetDto,
  VideoEncoderConfigDto,
  VideoEncoderConfigOptionsDto,
} from '../dto/dtos'

// ── API helpers ────────────────────────────────────────────────────────────

async function apiFetch<T>(url: string, init?: RequestInit): Promise<T> {
  const r = await fetch(url, init)
  if (!r.ok) throw new Error(await r.text())
  // Several endpoints (set-config, PTZ goto, sync-time, ...) return 200 with no body —
  // r.json() throws "Unexpected end of JSON input" on those, even though the call succeeded.
  const text = await r.text()
  return (text ? JSON.parse(text) : undefined) as T
}

// ── Video encoder section ──────────────────────────────────────────────────

function EncoderCard({ cameraId, config: initial }: { cameraId: string; config: VideoEncoderConfigDto }) {
  const { t } = useTranslation()
  const [cfg, setCfg] = useState<VideoEncoderConfigDto>(initial)
  const [options, setOptions] = useState<VideoEncoderConfigOptionsDto | null>(null)
  const [loadingOpts, setLoadingOpts] = useState(true)
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saveOk, setSaveOk] = useState(false)

  useEffect(() => {
    setLoadingOpts(true)
    apiFetch<VideoEncoderConfigOptionsDto[]>(
      `/api/onvif/cameras/${encodeURIComponent(cameraId)}/video-encoder-config-options?configToken=${encodeURIComponent(initial.token)}`,
    )
      .then((all) => setOptions(all.find((o) => o.encoding === initial.encoding) ?? all[0] ?? null))
      .catch(() => setOptions(null))
      .finally(() => setLoadingOpts(false))
  }, [cameraId, initial.token, initial.encoding])

  const hasGov = cfg.encoding === 'H264' || cfg.encoding === 'H265' || cfg.encoding === 'MPEG4'
  const isH264 = cfg.encoding === 'H264'

  const handleSave = async () => {
    setSaving(true); setSaveError(null); setSaveOk(false)
    try {
      await apiFetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/video-encoder-configs/${encodeURIComponent(cfg.token)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: cfg.name, encoding: cfg.encoding, width: cfg.width, height: cfg.height, frameRateLimit: cfg.frameRateLimit, bitrateLimit: cfg.bitrateLimit, govLength: cfg.govLength, h264Profile: cfg.h264Profile, quality: cfg.quality }),
      })
      setSaveOk(true)
      setTimeout(() => setSaveOk(false), 3000)
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : String(e))
    } finally {
      setSaving(false)
    }
  }

  return (
    <Card variant="outlined">
      <CardContent>
        <Stack spacing={2}>
          <Stack direction="row" alignItems="center" spacing={1}>
            <Typography variant="subtitle1" fontWeight={600}>{cfg.name || cfg.token}</Typography>
            <Chip label={cfg.encoding} size="small" />
          </Stack>
          {loadingOpts ? (
            <Stack spacing={1}><Skeleton variant="rounded" height={40} /><Skeleton variant="rounded" height={40} /></Stack>
          ) : (
            <Stack direction="row" flexWrap="wrap" gap={2}>
              <TextField select size="small" label={t('onvifSettings.resolution')}
                value={`${cfg.width}x${cfg.height}`}
                onChange={(e) => { const [w, h] = e.target.value.split('x').map(Number); setCfg((p) => ({ ...p, width: w, height: h })) }}
                sx={{ minWidth: 150 }}>
                {options && options.resolutions.length > 0
                  ? options.resolutions.map((r) => { const v = `${r.width}x${r.height}`; return <MenuItem key={v} value={v}>{v}</MenuItem> })
                  : <MenuItem value={`${cfg.width}x${cfg.height}`}>{`${cfg.width}x${cfg.height}`}</MenuItem>}
              </TextField>
              <TextField type="number" size="small" label={t('onvifSettings.frameRate')} value={cfg.frameRateLimit}
                onChange={(e) => setCfg((p) => ({ ...p, frameRateLimit: Number(e.target.value) }))}
                slotProps={{ htmlInput: { min: options?.minFrameRate ?? 1, max: options?.maxFrameRate ?? 60 } }} sx={{ width: 130 }} />
              <TextField type="number" size="small" label={t('onvifSettings.bitrate')} value={cfg.bitrateLimit}
                onChange={(e) => setCfg((p) => ({ ...p, bitrateLimit: Number(e.target.value) }))}
                slotProps={{ htmlInput: { min: options?.minBitrate ?? 0, max: options?.maxBitrate ?? 0 } }} sx={{ width: 150 }} />
              {hasGov && (
                <TextField type="number" size="small" label={t('onvifSettings.govLength')} value={cfg.govLength}
                  onChange={(e) => setCfg((p) => ({ ...p, govLength: Number(e.target.value) }))}
                  slotProps={{ htmlInput: { min: options?.minGovLength ?? 1, max: options?.maxGovLength ?? 0 } }} sx={{ width: 130 }} />
              )}
              {isH264 && options && options.h264Profiles.length > 0 && (
                <TextField select size="small" label={t('onvifSettings.h264Profile')} value={cfg.h264Profile}
                  onChange={(e) => setCfg((p) => ({ ...p, h264Profile: e.target.value }))} sx={{ minWidth: 140 }}>
                  {options.h264Profiles.map((p) => <MenuItem key={p} value={p}>{p}</MenuItem>)}
                </TextField>
              )}
            </Stack>
          )}
          <Stack direction="row" alignItems="center" spacing={1}>
            <Button variant="contained" size="small" disabled={saving || loadingOpts} onClick={() => void handleSave()}>
              {saving ? <CircularProgress size={16} /> : t('common.save')}
            </Button>
            {saveOk && <Typography variant="body2" color="success.main">✓</Typography>}
          </Stack>
          {saveError && <Alert severity="error" onClose={() => setSaveError(null)}>{saveError}</Alert>}
        </Stack>
      </CardContent>
    </Card>
  )
}

function VideoSection({ cameraId }: { cameraId: string }) {
  const { t } = useTranslation()
  const [configs, setConfigs] = useState<VideoEncoderConfigDto[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    setLoading(true); setError(null)
    apiFetch<VideoEncoderConfigDto[]>(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/video-encoder-configs`)
      .then(setConfigs)
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setLoading(false))
  }, [cameraId])

  if (loading) return <CircularProgress size={24} />
  if (error) return <Alert severity="error">{error}</Alert>
  if (configs.length === 0) return <Typography variant="body2" color="text.secondary">{t('onvifSettings.noConfigs')}</Typography>
  return <Stack spacing={2}>{configs.map((cfg) => <EncoderCard key={cfg.token} cameraId={cameraId} config={cfg} />)}</Stack>
}

// ── Audio encoder section ─────────────────────────────────────────────────

function AudioCard({ cameraId, config: initial }: { cameraId: string; config: AudioEncoderConfigDto }) {
  const { t } = useTranslation()
  const [cfg, setCfg] = useState<AudioEncoderConfigDto>(initial)
  const [options, setOptions] = useState<AudioEncoderConfigOptionsDto | null>(null)
  const [loadingOpts, setLoadingOpts] = useState(true)
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saveOk, setSaveOk] = useState(false)

  useEffect(() => {
    setLoadingOpts(true)
    apiFetch<AudioEncoderConfigOptionsDto[]>(
      `/api/onvif/cameras/${encodeURIComponent(cameraId)}/audio-encoder-config-options?configToken=${encodeURIComponent(initial.token)}`,
    )
      .then((all) => setOptions(all.find((o) => o.encoding === initial.encoding) ?? all[0] ?? null))
      .catch(() => setOptions(null))
      .finally(() => setLoadingOpts(false))
  }, [cameraId, initial.token, initial.encoding])

  const handleSave = async () => {
    setSaving(true); setSaveError(null); setSaveOk(false)
    try {
      await apiFetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/audio-encoder-configs/${encodeURIComponent(cfg.token)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: cfg.name, encoding: cfg.encoding, bitrate: cfg.bitrate, sampleRate: cfg.sampleRate }),
      })
      setSaveOk(true)
      setTimeout(() => setSaveOk(false), 3000)
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : String(e))
    } finally {
      setSaving(false)
    }
  }

  return (
    <Card variant="outlined">
      <CardContent>
        <Stack spacing={2}>
          <Stack direction="row" alignItems="center" spacing={1}>
            <Typography variant="subtitle1" fontWeight={600}>{cfg.name || cfg.token}</Typography>
            <Chip label={cfg.encoding} size="small" />
          </Stack>
          {loadingOpts ? (
            <Stack spacing={1}><Skeleton variant="rounded" height={40} /><Skeleton variant="rounded" height={40} /></Stack>
          ) : (
            <Stack direction="row" flexWrap="wrap" gap={2}>
              {options && options.bitrates.length > 0 ? (
                <TextField select size="small" label={t('onvifSettings.audioBitrate')} value={cfg.bitrate}
                  onChange={(e) => setCfg((p) => ({ ...p, bitrate: Number(e.target.value) }))} sx={{ minWidth: 160 }}>
                  {options.bitrates.map((b) => <MenuItem key={b} value={b}>{b}</MenuItem>)}
                </TextField>
              ) : (
                <TextField type="number" size="small" label={t('onvifSettings.audioBitrate')} value={cfg.bitrate}
                  onChange={(e) => setCfg((p) => ({ ...p, bitrate: Number(e.target.value) }))} sx={{ width: 160 }} />
              )}
              {options && options.sampleRates.length > 0 ? (
                <TextField select size="small" label={t('onvifSettings.audioSampleRate')} value={cfg.sampleRate}
                  onChange={(e) => setCfg((p) => ({ ...p, sampleRate: Number(e.target.value) }))} sx={{ minWidth: 180 }}>
                  {options.sampleRates.map((s) => <MenuItem key={s} value={s}>{s}</MenuItem>)}
                </TextField>
              ) : (
                <TextField type="number" size="small" label={t('onvifSettings.audioSampleRate')} value={cfg.sampleRate}
                  onChange={(e) => setCfg((p) => ({ ...p, sampleRate: Number(e.target.value) }))} sx={{ width: 180 }} />
              )}
            </Stack>
          )}
          <Stack direction="row" alignItems="center" spacing={1}>
            <Button variant="contained" size="small" disabled={saving || loadingOpts} onClick={() => void handleSave()}>
              {saving ? <CircularProgress size={16} /> : t('common.save')}
            </Button>
            {saveOk && <Typography variant="body2" color="success.main">✓</Typography>}
          </Stack>
          {saveError && <Alert severity="error" onClose={() => setSaveError(null)}>{saveError}</Alert>}
        </Stack>
      </CardContent>
    </Card>
  )
}

function AudioSection({ cameraId }: { cameraId: string }) {
  const { t } = useTranslation()
  const [configs, setConfigs] = useState<AudioEncoderConfigDto[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    setLoading(true); setError(null)
    apiFetch<AudioEncoderConfigDto[]>(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/audio-encoder-configs`)
      .then(setConfigs)
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setLoading(false))
  }, [cameraId])

  if (loading) return <CircularProgress size={24} />
  if (error) return <Alert severity="error">{error}</Alert>
  if (configs.length === 0) return <Typography variant="body2" color="text.secondary">{t('onvifSettings.noAudioConfigs')}</Typography>
  return <Stack spacing={2}>{configs.map((cfg) => <AudioCard key={cfg.token} cameraId={cameraId} config={cfg} />)}</Stack>
}

// ── Imaging section ────────────────────────────────────────────────────────

function ImagingSection({ cameraId }: { cameraId: string }) {
  const { t } = useTranslation()
  const [settings, setSettings] = useState<ImagingSettingsDto | null>(null)
  const [options, setOptions] = useState<ImagingOptionsDto | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [saveOk, setSaveOk] = useState(false)

  useEffect(() => {
    setLoading(true); setError(null)
    Promise.all([
      apiFetch<ImagingSettingsDto>(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/imaging-settings`),
      apiFetch<ImagingOptionsDto>(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/imaging-options`),
    ])
      .then(([s, o]) => { setSettings(s); setOptions(o) })
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setLoading(false))
  }, [cameraId])

  const handleSave = async () => {
    if (!settings) return
    setSaving(true); setSaveError(null); setSaveOk(false)
    try {
      await apiFetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/imaging-settings`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          brightness: settings.brightness ?? null,
          contrast: settings.contrast ?? null,
          colorSaturation: settings.colorSaturation ?? null,
          sharpness: settings.sharpness ?? null,
        }),
      })
      setSaveOk(true)
      setTimeout(() => setSaveOk(false), 3000)
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : String(e))
    } finally {
      setSaving(false)
    }
  }

  if (loading) return <Stack spacing={1}><Skeleton variant="rounded" height={40} /><Skeleton variant="rounded" height={40} /></Stack>
  if (error) return <Alert severity="error">{error}</Alert>
  if (!settings) return <Typography variant="body2" color="text.secondary">{t('onvifSettings.noImagingSupport')}</Typography>

  const allSliders: { key: keyof ImagingSettingsDto; label: string; range?: FloatRangeDto | null }[] = [
    { key: 'brightness', label: t('onvifSettings.brightness'), range: options?.brightness },
    { key: 'contrast', label: t('onvifSettings.contrast'), range: options?.contrast },
    { key: 'colorSaturation', label: t('onvifSettings.colorSaturation'), range: options?.colorSaturation },
    { key: 'sharpness', label: t('onvifSettings.sharpness'), range: options?.sharpness },
  ]
  const sliders = allSliders.filter((s) => s.range != null || settings[s.key] != null)

  if (sliders.length === 0)
    return <Typography variant="body2" color="text.secondary">{t('onvifSettings.noImagingSupport')}</Typography>

  return (
    <Card variant="outlined">
      <CardContent>
        <Stack spacing={3}>
          {sliders.map(({ key, label, range }) => {
            const value = settings[key] ?? null
            const min = range?.min ?? 0
            const max = range?.max ?? 100
            return (
              <Box key={key}>
                <Typography variant="body2" gutterBottom>
                  {label}{value != null ? `: ${value.toFixed(1)}` : ''}
                </Typography>
                <Slider
                  size="small"
                  min={min}
                  max={max}
                  step={(max - min) / 100 || 1}
                  value={value ?? min}
                  onChange={(_, v) => setSettings((p) => p ? { ...p, [key]: v as number } : p)}
                />
              </Box>
            )
          })}
          <Stack direction="row" alignItems="center" spacing={1}>
            <Button variant="contained" size="small" disabled={saving} onClick={() => void handleSave()}>
              {saving ? <CircularProgress size={16} /> : t('common.save')}
            </Button>
            {saveOk && <Typography variant="body2" color="success.main">✓</Typography>}
          </Stack>
          {saveError && <Alert severity="error" onClose={() => setSaveError(null)}>{saveError}</Alert>}
        </Stack>
      </CardContent>
    </Card>
  )
}

// ── PTZ presets section ───────────────────────────────────────────────────

type OnvifProfileInfo = { token: string; name: string }

function PtzPresetsSection({ cameraId }: { cameraId: string }) {
  const { t } = useTranslation()
  const [profiles, setProfiles] = useState<OnvifProfileInfo[]>([])
  const [profileToken, setProfileToken] = useState('')
  const [presets, setPresets] = useState<PtzPresetDto[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [newName, setNewName] = useState('')
  const [saving, setSaving] = useState(false)
  const [actionError, setActionError] = useState<string | null>(null)

  useEffect(() => {
    apiFetch<OnvifProfileInfo[]>(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/profiles`)
      .then((p) => { setProfiles(p); if (p.length > 0) setProfileToken(p[0].token) })
      .catch(() => setProfiles([]))
  }, [cameraId])

  const loadPresets = useCallback(() => {
    if (!profileToken) { setLoading(false); return }
    setLoading(true); setError(null)
    apiFetch<PtzPresetDto[]>(
      `/api/ptz/cameras/${encodeURIComponent(cameraId)}/presets?profileToken=${encodeURIComponent(profileToken)}`,
    )
      .then(setPresets)
      .catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setLoading(false))
  }, [cameraId, profileToken])

  useEffect(() => { loadPresets() }, [loadPresets])

  const handleGoto = async (presetToken: string) => {
    setActionError(null)
    try {
      await apiFetch(`/api/ptz/cameras/${encodeURIComponent(cameraId)}/presets/${encodeURIComponent(presetToken)}/goto`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ profileToken }),
      })
    } catch (e) { setActionError(e instanceof Error ? e.message : String(e)) }
  }

  const handleDelete = async (presetToken: string) => {
    setActionError(null)
    try {
      await apiFetch(`/api/ptz/cameras/${encodeURIComponent(cameraId)}/presets/${encodeURIComponent(presetToken)}?profileToken=${encodeURIComponent(profileToken)}`, { method: 'DELETE' })
      loadPresets()
    } catch (e) { setActionError(e instanceof Error ? e.message : String(e)) }
  }

  const handleSavePreset = async () => {
    if (!newName.trim()) return
    setSaving(true); setActionError(null)
    try {
      await apiFetch(`/api/ptz/cameras/${encodeURIComponent(cameraId)}/presets`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ profileToken, presetName: newName.trim(), presetToken: '' }),
      })
      setNewName('')
      loadPresets()
    } catch (e) { setActionError(e instanceof Error ? e.message : String(e)) }
    finally { setSaving(false) }
  }

  if (profiles.length === 0 && !loading)
    return <Typography variant="body2" color="text.secondary">{t('onvifSettings.noProfiles')}</Typography>

  return (
    <Stack spacing={2}>
      {profiles.length > 1 && (
        <TextField select size="small" label={t('onvifSettings.selectProfile')} value={profileToken}
          onChange={(e) => setProfileToken(e.target.value)} sx={{ maxWidth: 300 }}>
          {profiles.map((p) => <MenuItem key={p.token} value={p.token}>{p.name || p.token}</MenuItem>)}
        </TextField>
      )}

      {loading ? <CircularProgress size={24} /> : error ? (
        <Alert severity="error">{error}</Alert>
      ) : presets.length === 0 ? (
        <Typography variant="body2" color="text.secondary">{t('onvifSettings.noPresets')}</Typography>
      ) : (
        <Stack spacing={0.5}>
          {presets.map((p) => (
            <Box key={p.token} sx={{ display: 'flex', alignItems: 'center', gap: 1, py: 0.5 }}>
              <Typography variant="body2" sx={{ flex: 1 }}>{p.name || p.token}</Typography>
              <Button size="small" variant="outlined" onClick={() => void handleGoto(p.token)}>
                {t('onvifSettings.gotoPreset')}
              </Button>
              <Tooltip title={t('onvifSettings.deletePreset')}>
                <IconButton size="small" onClick={() => void handleDelete(p.token)}>
                  <DeleteIcon fontSize="small" />
                </IconButton>
              </Tooltip>
            </Box>
          ))}
        </Stack>
      )}

      <Divider />
      <Stack direction="row" spacing={1} alignItems="center">
        <TextField size="small" label={t('onvifSettings.presetName')} value={newName}
          onChange={(e) => setNewName(e.target.value)} sx={{ flex: 1, maxWidth: 240 }} />
        <Button variant="contained" size="small" disabled={saving || !newName.trim()} onClick={() => void handleSavePreset()}>
          {saving ? <CircularProgress size={16} /> : t('onvifSettings.addPreset')}
        </Button>
      </Stack>

      {actionError && <Alert severity="error" onClose={() => setActionError(null)}>{actionError}</Alert>}
    </Stack>
  )
}

// ── System section ────────────────────────────────────────────────────────

function DeviceInfoBlock({ cameraId }: { cameraId: string }) {
  const { t } = useTranslation()
  const [info, setInfo] = useState<DeviceInfoDto | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    setLoading(true)
    apiFetch<DeviceInfoDto>(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/device-info`)
      .then(setInfo)
      .catch(() => setInfo(null))
      .finally(() => setLoading(false))
  }, [cameraId])

  if (loading) return <Skeleton width={280} height={90} />
  if (!info) return null

  const rows: [string, string][] = [
    [t('onvifSettings.manufacturer'), info.manufacturer],
    [t('onvifSettings.model'), info.model],
    [t('onvifSettings.firmwareVersion'), info.firmwareVersion],
    [t('onvifSettings.serialNumber'), info.serialNumber],
    [t('onvifSettings.hardwareId'), info.hardwareId],
  ]

  return (
    <Box>
      <Typography variant="caption" color="text.secondary">{t('onvifSettings.deviceInfo')}</Typography>
      <Stack spacing={0.25}>
        {rows.map(([label, value]) => value && (
          <Typography key={label} variant="body2">
            <Typography component="span" color="text.secondary">{label}: </Typography>
            {value}
          </Typography>
        ))}
      </Stack>
    </Box>
  )
}

function toDatetimeLocalValue(utcIso: string): string {
  const d = new Date(utcIso)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}T${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}`
}

function SystemSection({ cameraId }: { cameraId: string }) {
  const { t } = useTranslation()
  const [deviceTime, setDeviceTime] = useState<string | null>(null)
  const [timeLoading, setTimeLoading] = useState(true)
  const [syncing, setSyncing] = useState(false)
  const [settingTime, setSettingTime] = useState(false)
  const [rebooting, setRebooting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [syncOk, setSyncOk] = useState(false)
  const [setTimeOk, setSetTimeOk] = useState(false)
  const [rebootMsg, setRebootMsg] = useState<string | null>(null)
  const [manualTime, setManualTime] = useState('')

  useEffect(() => {
    setTimeLoading(true)
    apiFetch<{ utcTime: string }>(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/device-time`)
      .then((r) => {
        setDeviceTime(r.utcTime ? new Date(r.utcTime).toUTCString() : '—')
        if (r.utcTime) setManualTime(toDatetimeLocalValue(r.utcTime))
      })
      .catch(() => setDeviceTime('—'))
      .finally(() => setTimeLoading(false))
  }, [cameraId])

  const handleSync = async () => {
    setSyncing(true); setError(null); setSyncOk(false)
    try {
      await apiFetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/sync-time`, { method: 'POST' })
      setSyncOk(true)
      setTimeout(() => setSyncOk(false), 3000)
    } catch (e) { setError(e instanceof Error ? e.message : String(e)) }
    finally { setSyncing(false) }
  }

  const handleSetTime = async () => {
    if (!manualTime) return
    setSettingTime(true); setError(null); setSetTimeOk(false)
    try {
      await apiFetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/set-time`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ utcTime: new Date(manualTime + ':00Z').toISOString() }),
      })
      setSetTimeOk(true)
      setTimeout(() => setSetTimeOk(false), 3000)
    } catch (e) { setError(e instanceof Error ? e.message : String(e)) }
    finally { setSettingTime(false) }
  }

  const handleReboot = async () => {
    if (!window.confirm(t('onvifSettings.rebootConfirm'))) return
    setRebooting(true); setError(null); setRebootMsg(null)
    try {
      const r = await apiFetch<{ message?: string }>(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/reboot`, { method: 'POST' })
      setRebootMsg(r.message ?? 'Rebooting…')
    } catch (e) { setError(e instanceof Error ? e.message : String(e)) }
    finally { setRebooting(false) }
  }

  return (
    <Stack spacing={3}>
      <DeviceInfoBlock cameraId={cameraId} />

      <Box>
        <Typography variant="caption" color="text.secondary">{t('onvifSettings.deviceTime')}</Typography>
        <Typography variant="body1">
          {timeLoading ? <Skeleton width={220} /> : deviceTime}
        </Typography>
      </Box>

      <Stack direction="row" spacing={1} alignItems="center">
        <Button variant="outlined" size="small" disabled={syncing} onClick={() => void handleSync()}>
          {syncing ? <CircularProgress size={16} /> : t('onvifSettings.syncTime')}
        </Button>
        {syncOk && <Typography variant="body2" color="success.main">✓</Typography>}
      </Stack>

      <Stack direction="row" spacing={1} alignItems="flex-end">
        <TextField
          label={t('onvifSettings.setTime')}
          type="datetime-local"
          size="small"
          value={manualTime}
          onChange={(e) => setManualTime(e.target.value)}
          InputLabelProps={{ shrink: true }}
          sx={{ minWidth: 220 }}
        />
        <Button variant="outlined" size="small" disabled={settingTime || !manualTime} onClick={() => void handleSetTime()}>
          {settingTime ? <CircularProgress size={16} /> : t('onvifSettings.setTime')}
        </Button>
        {setTimeOk && <Typography variant="body2" color="success.main">✓</Typography>}
      </Stack>

      <Box>
        <Button variant="outlined" color="warning" size="small" disabled={rebooting} onClick={() => void handleReboot()}>
          {rebooting ? <CircularProgress size={16} /> : t('onvifSettings.reboot')}
        </Button>
        {rebootMsg && (
          <Typography variant="body2" color="text.secondary" sx={{ mt: 1 }}>{rebootMsg}</Typography>
        )}
      </Box>

      {error && <Alert severity="error" onClose={() => setError(null)}>{error}</Alert>}
    </Stack>
  )
}

// ── Camera detail pane ────────────────────────────────────────────────────

function CameraDetail({ camera }: { camera: CameraSetupDto }) {
  const { t } = useTranslation()
  const [tab, setTab] = useState(0)
  const [caps, setCaps] = useState<DeviceCapabilitiesDto | null>(null)

  useEffect(() => {
    apiFetch<DeviceCapabilitiesDto>(`/api/onvif/cameras/${encodeURIComponent(camera.cameraId)}/capabilities`)
      .then(setCaps)
      .catch(() => setCaps(null)) // fail open: unknown capabilities -> show all tabs rather than hide everything
  }, [camera.cameraId])

  const tabs = [
    { key: 'video', label: t('onvifSettings.videoTab'), show: true },
    { key: 'audio', label: t('onvifSettings.audioTab'), show: caps?.hasAudio ?? true },
    { key: 'imaging', label: t('onvifSettings.imagingTab'), show: caps?.hasImaging ?? true },
    { key: 'ptz', label: t('onvifSettings.ptzTab'), show: caps?.hasPtz ?? true },
    { key: 'system', label: t('onvifSettings.systemTab'), show: true },
  ].filter((tb) => tb.show)

  useEffect(() => { if (tab >= tabs.length) setTab(0) }, [tabs.length, tab])
  const activeKey = tabs[tab]?.key

  return (
    <Stack spacing={0} sx={{ height: '100%' }}>
      <Tabs value={tab} onChange={(_, v: number) => setTab(v)} sx={{ borderBottom: 1, borderColor: 'divider', mb: 2 }}>
        {tabs.map((tb) => <Tab key={tb.key} label={tb.label} />)}
      </Tabs>
      <Box sx={{ flex: 1, overflow: 'auto', pt: 1 }}>
        {activeKey === 'video' && <VideoSection cameraId={camera.cameraId} />}
        {activeKey === 'audio' && <AudioSection cameraId={camera.cameraId} />}
        {activeKey === 'imaging' && <ImagingSection cameraId={camera.cameraId} />}
        {activeKey === 'ptz' && <PtzPresetsSection cameraId={camera.cameraId} />}
        {activeKey === 'system' && <SystemSection cameraId={camera.cameraId} />}
      </Box>
    </Stack>
  )
}

// ── Server group ──────────────────────────────────────────────────────────

interface ServerGroupProps {
  serverId: string
  cameras: CameraSetupDto[]
  selectedId: string | null
  isOpen: boolean
  onOpen: () => void
  onSelect: (id: string) => void
}

function ServerGroup({ serverId, cameras, selectedId, isOpen, onOpen, onSelect }: ServerGroupProps) {
  return (
    <Box>
      <Box
        onClick={() => { if (!isOpen) onOpen() }}
        sx={{ display: 'flex', alignItems: 'center', gap: 0.5, px: 1.5, py: 0.75,
          cursor: isOpen ? 'default' : 'pointer',
          bgcolor: 'action.hover', borderBottom: 1, borderColor: 'divider', userSelect: 'none' }}
      >
        {isOpen ? <ExpandLessIcon fontSize="small" /> : <ExpandMoreIcon fontSize="small" />}
        <Typography variant="caption" color="text.secondary" noWrap sx={{ flex: 1 }}>
          {serverId || '—'}
        </Typography>
      </Box>
      <Collapse in={isOpen}>
        {cameras.map((cam) => (
          <Box key={cam.cameraId} onClick={() => onSelect(cam.cameraId)}
            sx={{ px: 2, py: 0.75, cursor: 'pointer', borderBottom: 1, borderColor: 'divider',
              bgcolor: selectedId === cam.cameraId ? 'action.selected' : 'transparent',
              '&:hover': { bgcolor: selectedId === cam.cameraId ? 'action.selected' : 'action.hover' } }}>
            <Typography variant="body2" noWrap fontWeight={selectedId === cam.cameraId ? 600 : 400}>
              {cam.name || cam.cameraId}
            </Typography>
            <Typography variant="caption" color="text.secondary" noWrap>{cam.onvifConn?.ip}</Typography>
          </Box>
        ))}
      </Collapse>
    </Box>
  )
}

// ── Main panel ────────────────────────────────────────────────────────────

export default function OnvifCameraSettingsPanel() {
  const { t } = useTranslation()
  const allCameras = useAppSelector((s) => Object.values(s.live.cameraStatesById))
  const onvifCameras = allCameras.filter((c) => c.onvifConn?.ip)
  const [selectedId, setSelectedId] = useState<string | null>(null)

  const groups = onvifCameras.reduce<Record<string, CameraSetupDto[]>>((acc, cam) => {
    const key = cam.serverId ?? ''
    ;(acc[key] ??= []).push(cam)
    return acc
  }, {})

  const serverIds = Object.keys(groups)

  // Accordion: one group always open. Default = first server.
  const [openServerId, setOpenServerId] = useState<string>(() => serverIds[0] ?? '')

  // If selected camera moves to a different server, open that server.
  const selectedCamera = selectedId ? onvifCameras.find((c) => c.cameraId === selectedId) ?? null : null
  useEffect(() => {
    if (selectedCamera) {
      const key = selectedCamera.serverId ?? ''
      setOpenServerId(key)
    }
  }, [selectedCamera])

  // If the previously open server disappears (camera removed), fall back to first.
  useEffect(() => {
    if (serverIds.length > 0 && !serverIds.includes(openServerId)) {
      setOpenServerId(serverIds[0])
    }
  }, [serverIds, openServerId])

  if (onvifCameras.length === 0) {
    return (
      <Typography variant="body2" color="text.secondary" sx={{ p: 2 }}>
        {t('onvifSettings.noCamerasWithOnvif')}
      </Typography>
    )
  }

  return (
    <Box sx={{ display: 'flex', height: '100%', overflow: 'hidden', border: 1, borderColor: 'divider', borderRadius: 1 }}>
      <Box sx={{ width: 220, flexShrink: 0, borderRight: 1, borderColor: 'divider', overflowY: 'auto' }}>
        {serverIds.map((serverId) => (
          <ServerGroup
            key={serverId}
            serverId={serverId}
            cameras={groups[serverId]}
            selectedId={selectedId}
            isOpen={openServerId === serverId}
            onOpen={() => setOpenServerId(serverId)}
            onSelect={setSelectedId}
          />
        ))}
      </Box>

      <Box sx={{ flex: 1, overflowY: 'auto', p: 2 }}>
        {!selectedCamera ? (
          <Typography variant="body2" color="text.secondary">{t('onvifSettings.selectCamera')}</Typography>
        ) : (
          <Stack spacing={1} sx={{ height: '100%' }}>
            <Typography variant="h6">{selectedCamera.name || selectedCamera.cameraId}</Typography>
            <Typography variant="body2" color="text.secondary">
              {selectedCamera.onvifConn?.ip}:{selectedCamera.onvifConn?.port}
            </Typography>
            <CameraDetail camera={selectedCamera} />
          </Stack>
        )}
      </Box>
    </Box>
  )
}
