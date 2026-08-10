using Microsoft.EntityFrameworkCore;

namespace VmsAnalyticsSingleton;

public sealed class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options) : DbContext(options)
{
  public DbSet<DbDetection> Detections { get; set; }

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    modelBuilder.Entity<DbDetection>(e =>
    {
      e.ToTable("detections");
      e.HasKey(x => x.Id);
      e.Property(x => x.Id).HasColumnName("id");
      e.Property(x => x.CameraId).HasColumnName("camera_id");
      e.Property(x => x.TrackId).HasColumnName("track_id");
      e.Property(x => x.Kind).HasColumnName("kind");
      e.Property(x => x.Confidence).HasColumnName("confidence");
      e.Property(x => x.DetectedAt).HasColumnName("detected_at");
      e.Property(x => x.EventId).HasColumnName("event_id");
      e.Property(x => x.EventUuid).HasColumnName("event_uuid");
      e.Property(x => x.ObjectId).HasColumnName("object_id");
      e.Property(x => x.StorageId).HasColumnName("storage_id");
      e.Property(x => x.CropPath).HasColumnName("crop_path");
      e.Property(x => x.PlateText).HasColumnName("plate_text");
      e.HasIndex(x => new { x.CameraId, x.DetectedAt });
      e.HasIndex(x => x.EventId);
      e.HasIndex(x => x.EventUuid);
      e.HasIndex(x => x.ObjectId);
    });
  }
}

public sealed class DbDetection
{
  public long Id { get; set; }
  public string CameraId { get; set; } = string.Empty;
  public long TrackId { get; set; }
  public string Kind { get; set; } = string.Empty;
  public float Confidence { get; set; }
  public DateTime DetectedAt { get; set; }
  /// <summary>retention_events.event_id (ta_fs) — the group this detection's episode belongs to.</summary>
  public string? EventId { get; set; }
  /// <summary>retention_events.event_uuid (ta_fs) — the specific episode this detection triggered;
  /// the gallery link. Returned by RetainEvent (VmsFs owns episode identity).</summary>
  public Guid? EventUuid { get; set; }
  /// <summary>Re-identification label of the physical object (cross-camera); null when not resolved.</summary>
  public string? ObjectId { get; set; }
  /// <summary>Storage volume the crop was written to; null = no crop saved.</summary>
  public string? StorageId { get; set; }
  /// <summary>Path relative to the storage root, like clips' file_path.</summary>
  public string? CropPath { get; set; }
  public string? PlateText { get; set; }
}
