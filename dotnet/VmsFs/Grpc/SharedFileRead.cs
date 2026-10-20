namespace VmsFs;

// Opens a clip or an export for reading without getting in anybody else's way. video_srv keeps the
// live-edge segment open for writing while it is already in the archive, and ClipCleaner and
// RoiTranscodeScheduler delete or replace files a reader may still have open. Linux enforces none
// of this, so nothing changes there; on Windows FileShare.Read alone refuses to open a file that is
// being written, and makes every delete or replace of it fail for as long as the read lasts.
internal static class SharedFileRead
{
  public static FileStream Open(string path, int bufferSize) =>
    new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize, useAsync: true);
}
