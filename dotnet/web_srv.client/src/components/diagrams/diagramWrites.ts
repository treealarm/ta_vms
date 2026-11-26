/**
 * The type and picture a diagram-row save carries. A copy's are the diagram's, and an empty one is
 * a request to clear it — so a copy names both, even when empty. An ordinary diagram has a row of
 * its own that the whole request rewrites, so an empty value there travels as absent, which is
 * how clearing it has always worked.
 */
export function contentWrite(
  copy: boolean, typeId: string, fileId: string,
): { dgrType?: string; fileId?: string } {
  return copy
    ? { dgrType: typeId, fileId }
    : { dgrType: typeId || undefined, fileId: fileId || undefined }
}
