import { describe, expect, it } from 'vitest'
import type { MapDiagramContent } from '../dto/mapDtos'
import reducer, { loadDiagramContent } from './diagramsSlice'

// What the store makes of two reads that overlap: leaving one diagram for another launches both —
// a drag opens the element it drops and the diagram it was dropped on reloads — and each answer
// keeps its own entry, so whichever lands last cannot blank the paper being shown nor hang its
// spinner or its failure on the other one.
const FIRST = 'first'
const SECOND = 'second'
const FIRST_REQUEST = 'request-first'
const SECOND_REQUEST = 'request-second'
const READ_FAILURE = 'the diagram could not be read'

const content = (diagramId: string, depth = 1): MapDiagramContent => ({
  diagramId,
  depth,
  content: [],
  children: [],
})

const started = (requestId: string, id: string, depth = 1) =>
  loadDiagramContent.pending(requestId, { id, depth })

const answered = (requestId: string, id: string, depth = 1) =>
  loadDiagramContent.fulfilled({ id, content: content(id, depth) }, requestId, { id, depth })

const failed = (requestId: string, id: string) =>
  loadDiagramContent.rejected(new Error(READ_FAILURE), requestId, { id, depth: 1 })

describe('the diagram content read', () => {
  it('keeps the answer under the diagram it was read for', () => {
    let state = reducer(undefined, started(FIRST_REQUEST, FIRST))
    expect(state.loading[FIRST]).toBe(true)

    state = reducer(state, answered(FIRST_REQUEST, FIRST))
    expect(state.loading[FIRST]).toBe(false)
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
    expect(state.loading[FIRST]).toBe(false)
    expect(state.loading[SECOND]).toBe(false)
  })

  it('keeps a failure with the diagram it was read for', () => {
    let state = reducer(undefined, started(FIRST_REQUEST, FIRST))
    state = reducer(state, started(SECOND_REQUEST, SECOND))
    state = reducer(state, failed(FIRST_REQUEST, FIRST))

    expect(state.error[FIRST]).toBe(READ_FAILURE)
    expect(state.loading[FIRST]).toBe(false)
    // The diagram that is open did not fail: its own read is still on and its message is not shown.
    expect(state.error[SECOND]).toBeUndefined()
    expect(state.loading[SECOND]).toBe(true)
  })

  it('keeps the newest answer when a read of the same diagram is overtaken', () => {
    // A depth change reads the diagram again while its first read is still on; the old answer
    // arriving last must not shrink the paper back to the depth the reader has left.
    let state = reducer(undefined, started(FIRST_REQUEST, FIRST))
    state = reducer(state, started(SECOND_REQUEST, FIRST, 2))
    state = reducer(state, answered(SECOND_REQUEST, FIRST, 2))
    state = reducer(state, answered(FIRST_REQUEST, FIRST, 1))

    expect(state.content[FIRST]?.depth).toBe(2)
    expect(state.loading[FIRST]).toBe(false)
  })

  it('does not hang an overtaken failure on the read that replaced it', () => {
    let state = reducer(undefined, started(FIRST_REQUEST, FIRST))
    state = reducer(state, started(SECOND_REQUEST, FIRST, 2))
    state = reducer(state, failed(FIRST_REQUEST, FIRST))

    // The read the reader is waiting for is still on, and has not failed.
    expect(state.error[FIRST]).toBeUndefined()
    expect(state.loading[FIRST]).toBe(true)
  })
})
