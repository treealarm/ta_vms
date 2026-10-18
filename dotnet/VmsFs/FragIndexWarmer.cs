using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VmsFs;

// Proactively builds a clip's fmp4 fragment index shortly after it is ingested (finalized), so the
// first playback doesn't pay for the parse. A single worker drains a bounded queue sequentially;
// when the queue is full the enqueue is simply dropped and that clip falls back to lazy parsing on
// first GetClipIndexes. Best-effort — never blocks ingest.
public sealed class FragIndexWarmer(
  ClipStorage storage,
  StorageListCache storageListCache,
  ILogger<FragIndexWarmer> logger) : BackgroundService
{
  private readonly Channel<Guid> _queue = Channel.CreateBounded<Guid>(
    new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

  // Returns false when the queue is full (caller relies on lazy parsing instead).
  public bool Enqueue(Guid clipUid) => _queue.Writer.TryWrite(clipUid);

  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    await foreach (var uid in _queue.Reader.ReadAllAsync(stoppingToken))
    {
      try
      {
        var clips = await storage.GetClipsForIndexAsync([uid], stoppingToken);
        if (!clips.TryGetValue(uid, out var info) || info.FragIndex is not null)
          continue; // gone or already cached

        var storages = await storageListCache.GetAsync(stoppingToken);
        var root = storageListCache.Find(storages, info.StorageId)?.RootPath;
        if (string.IsNullOrWhiteSpace(root))
          continue;

        var idx = await ClipIndexParser.ReadAsync(Path.Combine(root, info.FilePath), stoppingToken);
        if (idx is not null)
          await storage.SaveFragIndexAsync(uid, ClipIndexParser.Serialize(idx), stoppingToken);
      }
      catch (OperationCanceledException) { throw; }
      catch (Exception ex)
      {
        logger.LogDebug(ex, "FragIndexWarmer: failed to warm clip {ClipUid}", uid);
      }
    }
  }
}
