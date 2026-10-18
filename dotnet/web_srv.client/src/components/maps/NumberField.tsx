import { useEffect, useState } from 'react'
import { TextField, type TextFieldProps } from '@mui/material'

type Props = Omit<TextFieldProps, 'value' | 'onChange' | 'onBlur' | 'type'> & {
  value: number
  /** Called when the field is left or Enter is pressed with a valid number that differs. */
  onCommit: (value: number) => void
  min?: number
  max?: number
  step?: number
}

/**
 * A number edited in place on a map panel and saved when the field is left, not on every key: a
 * placement is saved to the server and announced to every service, and "4" on the way to "4.5"
 * should not be.
 */
export default function NumberField({ value, onCommit, min, max, step, ...rest }: Props) {
  const [text, setText] = useState(String(value))
  useEffect(() => { setText(String(value)) }, [value])

  const commit = () => {
    const v = Number(text.replace(',', '.'))
    if (text.trim() === '' || !Number.isFinite(v) || (min !== undefined && v < min) || (max !== undefined && v > max)) {
      setText(String(value))
      return
    }
    if (v !== value) onCommit(v)
  }

  return (
    <TextField
      {...rest}
      size="small"
      value={text}
      onChange={(e) => setText(e.target.value)}
      onBlur={commit}
      onKeyDown={(e) => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur() }}
      slotProps={{ htmlInput: { inputMode: 'decimal', step } }}
    />
  )
}
