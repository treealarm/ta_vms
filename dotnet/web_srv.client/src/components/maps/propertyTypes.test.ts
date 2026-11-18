import { describe, expect, it } from 'vitest'
import {
  PROPERTY_TYPES, PROPERTY_TYPE_LABELS, fromLocalInput, normalizeType, normalizeValue, toLocalInput,
} from './propertyTypes'

// The fixtures these tests share: the type codes, the values they are given and what must survive.
const CLR = '__clr'
const DOUBLE = '__double'
const INT = '__int'
const DATETIME = '__datetime'
const PASS = '__pass'
const NONSENSE = 'nonsense'
const LOCAL_INSTANT = '2026-11-03T10:00'
const SECRET = 'secret'
const SEVEN_AND_HALF = '7.5'
const ABC = 'abc'

describe('property types', () => {
  it('offers plain text as the default and names every type it offers', () => {
    expect(PROPERTY_TYPES[0]).toBe('')
    for (const type of PROPERTY_TYPES) expect(PROPERTY_TYPE_LABELS[type]).toBeTruthy()
  })

  it('reads a type the editor no longer offers as plain text', () => {
    expect(normalizeType(CLR)).toBe(CLR)
    expect(normalizeType('__txt')).toBe('')
    expect(normalizeType(PASS)).toBe('')
    expect(normalizeType(undefined)).toBe('')
  })
})

describe('normalizeValue', () => {
  it('leaves text exactly as typed', () => {
    expect(normalizeValue('', '  Floor 3, east ')).toBe('  Floor 3, east ')
  })

  it('writes numbers in one invariant form', () => {
    expect(normalizeValue(DOUBLE, '3,50')).toBe('3.5')
    expect(normalizeValue(DOUBLE, ' 2 ')).toBe('2')
    expect(normalizeValue(DOUBLE, '.5')).toBe('0.5')
    expect(normalizeValue(INT, '7,0')).toBe('7')
  })

  it('keeps a number that does not parse, so a half-typed value is never lost', () => {
    expect(normalizeValue(DOUBLE, ABC)).toBe(ABC)
    expect(normalizeValue(DOUBLE, '')).toBe('')
    expect(normalizeValue(INT, SEVEN_AND_HALF)).toBe(SEVEN_AND_HALF)
  })

  it('stores an instant as ISO 8601 UTC', () => {
    expect(normalizeValue(DATETIME, LOCAL_INSTANT))
      .toBe(new Date(2026, 10, 3, 10, 0).toISOString())
    expect(normalizeValue(DATETIME, NONSENSE)).toBe(NONSENSE)
  })

  it('leaves a value alone when its type is unknown', () => {
    expect(normalizeValue(PASS, SECRET)).toBe(SECRET)
  })
})

describe('datetime-local conversion', () => {
  it('round-trips a local instant', () => {
    expect(toLocalInput(fromLocalInput(LOCAL_INSTANT))).toBe(LOCAL_INSTANT)
  })

  it('shows an empty input for an empty or unparseable instant', () => {
    expect(toLocalInput('')).toBe('')
    expect(toLocalInput(NONSENSE)).toBe('')
    expect(fromLocalInput('')).toBe('')
    expect(fromLocalInput(NONSENSE)).toBe('')
  })
})
