import { describe, expect, it } from 'vitest'

import { isAbsoluteHostPath, normalizeDisplayPath } from './hostPath'

describe('normalizeDisplayPath', () => {
  it('keeps a path that has nothing to collapse', () => {
    expect(normalizeDisplayPath('/data/media_storage')).toBe('/data/media_storage')
  })

  it('collapses a parent step in a POSIX path', () => {
    // The value a dev launch config writes: ${workspaceFolder}/../media_storage.
    expect(normalizeDisplayPath('/mnt/wdblue/tests/ta_vms/../media_storage'))
      .toBe('/mnt/wdblue/tests/media_storage')
  })

  it('drops a current-directory step and a trailing separator', () => {
    expect(normalizeDisplayPath('/data/./video/')).toBe('/data/video')
  })

  it('does not climb above the root', () => {
    expect(normalizeDisplayPath('/../../data')).toBe('/data')
  })

  it('collapses a Windows path and keeps backslashes', () => {
    expect(normalizeDisplayPath('C:\\video\\..\\archive')).toBe('C:\\archive')
  })

  it('keeps forward slashes on a Windows drive', () => {
    expect(normalizeDisplayPath('D:/video/./archive')).toBe('D:/video/archive')
  })

  it('never pops above a UNC share', () => {
    expect(normalizeDisplayPath('\\\\server\\share\\..\\..\\video'))
      .toBe('\\\\server\\share\\video')
  })

  it('leaves an empty value empty', () => {
    expect(normalizeDisplayPath('')).toBe('')
  })

  it('agrees with the absolute-path check it is displayed beside', () => {
    expect(isAbsoluteHostPath('/data')).toBe(true)
    expect(isAbsoluteHostPath('relative/path')).toBe(false)
  })
})
