import { useEffect, useState } from 'react'
import { Box, Stack, TextField } from '@mui/material'
import {
  normalizeType, normalizeValue, toLocalInput, type PropertyType,
} from './propertyTypes'

type Props = {
  /** The stored `visual_type`; anything the editor does not know edits as text. */
  type: string | undefined
  value: string
  label: string
  /** The value in the spelling its type should be stored in -- see `normalizeValue`. */
  onChange: (value: string) => void
}

/**
 * The editor's control for one property value: a colour swatch, a date and time, a number, or
 * plain text. The control follows the type, so a value can only be typed the way it is stored.
 */
export default function PropertyValueField({ type, value, label, onChange }: Props) {
  const known = normalizeType(type)

  if (known === '__clr') {
    return (
      <Stack direction="row" spacing={1} alignItems="center">
        <Box
          component="input"
          type="color"
          value={/^#[0-9a-f]{6}$/i.test(value) ? value : '#90a4ae'}
          onChange={(event: React.ChangeEvent<HTMLInputElement>) => onChange(event.target.value)}
          sx={{ width: 40, height: 32, p: 0, border: 0, bgcolor: 'transparent', cursor: 'pointer' }}
        />
        <TextField
          size="small"
          label={label}
          value={value}
          onChange={(event) => onChange(event.target.value)}
          sx={{ flex: 1 }}
        />
      </Stack>
    )
  }

  if (known === '__datetime' || known === '__double' || known === '__int') {
    return <TypedValueField kind={known} value={value} label={label} onChange={onChange} />
  }

  return (
    <TextField
      size="small"
      label={label}
      value={value}
      onChange={(event) => onChange(event.target.value)}
    />
  )
}

/**
 * A number or an instant is edited as text: while the value is half-typed the native control
 * reports nothing usable, and a number input rejects "-" and "3," outright. The field keeps what
 * is on screen and commits it in its stored spelling when it is left.
 */
function TypedValueField({
  kind, value, label, onChange,
}: { kind: PropertyType; value: string; label: string; onChange: (value: string) => void }) {
  const [draft, setDraft] = useState(() => shown(kind, value))

  useEffect(() => {
    setDraft(shown(kind, value))
  }, [kind, value])

  const commit = () => onChange(normalizeValue(kind, draft))

  return (
    <TextField
      size="small"
      label={label}
      type={kind === '__datetime' ? 'datetime-local' : 'number'}
      value={draft}
      onChange={(event) => setDraft(event.target.value)}
      onBlur={commit}
      onKeyDown={(event) => {
        if (event.key === 'Enter') commit()
      }}
      slotProps={{
        inputLabel: { shrink: true },
        htmlInput: kind === '__datetime'
          ? { step: 60 }
          : { step: kind === '__int' ? 1 : 'any' },
      }}
    />
  )
}

function shown(kind: PropertyType, value: string): string {
  return kind === '__datetime' ? toLocalInput(value) : value
}
