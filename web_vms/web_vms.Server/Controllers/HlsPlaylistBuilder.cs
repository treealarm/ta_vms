using System.Text;

namespace web_vms;

public sealed class HlsPlaylistBuilder
{
  private const string Lf = "\n";
  private readonly StringBuilder _sb = new();

  public void AppendHeader(int targetDuration, double? startOffset = null)
  {
    _sb.Append("#EXTM3U").Append(Lf);
    _sb.Append("#EXT-X-VERSION:7").Append(Lf);
    _sb.Append($"#EXT-X-TARGETDURATION:{targetDuration}").Append(Lf);
    _sb.Append("#EXT-X-MEDIA-SEQUENCE:0").Append(Lf);
    if (startOffset.HasValue)
      _sb.Append($"#EXT-X-START:TIME-OFFSET={startOffset.Value:F3},PRECISE=YES").Append(Lf);
  }

  public void AppendDiscontinuity() => _sb.Append("#EXT-X-DISCONTINUITY").Append(Lf);

  public void AppendMap(string uri)
    => _sb.Append($"#EXT-X-MAP:URI=\"{uri}\"").Append(Lf);

  // Wall-clock anchor for the segment that follows. Playback across a multi-clip range cannot
  // derive wall time from "range start + currentTime": gaps between clips accumulate into minutes
  // of drift, which desynchronizes cameras sharing one playhead. PDT is the only mapping the
  // client is allowed to use.
  public void AppendProgramDateTime(DateTime wallStart)
  {
    var utc = wallStart.Kind == DateTimeKind.Utc ? wallStart : wallStart.ToUniversalTime();
    _sb.Append($"#EXT-X-PROGRAM-DATE-TIME:{utc:yyyy-MM-ddTHH:mm:ss.fff}Z").Append(Lf);
  }

  // Segments actually written. Callers check this after appending everything: emitting a
  // structurally valid but segment-less playlist would make the player stall silently.
  public int SegmentCount { get; private set; }

  public void AppendSegment(string uri, double durSec)
  {
    _sb.Append($"#EXTINF:{durSec:F3},").Append(Lf);
    _sb.Append(uri).Append(Lf);
    SegmentCount++;
  }

  public void AppendEndList() => _sb.Append("#EXT-X-ENDLIST").Append(Lf);

  public string Build() => _sb.ToString();
}
