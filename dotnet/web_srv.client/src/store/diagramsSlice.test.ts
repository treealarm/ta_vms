import { describe, expect, it } from 'vitest'
import type { MapDiagramContent } from '../dto/mapDtos'
import reducer, { loadDiagramContent } from './diagramsSlice'

// What the store makes of two reads that overlap: leaving one diagram for another launches both —
// a drag opens the element it drops and the diagram it was dropped on reloads — and each answer
// keeps its own entry, so whichever lands last cannot blank the paper being shown.
const FIRST = 'first'
const SECOND = 'second'
const FIRST_REQUEST = 'request-first'
const SECOND_REQUEST = 'request-second'

const content = (diagramId: string): MapDiagramContent => ({
  diagramId,
  content: [],
  children: [],
})

const started = (requestId: string, id: string) =>
  loadDiagramContent.pending(requestId, { id, depth: 1 })

const answered = (requestId: string, id: string) =>
  loadDiagramContent.fulfilled({ id, content: content(id) }, requestId, { id, depth: 1 })

describe('the diagram content read', () => {
  it('keeps the answer under the diagram it was read for', () => {
    let state = reducer(undefined, started(FIRST_REQUEST, FIRST))
    expect(state.loading).toBe(true)

    state = reducer(state, answered(FIRST_REQUEST, FIRST))
    expect(state.loading).toBe(false)
    expect(state.content[FIRST]?.diagramId).toBe(FIRST)
  })

  it('keeps both answers when one read was overtaken by the next', () => {
    let state = reducer(undefined, started(FIRST_REQUEST, FIRST))
    state = reducer(state, started(SECOND_REQUEST, SECOND))
    state = reducer(state, answered(SECOND_REQUEST, SECOND))
    state = reducer(state, answered(FIRST_REQUEST, FIRST))

    // The older answer lands last; both diagrams keep their content and neither is blanked.
    expect(state.content[FIRST]?.diagramId).toBe(FIRST)
    expect(state.content[SECOND]?.diagramId).toBe(SECOND)
    expect(state.loading).toBe(false)
  })
})
