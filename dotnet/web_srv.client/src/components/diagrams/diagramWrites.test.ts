import { describe, expect, it } from 'vitest'
import { contentWrite } from './diagramWrites'

// A copy's type and picture are the diagram's: the server writes them there, and an empty value
// is a request to clear the diagram's. An ordinary diagram rewrites its own row whole, so an empty
// value there stays absent.
describe('the content a diagram save names', () => {
  const TYPE = 'type/panel'
  const FILE = 'file/panel'

  it('names both fields on a copy, so an empty one clears the diagram', () => {
    expect(contentWrite(true, '', '')).toEqual({ dgrType: '', fileId: '' })
    expect(contentWrite(true, TYPE, FILE)).toEqual({ dgrType: TYPE, fileId: FILE })
  })

  it('leaves an empty field out of an ordinary diagram write', () => {
    expect(contentWrite(false, '', '')).toEqual({ dgrType: undefined, fileId: undefined })
    expect(contentWrite(false, TYPE, FILE)).toEqual({ dgrType: TYPE, fileId: FILE })
  })
})
