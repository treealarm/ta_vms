import { useEffect, useMemo, useState } from 'react'
import {
  Autocomplete,
  Box,
  Button,
  IconButton,
  Stack,
  TextField,
  Tooltip,
  Typography,
} from '@mui/material'
import DeleteIcon from '@mui/icons-material/Delete'
import CheckIcon from '@mui/icons-material/Check'
import CloseIcon from '@mui/icons-material/Close'
import EditIcon from '@mui/icons-material/Edit'
import { useTranslation } from 'react-i18next'
import type { ObservedTopicDto, OnvifAlarmFilterDto } from '../dto/dtos'

type TopicOption = {
  filterText: string
  label: string
  description: string
}

const TOPIC_PRESET_KEYS = [
  { key: 'motion',          filterText: 'Motion' },
  { key: 'faceDetection',   filterText: 'FaceDetection' },
  { key: 'lineCrossing',    filterText: 'LineCrossing' },
  { key: 'fieldDetection',  filterText: 'FieldDetection' },
  { key: 'alarmList',       filterText: 'AlarmList' },
  { key: 'objectDetection', filterText: 'ObjectDetection' },
]

function lastTopicSegment(topic: string): string {
  return topic.split('/').filter(Boolean).pop() ?? topic
}

function buildTopicOptions(
  observed: ObservedTopicDto[],
  t: (key: string) => string,
): TopicOption[] {
  return observed.map(ot => {
    const matched = TOPIC_PRESET_KEYS.find(p =>
      ot.topic.toLowerCase().includes(p.filterText.toLowerCase()))
    return {
      filterText: ot.topic,
      label: matched
        ? t(`onvifFilters.topicPresets.${matched.key}.label`)
        : lastTopicSegment(ot.topic),
      description: ot.topic,
    }
  })
}

type Props = { cameraId: string }

async function upsertFilter(cameraId: string, filter: OnvifAlarmFilterDto) {
  const resp = await fetch(
    `/api/onvif/cameras/${encodeURIComponent(cameraId)}/alarm-filters`,
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(filter),
    },
  )
  if (!resp.ok) throw new Error(await resp.text())
}

function TopicAutocomplete({ value, onChange, topicOptions, label, autoFocus }: {
  value: string
  onChange: (v: string) => void
  topicOptions: TopicOption[]
  label: string
  autoFocus?: boolean
}) {
  return (
    <Autocomplete
      freeSolo
      openOnFocus
      options={topicOptions}
      getOptionLabel={(opt) => typeof opt === 'string' ? opt : opt.filterText}
      groupBy={(opt) => opt.label}
      inputValue={value}
      onInputChange={(_, val) => onChange(val)}
      onChange={(_, val) => { if (val && typeof val !== 'string') onChange(val.filterText) }}
      sx={{ flex: 1 }}
      renderInput={(params) => (
        <TextField {...params} label={label} size="small" autoFocus={autoFocus} />
      )}
      renderOption={({ key, ...props }, opt) => (
        <li key={key} {...props}>
          <Typography variant="body2" color="text.secondary">{opt.description}</Typography>
        </li>
      )}
    />
  )
}

function FilterRow({ filter, cameraId, topicOptions, onSaved, onDeleted }: {
  filter: OnvifAlarmFilterDto
  cameraId: string
  topicOptions: TopicOption[]
  onSaved: () => void
  onDeleted: () => void
}) {
  const { t } = useTranslation()
  const [editing, setEditing] = useState(false)
  const [text, setText] = useState(filter.filterText)
  const [textFilter, setTextFilter] = useState(filter.textFilter ?? '')
  const [timeout, setTimeout] = useState(filter.timeoutSeconds)
  const [saving, setSaving] = useState(false)

  const handleSave = async () => {
    if (!text.trim()) return
    setSaving(true)
    try {
      await upsertFilter(cameraId, { ...filter, filterText: text.trim(), textFilter: textFilter.trim() || undefined, timeoutSeconds: timeout })
      setEditing(false)
      onSaved()
    } finally {
      setSaving(false)
    }
  }

  const handleCancel = () => {
    setText(filter.filterText)
    setTextFilter(filter.textFilter ?? '')
    setTimeout(filter.timeoutSeconds)
    setEditing(false)
  }

  const handleDelete = async () => {
    const resp = await fetch(
      `/api/onvif/cameras/${encodeURIComponent(cameraId)}/alarm-filters/${encodeURIComponent(filter.id!)}`,
      { method: 'DELETE' },
    )
    if (resp.ok) onDeleted()
  }

  if (editing) {
    return (
      <Box component="form" onSubmit={(e) => { e.preventDefault(); void handleSave() }}>
        <Stack spacing={1}>
          <Stack direction="row" spacing={1} alignItems="center">
            <TopicAutocomplete
              value={text}
              onChange={setText}
              topicOptions={topicOptions}
              label={t('onvifFilters.filterText')}
              autoFocus
            />
            <TextField
              label={t('onvifFilters.textFilter')}
              value={textFilter}
              onChange={(e) => setTextFilter(e.target.value)}
              size="small"
              sx={{ flex: 1 }}
              placeholder={t('onvifFilters.textFilterPlaceholder')}
            />
            <TextField
              type="number"
              value={timeout}
              onChange={(e) => setTimeout(Math.max(1, Number(e.target.value)))}
              size="small"
              sx={{ width: 90 }}
              inputProps={{ min: 1 }}
            />
            <Tooltip title={t('common.save')}>
              <span>
                <IconButton size="small" type="submit" disabled={!text.trim() || saving} color="primary">
                  <CheckIcon fontSize="small" />
                </IconButton>
              </span>
            </Tooltip>
            <Tooltip title={t('common.cancel')}>
              <IconButton size="small" onClick={handleCancel}>
                <CloseIcon fontSize="small" />
              </IconButton>
            </Tooltip>
          </Stack>
        </Stack>
      </Box>
    )
  }

  return (
    <Stack direction="row" spacing={1} alignItems="center">
      <Box sx={{ flex: 1, minWidth: 0 }}>
        <Typography variant="body2" noWrap>{filter.filterText}</Typography>
        {filter.textFilter && (
          <Typography variant="caption" color="text.secondary" noWrap>+ &quot;{filter.textFilter}&quot;</Typography>
        )}
      </Box>
      <Typography variant="body2" color="text.secondary" sx={{ whiteSpace: 'nowrap' }}>
        {filter.timeoutSeconds}s
      </Typography>
      <Tooltip title={t('recording.deleteTooltip')}>
        <IconButton size="small" onClick={() => setEditing(true)}>
          <EditIcon fontSize="small" />
        </IconButton>
      </Tooltip>
      <Tooltip title={t('onvifSubs.removeFilter')}>
        <IconButton size="small" color="error" onClick={() => void handleDelete()}>
          <DeleteIcon fontSize="small" />
        </IconButton>
      </Tooltip>
    </Stack>
  )
}

