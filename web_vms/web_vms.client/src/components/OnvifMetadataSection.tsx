import { useCallback, useEffect, useState } from 'react'
import {
  Alert,
  Box,
  Button,
  Checkbox,
  Chip,
  CircularProgress,
  Divider,
  FormControlLabel,
  IconButton,
  MenuItem,
  Skeleton,
  Stack,
  TextField,
  Typography,
} from '@mui/material'
import AddIcon from '@mui/icons-material/Add'
import DeleteIcon from '@mui/icons-material/Delete'
import { useTranslation } from 'react-i18next'
import { apiFetch } from '../api/apiFetch'
import type {
  MetadataConfigDto,
  MetadataConfigsResponseDto,
  OnvifAnalyticsConfigDto,
  OnvifAnalyticsConfigsResponseDto,
  OnvifAnalyticsModuleDto,
  OnvifAnalyticsModuleDescriptionsResponseDto,
  OnvifAnalyticsModuleDescriptionDto,
  OnvifAnalyticsModulesResponseDto,
} from '../dto/dtos'

// Same shape the other ONVIF sections read from /profiles — only token and name are used here.
type OnvifProfileInfo = { token: string; name: string }

const base = (cameraId: string) => `/api/onvif/cameras/${encodeURIComponent(cameraId)}`

const errText = (e: unknown) => (e instanceof Error ? e.message : String(e))

// ── Metadata configurations ─────────────────────────────────────────────────

