using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VmsFs;

/// <summary>
/// One flush batch of analytics geometry (~1s of frames) belonging to one clip — see the 0007
/// migration for the payload shape and the reasoning behind the row granularity.
/// </summary>
/// <remarks>
/// There is deliberately no navigation property from <see cref="DbClip"/> to this type: the
/// lifetime link is the database's ON DELETE CASCADE, and an EF navigation would only tempt a
/// caller into loading a minute of geometry alongside a clip row that never needs it.
/// </remarks>
[Table("clip_metadata_chunks")]
public sealed class DbClipMetadataChunk
{
  [Key]
  [Column("chunk_id")]
  public long ChunkId { get; set; }

  [Column("clip_uid")]
  public Guid ClipUid { get; set; }

  [Column("camera_id")]
  public string CameraId { get; set; } = "";

  [Column("stream_name")]
  public string StreamName { get; set; } = "";

  /// <summary>"cv" or "onvif" — who produced the geometry. Checked by the database.</summary>
  [Column("source")]
  public string Source { get; set; } = "";

  [Column("from_ts")]
  public DateTime FromTs { get; set; }

  [Column("to_ts")]
  public DateTime ToTs { get; set; }

  /// <summary>Frames as JSON; see the 0007 migration for the shape.</summary>
  [Column("samples", TypeName = "jsonb")]
  public string Samples { get; set; } = "[]";
}
