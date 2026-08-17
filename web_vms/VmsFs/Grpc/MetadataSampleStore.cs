using System.Text.Json;
using System.Text.Json.Serialization;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Vms.Fs;

namespace VmsFs;

/// <summary>
/// Analytics geometry stored against the archive timeline: one row per flush batch, owned by the
/// clip that covers it (see the 0007 migration).
/// </summary>
/// <remarks>
/// Note the deliberate disagreement with the read path about what an open clip (end_ts IS NULL)
/// spans. <c>ClipStorage</c> and the client both assume one minute, because a reader cannot tell a
/// live writer from a dead one and must not advertise archive that may not exist. Ingest is in the
/// opposite position: a sample arrives now, and an open clip is precisely what "now" is being
/// written into. Using the one-minute assumption here would silently discard everything recorded
/// past the first minute of a segment. Hence 'infinity' below, bounded instead by
/// <see cref="MaxOpenClipSpan"/> so a clip abandoned by a dead run cannot swallow arbitrary future
/// samples before <c>FinalizeDanglingClipsAsync</c> gets to it.
/// </remarks>
public sealed class MetadataSampleStore(
  IDbContextFactory<ClipsDbContext> dbFactory,
  FsActivityLog activityLog,
  ILogger<MetadataSampleStore> logger)
{
  /// <summary>
  /// How far past its start an open clip may still accept samples. A segment is capped at 60s by
  /// policy; three minutes leaves room for a long prealarm segment while still bounding a clip that
  /// was abandoned mid-write.
  /// </summary>
  private static readonly TimeSpan MaxOpenClipSpan = TimeSpan.FromMinutes(3);

  /// <summary>
  /// A frame landing in a hole shorter than this is pulled onto the nearest clip boundary. Segment
  /// rotation leaves a sub-second gap between the last packet written to the old segment and the
  /// first of the new one; a box is not worth losing to it. Anything longer is a real recording
  /// break, and the frame is dropped.
  /// </summary>
  private static readonly TimeSpan BoundaryGrace = TimeSpan.FromMilliseconds(1500);

  private static readonly JsonSerializerOptions JsonOpts = new()
  {
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
  };

  // Wire shape of the `samples` column. Names are one character because they repeat once per box
  // per frame; see the 0007 migration for the field mapping.
  private sealed record ChunkBox(
    [property: JsonPropertyName("i")] long I,
    [property: JsonPropertyName("k")] string K,
    [property: JsonPropertyName("c")] float C,
    [property: JsonPropertyName("x")] float X,
    [property: JsonPropertyName("y")] float Y,
    [property: JsonPropertyName("w")] float W,
    [property: JsonPropertyName("h")] float H,
    [property: JsonPropertyName("l")] string? L,
    [property: JsonPropertyName("p")] string? P);

  private sealed record ChunkFrame(
    /// <summary>Milliseconds after the chunk's from_ts.</summary>
    [property: JsonPropertyName("t")] int T,
    [property: JsonPropertyName("b")] List<ChunkBox> B);

  private sealed record CoveringClip(Guid ClipUid, DateTime StartTs, DateTime? EndTs);

  public async Task PushAsync(PushMetadataSamplesRequest req, CancellationToken ct)
  {
    if (string.IsNullOrWhiteSpace(req.CameraId) || req.Frames.Count == 0)
      return;
    if (req.Source is not ("cv" or "onvif"))
      throw new ArgumentException($"unknown metadata source '{req.Source}'");

    var streamName = req.StreamName ?? string.Empty;
    var frames = req.Frames
      .Where(f => f.Ts is not null)
      .Select(f => (Ts: f.Ts.ToDateTime().ToUniversalTime(), Frame: f))
      .OrderBy(f => f.Ts)
      .ToList();
    if (frames.Count == 0)
      return;

    var from = frames[0].Ts;
    var to = frames[^1].Ts;

    await using var db = await dbFactory.CreateDbContextAsync(ct);

    // 'infinity' for an open clip, expressed in LINQ as "no end yet" — see the class remarks.
    var clips = (await db.Clips
        .Where(c => c.CameraId == req.CameraId
                 && c.StreamName == streamName
                 && c.StartTs <= to
                 && (c.EndTs == null || c.EndTs >= from))
        .OrderBy(c => c.StartTs)
        .Select(c => new { c.ClipUid, c.StartTs, c.EndTs })
        .ToListAsync(ct))
      .Select(c => new CoveringClip(c.ClipUid, c.StartTs, c.EndTs))
      .ToList();

    // Only the newest open clip is a live writer; an older one with no end_ts was abandoned and is
    // waiting for FinalizeDanglingClipsAsync, so it must not keep swallowing current samples.
    var newestOpen = clips.Where(c => c.EndTs is null).MaxBy(c => c.StartTs);

    var byClip = new Dictionary<Guid, List<(DateTime Ts, MetadataFrame Frame)>>();
    var dropped = 0;

    foreach (var f in frames)
    {
      var clip = Resolve(clips, newestOpen, f.Ts);
      if (clip is null)
      {
        dropped++;
        continue;
      }
      if (!byClip.TryGetValue(clip.ClipUid, out var list))
        byClip[clip.ClipUid] = list = [];
      list.Add(f);
    }

    if (dropped > 0)
    {
      // One entry per batch, never per frame: this is the only way to answer "why are there no
      // boxes in the archive for this camera", and it must not become the loudest thing in the log.
      activityLog.Record(new FsActivityEntry(
        Guid.Empty, "", req.CameraId, streamName, false, dropped, DateTime.UtcNow,
        "metadata-dropped", null));
    }

    if (byClip.Count == 0)
      return;

    foreach (var (clipUid, list) in byClip)
    {
      var chunkFrom = list[0].Ts;
      var payload = list
        .Select(f => new ChunkFrame(
          (int)Math.Round((f.Ts - chunkFrom).TotalMilliseconds),
          f.Frame.Boxes.Select(b => new ChunkBox(
            b.TrackId, b.Kind, b.Confidence, b.X, b.Y, b.Width, b.Height,
            b.HasLabel ? b.Label : null,
            b.HasPlateText ? b.PlateText : null)).ToList()))
        .ToList();

      db.MetadataChunks.Add(new DbClipMetadataChunk
      {
        ClipUid = clipUid,
        CameraId = req.CameraId,
        StreamName = streamName,
        Source = req.Source,
        FromTs = chunkFrom,
        ToTs = list[^1].Ts,
        Samples = JsonSerializer.Serialize(payload, JsonOpts),
      });
    }

    try
    {
      await db.SaveChangesAsync(ct);
    }
    catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23503" })
    {
      // The clip was deleted between resolving it and inserting. That is a normal outcome, not a
      // failure to retry: the video these boxes describe is gone, so the boxes have nothing left to
      // sit on. Retrying would only re-race the same cleaner.
      logger.LogDebug("Metadata chunk lost its clip mid-insert (camera {CameraId}) — dropped", req.CameraId);
    }
  }

  private static CoveringClip? Resolve(List<CoveringClip> clips, CoveringClip? newestOpen, DateTime ts)
  {
    CoveringClip? best = null;
    foreach (var c in clips)
    {
      if (ts < c.StartTs)
        continue;
      if (c.EndTs is null)
      {
        if (!ReferenceEquals(c, newestOpen) || ts > c.StartTs + MaxOpenClipSpan)
          continue;
      }
      else if (ts > c.EndTs.Value)
      {
        continue;
      }
      // Clips are ordered by start_ts, so a later match is the tighter one on the rotation overlap
      // where a closing and an opening ingest briefly describe the same instant.
      best = c;
    }
    if (best is not null)
      return best;

    // Rotation gap: pull the frame onto whichever boundary is close enough to be the same recording.
    CoveringClip? nearest = null;
    var nearestGap = BoundaryGrace;
    foreach (var c in clips)
    {
      var gap = ts < c.StartTs
        ? c.StartTs - ts
        : c.EndTs is not null && ts > c.EndTs.Value ? ts - c.EndTs.Value : TimeSpan.Zero;
      if (gap > TimeSpan.Zero && gap <= nearestGap)
      {
        nearestGap = gap;
        nearest = c;
      }
    }
    return nearest;
  }

  public async Task<List<MetadataSourceFrames>> GetAsync(
    string cameraId,
    string? streamName,
    DateTime from,
    DateTime to,
    CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);

    var q = db.MetadataChunks.Where(m => m.CameraId == cameraId && m.FromTs <= to && m.ToTs >= from);
    if (!string.IsNullOrEmpty(streamName))
      q = q.Where(m => m.StreamName == streamName);

    var rows = await q
      .OrderBy(m => m.FromTs)
      .Select(m => new { m.Source, m.FromTs, m.Samples })
      .ToListAsync(ct);

    var bySource = new Dictionary<string, List<MetadataFrame>>();
    foreach (var row in rows)
    {
      List<ChunkFrame>? parsed;
      try
      {
        parsed = JsonSerializer.Deserialize<List<ChunkFrame>>(row.Samples, JsonOpts);
      }
      catch (JsonException ex)
      {
        logger.LogWarning(ex, "Unreadable metadata chunk for camera {CameraId} — skipped", cameraId);
        continue;
      }
      if (parsed is null)
        continue;

      if (!bySource.TryGetValue(row.Source, out var frames))
        bySource[row.Source] = frames = [];

      foreach (var frame in parsed)
      {
        var ts = row.FromTs.AddMilliseconds(frame.T);
        if (ts < from || ts > to)
          continue;
        var outFrame = new MetadataFrame { Ts = Timestamp.FromDateTime(DateTime.SpecifyKind(ts, DateTimeKind.Utc)) };
        foreach (var b in frame.B)
        {
          var box = new MetadataBox
          {
            TrackId = b.I, Kind = b.K, Confidence = b.C,
            X = b.X, Y = b.Y, Width = b.W, Height = b.H,
          };
          if (b.L is not null) box.Label = b.L;
          if (b.P is not null) box.PlateText = b.P;
          outFrame.Boxes.Add(box);
        }
        frames.Add(outFrame);
      }
    }

    return bySource
      .Select(kv =>
      {
        var sf = new MetadataSourceFrames { Source = kv.Key };
        sf.Frames.AddRange(kv.Value.OrderBy(f => f.Ts.ToDateTime()));
        return sf;
      })
      .ToList();
  }

  public async Task<(bool HasCv, bool HasOnvif)> HasAnyAsync(
    string cameraId,
    string? streamName,
    DateTime from,
    DateTime to,
    CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);

    var q = db.MetadataChunks.Where(m => m.CameraId == cameraId && m.FromTs <= to && m.ToTs >= from);
    if (!string.IsNullOrEmpty(streamName))
      q = q.Where(m => m.StreamName == streamName);

    var hasCv = await q.AnyAsync(m => m.Source == "cv", ct);
    var hasOnvif = await q.AnyAsync(m => m.Source == "onvif", ct);
    return (hasCv, hasOnvif);
  }
}