function MetadataConfigRow({ cameraId, config, profileToken, onChanged }: {
  cameraId: string
  config: MetadataConfigDto
  profileToken: string
  onChanged: () => void
}) {
  const { t } = useTranslation()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const attached = config.attachedProfileTokens.includes(profileToken)

  const run = async (fn: () => Promise<unknown>) => {
    setBusy(true); setError(null)
    try {
      await fn()
      onChanged()
    } catch (e) {
      setError(errText(e))
    } finally {
      setBusy(false)
    }
  }

  const setAnalytics = (analytics: boolean) => void run(() =>
    apiFetch(`${base(cameraId)}/metadata-configs/${encodeURIComponent(config.token)}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ analytics }),
    }))

  const toggleAttach = () => void run(() => {
    const url = `${base(cameraId)}/profiles/${encodeURIComponent(profileToken)}/metadata-config`
      + `?configToken=${encodeURIComponent(config.token)}`
    return apiFetch(url, { method: attached ? 'DELETE' : 'POST' })
  })

  return (
    <Stack spacing={0.5} sx={{ py: 1 }}>
      <Stack direction="row" alignItems="center" spacing={2} flexWrap="wrap" useFlexGap>
        <Typography variant="body2" sx={{ minWidth: 160, fontWeight: 500 }}>
          {config.name || config.token}
        </Typography>
        <FormControlLabel
          control={
            <Checkbox
              size="small"
              checked={config.analytics}
              disabled={busy}
              onChange={(e) => setAnalytics(e.target.checked)}
            />
          }
          label={t('onvifSettings.metadataAnalytics')}
        />
        {config.ptzStatus && <Chip size="small" label={t('onvifSettings.metadataPtzStatus')} />}
        {config.events && <Chip size="small" label={t('onvifSettings.metadataEvents')} />}
        {config.shapePolygon && <Chip size="small" label={t('onvifSettings.metadataShapePolygon')} />}
        <Box sx={{ flex: 1 }} />
        <Button
          size="small"
          variant={attached ? 'outlined' : 'contained'}
          disabled={busy || !profileToken}
          onClick={toggleAttach}
        >
          {busy ? <CircularProgress size={16} />
            : attached ? t('onvifSettings.metadataDetach') : t('onvifSettings.metadataAttach')}
        </Button>
      </Stack>
      {error && <Alert severity="error" onClose={() => setError(null)}>{error}</Alert>}
    </Stack>
  )
}

// ── Analytics modules / rules ───────────────────────────────────────────────

type ModuleKind = 'analytics-modules' | 'rules'

function ModuleEditor({ cameraId, kind, configToken, module, descriptions, onChanged }: {
  cameraId: string
  kind: ModuleKind
  configToken: string
  module: OnvifAnalyticsModuleDto
  descriptions: OnvifAnalyticsModuleDescriptionDto[]
  onChanged: () => void
}) {
  const { t } = useTranslation()
  const [items, setItems] = useState(module.simpleItems)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Reset when the camera or configuration switches under us — this component is keyed by module
  // name, which repeats across configurations.
  useEffect(() => { setItems(module.simpleItems) }, [module])

  const description = descriptions.find((d) => d.type === module.type)

  const save = async () => {
    setBusy(true); setError(null)
    try {
      await apiFetch(`${base(cameraId)}/${kind}?configToken=${encodeURIComponent(configToken)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        // elementItemsXml is echoed back untouched: the camera's structured parameters are not
        // editable here, and dropping them would silently reconfigure the module.
        body: JSON.stringify({
          name: module.name,
          type: module.type,
          simpleItems: items,
          elementItemsXml: module.elementItemsXml,
        }),
      })
      onChanged()
    } catch (e) {
      setError(errText(e))
    } finally {
      setBusy(false)
    }
  }

  const remove = async () => {
    setBusy(true); setError(null)
    try {
      await apiFetch(
        `${base(cameraId)}/${kind}/${encodeURIComponent(module.name)}`
        + `?configToken=${encodeURIComponent(configToken)}`,
        { method: 'DELETE' })
      onChanged()
    } catch (e) {
      setError(errText(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Stack spacing={1} sx={{ py: 1 }}>
      <Stack direction="row" alignItems="center" spacing={1}>
        <Typography variant="body2" sx={{ fontWeight: 500 }}>{module.name}</Typography>
        <Chip size="small" label={module.type} />
        <Box sx={{ flex: 1 }} />
        <Button size="small" variant="contained" disabled={busy} onClick={() => void save()}>
          {busy ? <CircularProgress size={16} /> : t('onvifSettings.metadataSave')}
        </Button>
        <IconButton size="small" disabled={busy} onClick={() => void remove()}>
          <DeleteIcon fontSize="small" />
        </IconButton>
      </Stack>
      <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap>
        {items.map((item, idx) => (
          <TextField
            key={item.name}
            size="small"
            label={item.name}
            helperText={description?.parameters.find((p) => p.name === item.name)?.type}
            value={item.value}
            onChange={(e) => setItems(items.map((it, i) =>
              i === idx ? { ...it, value: e.target.value } : it))}
            sx={{ minWidth: 160 }}
          />
        ))}
        {items.length === 0 && (
          <Typography variant="body2" color="text.secondary">
            {t('onvifSettings.metadataNoParameters')}
          </Typography>
        )}
      </Stack>
      {module.elementItemsXml.length > 0 && (
        <Typography variant="caption" color="text.secondary">
          {t('onvifSettings.metadataStructuredParams', { count: module.elementItemsXml.length })}
        </Typography>
      )}
      {error && <Alert severity="error" onClose={() => setError(null)}>{error}</Alert>}
    </Stack>
  )
}

function ModuleGroup({ cameraId, kind, configToken, title }: {
  cameraId: string
  kind: ModuleKind
  configToken: string
  title: string
}) {
  const { t } = useTranslation()
  const [modules, setModules] = useState<OnvifAnalyticsModuleDto[] | null>(null)
  const [descriptions, setDescriptions] = useState<OnvifAnalyticsModuleDescriptionDto[]>([])
  const [error, setError] = useState<string | null>(null)
  const [newType, setNewType] = useState('')
  const [newName, setNewName] = useState('')
  const [adding, setAdding] = useState(false)
  const [reload, setReload] = useState(0)

  useEffect(() => {
    if (!configToken) { setModules([]); return }
    let cancelled = false
    const q = `?configToken=${encodeURIComponent(configToken)}`

    apiFetch<OnvifAnalyticsModulesResponseDto>(`${base(cameraId)}/${kind}${q}`)
      .then((r) => { if (!cancelled) { setModules(r.modules); setError(r.error || null) } })
      .catch((e: unknown) => { if (!cancelled) { setModules([]); setError(errText(e)) } })

    // Advisory only — they seed the "add" dropdown and label parameter types. A failure here must
    // not hide the modules the camera already runs.
    apiFetch<OnvifAnalyticsModuleDescriptionsResponseDto>(`${base(cameraId)}/${kind}/supported${q}`)
      .then((r) => { if (!cancelled) setDescriptions(r.descriptions) })
      .catch(() => { if (!cancelled) setDescriptions([]) })

    return () => { cancelled = true }
  }, [cameraId, kind, configToken, reload])

  const add = async () => {
    setAdding(true); setError(null)
    try {
      const description = descriptions.find((d) => d.type === newType)
      await apiFetch(`${base(cameraId)}/${kind}?configToken=${encodeURIComponent(configToken)}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        // Parameters start empty: the camera fills in its own defaults, and guessing values for
        // a vendor module we only know the parameter names of would be worse than asking it.
        body: JSON.stringify({
          name: newName,
          type: newType,
          simpleItems: (description?.parameters ?? []).map((p) => ({ name: p.name, value: '' })),
          elementItemsXml: [],
        }),
      })
      setNewName('')
      setNewType('')
      setReload((n) => n + 1)
    } catch (e) {
      setError(errText(e))
    } finally {
      setAdding(false)
    }
  }

  return (
    <Stack spacing={1}>
      <Typography variant="subtitle2">{title}</Typography>
      {error && <Alert severity="error" onClose={() => setError(null)}>{error}</Alert>}
      {modules === null ? (
        <Skeleton variant="rounded" height={40} />
      ) : modules.length === 0 ? (
        <Typography variant="body2" color="text.secondary">{t('onvifSettings.metadataNoModules')}</Typography>
      ) : (
        <Stack divider={<Divider />}>
          {modules.map((m) => (
            <ModuleEditor
              key={m.name}
              cameraId={cameraId}
              kind={kind}
              configToken={configToken}
              module={m}
              descriptions={descriptions}
              onChanged={() => setReload((n) => n + 1)}
            />
          ))}
        </Stack>
      )}
      {descriptions.length > 0 && (
        <Stack direction="row" spacing={1} alignItems="center">
          <TextField
            select
            size="small"
            label={t('onvifSettings.metadataModuleType')}
            value={newType}
            onChange={(e) => setNewType(e.target.value)}
            sx={{ minWidth: 200 }}
          >
            {descriptions.map((d) => (
              <MenuItem key={d.type} value={d.type}>{d.type}</MenuItem>
            ))}
          </TextField>
          <TextField
            size="small"
            label={t('onvifSettings.metadataModuleName')}
            value={newName}
            onChange={(e) => setNewName(e.target.value)}
          />
          <Button
            size="small"
            startIcon={adding ? <CircularProgress size={16} /> : <AddIcon />}
            disabled={adding || !newType || !newName}
            onClick={() => void add()}
          >
            {t('onvifSettings.metadataAdd')}
          </Button>
        </Stack>
      )}
    </Stack>
  )
}

// ── Section ─────────────────────────────────────────────────────────────────

/**
 * ONVIF Profile M. Turning the camera's own object boxes on takes two attachments to the profile
 * the stream is pulled from: a MetadataConfiguration with Analytics enabled (which is what puts a
 * metadata track in the RTSP session at all) and a VideoAnalyticsConfiguration (whose modules and
 * rules produce the objects). Both are driven from here, which is why the profile selector sits at
 * the top rather than inside either group.
 */
export default function OnvifMetadataSection({ cameraId }: { cameraId: string }) {
  const { t } = useTranslation()
  const [profiles, setProfiles] = useState<OnvifProfileInfo[]>([])
  const [profileToken, setProfileToken] = useState('')
  const [metadata, setMetadata] = useState<MetadataConfigDto[] | null>(null)
  const [analytics, setAnalytics] = useState<OnvifAnalyticsConfigDto[]>([])
  const [error, setError] = useState<string | null>(null)
  const [reload, setReload] = useState(0)
  const [busy, setBusy] = useState(false)

  const refresh = useCallback(() => setReload((n) => n + 1), [])

  useEffect(() => {
    setProfileToken('')
    apiFetch<OnvifProfileInfo[]>(`${base(cameraId)}/profiles`)
      .then((p) => { setProfiles(p); setProfileToken(p[0]?.token ?? '') })
      .catch(() => { setProfiles([]); setProfileToken('') })
  }, [cameraId])

  useEffect(() => {
    let cancelled = false
    setMetadata(null)

    apiFetch<MetadataConfigsResponseDto>(`${base(cameraId)}/metadata-configs`)
      .then((r) => { if (!cancelled) { setMetadata(r.configs); setError(r.error || null) } })
      .catch((e: unknown) => { if (!cancelled) { setMetadata([]); setError(errText(e)) } })

    apiFetch<OnvifAnalyticsConfigsResponseDto>(`${base(cameraId)}/analytics-configs`)
      .then((r) => { if (!cancelled) setAnalytics(r.configs) })
      .catch(() => { if (!cancelled) setAnalytics([]) })

    return () => { cancelled = true }
  }, [cameraId, reload])

  // Which analytics engine the modules/rules below belong to: the one attached to the selected
  // profile if there is one, otherwise the camera's first, so the lists are never empty just
  // because nothing is attached yet.
  const activeAnalytics =
    analytics.find((c) => c.attachedProfileTokens.includes(profileToken)) ?? analytics[0]

  const analyticsAttached = activeAnalytics?.attachedProfileTokens.includes(profileToken) ?? false

  const toggleAnalyticsAttach = async () => {
    if (!activeAnalytics || !profileToken) return
    setBusy(true); setError(null)
    try {
      const url = `${base(cameraId)}/profiles/${encodeURIComponent(profileToken)}/analytics-config`
        + `?configToken=${encodeURIComponent(activeAnalytics.token)}`
      await apiFetch(url, { method: analyticsAttached ? 'DELETE' : 'POST' })
      refresh()
    } catch (e) {
      setError(errText(e))
    } finally {
      setBusy(false)
    }
  }

  return (
    <Stack spacing={2}>
      <Alert severity="info" sx={{ py: 0 }}>{t('onvifSettings.metadataHint')}</Alert>
      {error && <Alert severity="error" onClose={() => setError(null)}>{error}</Alert>}

      <TextField
        select
        size="small"
        label={t('onvifSettings.metadataProfile')}
        value={profileToken}
        onChange={(e) => setProfileToken(e.target.value)}
        sx={{ maxWidth: 320 }}
      >
        {profiles.map((p) => (
          <MenuItem key={p.token} value={p.token}>{p.name || p.token}</MenuItem>
        ))}
      </TextField>

      <Stack spacing={1}>
        <Typography variant="subtitle2">{t('onvifSettings.metadataConfigsGroup')}</Typography>
        {metadata === null ? (
          <Skeleton variant="rounded" height={40} />
        ) : metadata.length === 0 ? (
          <Typography variant="body2" color="text.secondary">{t('onvifSettings.metadataNoConfigs')}</Typography>
        ) : (
          <Stack divider={<Divider />}>
            {metadata.map((c) => (
              <MetadataConfigRow
                key={c.token}
                cameraId={cameraId}
                config={c}
                profileToken={profileToken}
                onChanged={refresh}
              />
            ))}
          </Stack>
        )}
      </Stack>

      <Divider />

      <Stack spacing={1}>
        <Typography variant="subtitle2">{t('onvifSettings.metadataAnalyticsGroup')}</Typography>
        {!activeAnalytics ? (
          <Typography variant="body2" color="text.secondary">{t('onvifSettings.metadataNoAnalyticsConfigs')}</Typography>
        ) : (
          <Stack direction="row" alignItems="center" spacing={2}>
            <Typography variant="body2" sx={{ fontWeight: 500 }}>
              {activeAnalytics.name || activeAnalytics.token}
            </Typography>
            <Box sx={{ flex: 1 }} />
            <Button
              size="small"
              variant={analyticsAttached ? 'outlined' : 'contained'}
              disabled={busy || !profileToken}
              onClick={() => void toggleAnalyticsAttach()}
            >
              {busy ? <CircularProgress size={16} />
                : analyticsAttached ? t('onvifSettings.metadataDetach') : t('onvifSettings.metadataAttach')}
            </Button>
          </Stack>
        )}
      </Stack>

      {activeAnalytics && (
        <>
          <Divider />
          <ModuleGroup
            cameraId={cameraId}
            kind="analytics-modules"
            configToken={activeAnalytics.token}
            title={t('onvifSettings.metadataModulesGroup')}
          />
          <Divider />
          <ModuleGroup
            cameraId={cameraId}
            kind="rules"
            configToken={activeAnalytics.token}
            title={t('onvifSettings.metadataRulesGroup')}
          />
        </>
      )}
    </Stack>
  )
}
