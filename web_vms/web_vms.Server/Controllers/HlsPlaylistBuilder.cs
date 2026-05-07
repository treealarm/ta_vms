using System.Text;

namespace web_vms;

public sealed class HlsPlaylistBuilder
{
  private const string Lf = "\n";
  private readonly StringBuilder _sb = new();

  public void AppendHeader(int targetDuration, double? startOffset = null)
  {
    _sb.Append("#EXTM3U").Append(Lf);
    _sb.Append("#EXT-X-VERSION:6").Append(Lf);
    _sb.Append($"#EXT-X-TARGETDURATION:{targetDuration}").Append(Lf);
    _sb.Append("#EXT-X-MEDIA-SEQUENCE:0").Append(Lf);
    if (startOffset.HasValue)
      _sb.Append($"#EXT-X-START:TIME-OFFSET={startOffset.Value:F3}").Append(Lf);
  }

  public void AppendDiscontinuity() => _sb.Append("#EXT-X-DISCONTINUITY").Append(Lf);

  public void AppendSegment(string uri, double durSec)
  {
    _sb.Append($"#EXTINF:{durSec:F3},").Append(Lf);
    _sb.Append(uri).Append(Lf);
  }

  public void AppendEndList() => _sb.Append("#EXT-X-ENDLIST").Append(Lf);

  public string Build() => _sb.ToString();
}
