import { describe, expect, it } from 'vitest'
import type { MapMarker } from '../dto/mapDtos'
import { treeRows, type TreePage } from './nodeTree'

const marker = (id: string, parentId?: string): MapMarker => ({ id, parentId, name: id })

const markers: Record<string, MapMarker> = {
  a: marker('a', 'root'),
  b: marker('b', 'root'),
  c: marker('c', 'a'),
  d: marker('d', 'c'),
}

const pages: Record<string, TreePage | undefined> = {
  root: { ids: ['a', 'b'], loaded: true },
  a: { ids: ['c'], loaded: true },
  b: { ids: [], loaded: false },
  c: { ids: ['d'], loaded: true },
}

const roots = [markers.a, markers.b]
const ids = (rows: ReturnType<typeof treeRows>) => rows.map((row) => row.marker.id)

describe('treeRows', () => {
  it('shows the roots alone while nothing is open', () => {
    expect(ids(treeRows(roots, pages, markers, new Set()))).toEqual(['a', 'b'])
  })

  it('unfolds an open row into its page', () => {
    const rows = treeRows(roots, pages, markers, new Set(['a']))
    expect(ids(rows)).toEqual(['a', 'c', 'b'])
    expect(rows.find((row) => row.marker.id === 'c')?.depth).toBe(1)
  })

  it('goes as deep as the reader has opened', () => {
    const rows = treeRows(roots, pages, markers, new Set(['a', 'c']))
    expect(ids(rows)).toEqual(['a', 'c', 'd', 'b'])
    expect(rows.find((row) => row.marker.id === 'd')?.depth).toBe(2)
  })

  it('does not walk a page that has not been read', () => {
    expect(ids(treeRows(roots, pages, markers, new Set(['b'])))).toEqual(['a', 'b'])
  })

  it('skips a child the store does not know, rather than drawing a blank row', () => {
    const partial = { ...pages, a: { ids: ['c', 'gone'], loaded: true } }
    expect(ids(treeRows(roots, partial, markers, new Set(['a'])))).toEqual(['a', 'c', 'b'])
  })

  it('draws a node once even if the data closes a cycle', () => {
    const cyclic = { ...pages, c: { ids: ['a'], loaded: true } }
    expect(ids(treeRows(roots, cyclic, markers, new Set(['a', 'c'])))).toEqual(['a', 'c', 'b'])
  })
})
