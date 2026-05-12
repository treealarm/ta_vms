using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace VmsFs;

[Table("clips")]
public sealed class DbClip
{
  [Key]
  [Column("clip_uid")]
  public Guid ClipUid { get; set; }

  [Column("file_path")]
  public string FilePath { get; set; } = "";

  [Column("camera_id")]
  public string CameraId { get; set; } = "";

  [Column("stream_name")]
  public string StreamName { get; set; } = "";

  [Column("storage_id")]
  public string StorageId { get; set; } = "";

  [Column("start_ts")]
  public DateTime StartTs { get; set; }

  [Column("end_ts")]
  public DateTime? EndTs { get; set; }

  [Column("size_bytes")]
  public long SizeBytes { get; set; }

  [Column("retained")]
  public bool Retained { get; set; } = true;
}
