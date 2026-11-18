import { describe, expect, it } from 'vitest'
import type { MapMarker } from '../dto/mapDtos'
import { treeRows, type TreePage } from './nodeTree'

// The fixtures these tests share: a root with two children, and a chain below one of them.
const ROOT = 'root'
const NODE_A = 'a'
const NODE_B = 'b'
const NODE_C = 'c'
const NODE_D = 'd'

const marker = (id: string, parentId?: string): MapMarker => ({ id, parentId, name: id })

const markers: Record<string, MapMarker> = {
  a: marker(NODE_A, ROOT),
  b: marker(NODE_B, ROOT),
  c: marker(NODE_C, NODE_A),
  d: marker(NODE_D, NODE_C),
}

const pages: Record<string, TreePage | undefined> = {
  root: { ids: [NODE_A, NODE_B], loaded: true },
  a: { ids: [NODE_C], loaded: true },
  b: { ids: [], loaded: false },
  c: { ids: [NODE_D], loaded: true },
}

const roots = [markers.a, markers.b]
const ids = (rows: ReturnType<typeof treeRows>) => rows.map((row) => row.marker.id)

describe('treeRows', () => {
  it('shows the roots alone while nothing is open', () => {
    expect(ids(treeRows(roots, pages, markers, new Set()))).toEqual([NODE_A, NODE_B])
  })

  it('unfolds an open row into its page', () => {
    const rows = treeRows(roots, pages, markers, new Set([NODE_A]))
    expect(ids(rows)).toEqual([NODE_A, NODE_C, NODE_B])
    expect(rows.find((row) => row.marker.id === NODE_C)?.depth).toBe(1)
  })

  it('goes as deep as the reader has opened', () => {
    const rows = treeRows(roots, pages, markers, new Set([NODE_A, NODE_C]))
    expect(ids(rows)).toEqual([NODE_A, NODE_C, NODE_D, NODE_B])
    expect(rows.find((row) => row.marker.id === NODE_D)?.depth).toBe(2)
  })

  it('does not walk a page that has not been read', () => {
    expect(ids(treeRows(roots, pages, markers, new Set([NODE_B])))).toEqual([NODE_A, NODE_B])
  })

  it('skips a child the store does not know, rather than drawing a blank row', () => {
    const partial = { ...pages, a: { ids: [NODE_C, 'gone'], loaded: true } }
    expect(ids(treeRows(roots, partial, markers, new Set([NODE_A])))).toEqual([NODE_A, NODE_C, NODE_B])
  })

  it('draws a node once even if the data closes a cycle', () => {
    const cyclic = { ...pages, c: { ids: [NODE_A], loaded: true } }
    expect(ids(treeRows(roots, cyclic, markers, new Set([NODE_A, NODE_C])))).toEqual([NODE_A, NODE_C, NODE_B])
  })
})