export default function OnvifAlarmFiltersPanel({ cameraId }: Props) {
  const { t } = useTranslation()
  const [filters, setFilters] = useState<OnvifAlarmFilterDto[]>([])
  const [observedTopics, setObservedTopics] = useState<ObservedTopicDto[]>([])
  const [newText, setNewText] = useState('')
  const [newTextFilter, setNewTextFilter] = useState('')
  const [newTimeout, setNewTimeout] = useState(30)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const topicOptions = useMemo(
    () => buildTopicOptions(observedTopics, t),
    [observedTopics, t],
  )

  const load = async () => {
    setLoading(true)
    setError(null)
    try {
      const [filtersResp, topicsResp] = await Promise.all([
        fetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/alarm-filters`),
        fetch(`/api/onvif/cameras/${encodeURIComponent(cameraId)}/observed-topics`),
      ])
      if (!filtersResp.ok) throw new Error(await filtersResp.text())
      setFilters(await filtersResp.json())
      if (topicsResp.ok) setObservedTopics(await topicsResp.json())
    } catch (e) {
      setError(String(e))
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => { void load() }, [cameraId])

  const handleAdd = async () => {
    if (!newText.trim()) return
    setError(null)
    try {
      await upsertFilter(cameraId, {
        cameraId,
        filterText: newText.trim(),
        textFilter: newTextFilter.trim() || undefined,
        timeoutSeconds: newTimeout,
      })
      setNewText('')
      setNewTextFilter('')
      setNewTimeout(30)
      await load()
    } catch (e) {
      setError(String(e))
    }
  }

  return (
    <Stack spacing={1.5} sx={{ pt: 1 }}>
      {error && <Typography color="error" variant="body2">{error}</Typography>}
      {loading && <Typography variant="body2" color="text.secondary">{t('common.loading')}</Typography>}

      {!loading && filters.length === 0 && (
        <Typography variant="body2" color="text.secondary">{t('onvifFilters.noFilters')}</Typography>
      )}

      {filters.map((f) => (
        <FilterRow
          key={f.id}
          filter={f}
          cameraId={cameraId}
          topicOptions={topicOptions}
          onSaved={() => void load()}
          onDeleted={() => setFilters((prev) => prev.filter((x) => x.id !== f.id))}
        />
      ))}

      <Box component="form" onSubmit={(e) => { e.preventDefault(); void handleAdd() }} sx={{ pt: 0.5 }}>
        <Stack direction="row" spacing={1} alignItems="flex-start">
          <TopicAutocomplete
            value={newText}
            onChange={setNewText}
            topicOptions={topicOptions}
            label={t('onvifFilters.filterText')}
          />
          <TextField
            label={t('onvifFilters.textFilter')}
            value={newTextFilter}
            onChange={(e) => setNewTextFilter(e.target.value)}
            size="small"
            sx={{ flex: 1 }}
            placeholder={t('onvifFilters.textFilterPlaceholder')}
          />
          <TextField
            label={t('onvifFilters.timeoutSec')}
            type="number"
            value={newTimeout}
            onChange={(e) => setNewTimeout(Math.max(1, Number(e.target.value)))}
            size="small"
            sx={{ width: 120 }}
            inputProps={{ min: 1 }}
          />
          <Button
            type="submit"
            variant="outlined"
            size="small"
            disabled={!newText.trim()}
            sx={{ mt: 0.5 }}
          >
            {t('onvifFilters.addFilter')}
          </Button>
        </Stack>
      </Box>
    </Stack>
  )
}
