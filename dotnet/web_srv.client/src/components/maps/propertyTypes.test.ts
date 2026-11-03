import { describe, expect, it } from 'vitest'
import {
  PROPERTY_TYPES, PROPERTY_TYPE_LABELS, fromLocalInput, normalizeType, normalizeValue, toLocalInput,
} from './propertyTypes'

describe('property types', () => {
  it('offers plain text as the default and names every type it offers', () => {
    expect(PROPERTY_TYPES[0]).toBe('')
    for (const type of PROPERTY_TYPES) expect(PROPERTY_TYPE_LABELS[type]).toBeTruthy()
  })

  it('reads a type the editor no longer offers as plain text', () => {
    expect(normalizeType('__clr')).toBe('__clr')
    expect(normalizeType('__txt')).toBe('')
    expect(normalizeType('__pass')).toBe('')
    expect(normalizeType(undefined)).toBe('')
  })
})

describe('normalizeValue', () => {
  it('leaves text exactly as typed', () => {
    expect(normalizeValue('', '  Floor 3, east ')).toBe('  Floor 3, east ')
  })

  it('writes numbers in one invariant form', () => {
    expect(normalizeValue('__double', '3,50')).toBe('3.5')
    expect(normalizeValue('__double', ' 2 ')).toBe('2')
    expect(normalizeValue('__double', '.5')).toBe('0.5')
    expect(normalizeValue('__int', '7,0')).toBe('7')
  })

  it('keeps a number that does not parse, so a half-typed value is never lost', () => {
    expect(normalizeValue('__double', 'abc')).toBe('abc')
    expect(normalizeValue('__double', '')).toBe('')
    expect(normalizeValue('__int', '7.5')).toBe('7.5')
  })

  it('stores an instant as ISO 8601 UTC', () => {
    expect(normalizeValue('__datetime', '2026-11-03T10:00'))
      .toBe(new Date(2026, 10, 3, 10, 0).toISOString())
    expect(normalizeValue('__datetime', 'nonsense')).toBe('nonsense')
  })

  it('leaves a value alone when its type is unknown', () => {
    expect(normalizeValue('__pass', 'secret')).toBe('secret')
  })
})

describe('datetime-local conversion', () => {
  it('round-trips a local instant', () => {
    expect(toLocalInput(fromLocalInput('2026-11-03T10:00'))).toBe('2026-11-03T10:00')
  })

  it('shows an empty input for an empty or unparseable instant', () => {
    expect(toLocalInput('')).toBe('')
    expect(toLocalInput('nonsense')).toBe('')
    expect(fromLocalInput('')).toBe('')
    expect(fromLocalInput('nonsense')).toBe('')
  })
})
