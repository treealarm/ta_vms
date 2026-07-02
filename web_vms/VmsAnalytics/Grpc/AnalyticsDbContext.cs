using Microsoft.EntityFrameworkCore;

namespace VmsAnalytics;

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
      e.Property(x => x.CropRef).HasColumnName("crop_ref");
      e.Property(x => x.PlateText).HasColumnName("plate_text");
      e.HasIndex(x => new { x.CameraId, x.DetectedAt });
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
  public string? CropRef { get; set; }
  public string? PlateText { get; set; }
}
