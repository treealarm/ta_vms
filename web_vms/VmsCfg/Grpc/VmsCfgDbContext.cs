using Microsoft.EntityFrameworkCore;

namespace VmsCfg;

public sealed class VmsCfgDbContext(DbContextOptions<VmsCfgDbContext> options) : DbContext(options)
{
  public DbSet<DbCameraStream> CameraStreams { get; set; }
  public DbSet<DbRecordingProfile> RecordingProfiles { get; set; }
  public DbSet<DbOnvifAlarmFilter> OnvifAlarmFilters { get; set; }
  public DbSet<DbCameraState> CameraStates { get; set; }

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    modelBuilder.Entity<DbCameraStream>(e =>
    {
      e.ToTable("camera_stream");
      e.HasKey(x => new { x.CameraId, x.StreamName });
      e.Property(x => x.CameraId).HasColumnName("camera_id");
      e.Property(x => x.StreamName).HasColumnName("stream_name");
      e.Property(x => x.RecordingProfileId).HasColumnName("recording_profile_id");
      e.Property(x => x.OverrideProfileId).HasColumnName("override_profile_id");
      e.Property(x => x.AlarmOverrideUntil).HasColumnName("alarm_override_until");
      e.HasOne(x => x.RecordingProfile)
       .WithMany()
       .HasForeignKey(x => x.RecordingProfileId)
       .IsRequired(false);
    });

    modelBuilder.Entity<DbRecordingProfile>(e =>
    {
      e.ToTable("recording_profile");
      e.HasKey(x => x.Id);
      e.Property(x => x.Id).HasColumnName("id");
      e.Property(x => x.Mode).HasColumnName("mode");
    });

    modelBuilder.Entity<DbOnvifAlarmFilter>(e =>
    {
      e.ToTable("onvif_alarm_filter");
      e.HasKey(x => x.Id);
      e.Property(x => x.Id).HasColumnName("id");
      e.Property(x => x.CameraId).HasColumnName("camera_id");
      e.Property(x => x.FilterText).HasColumnName("filter_text");
      e.Property(x => x.TimeoutSeconds).HasColumnName("timeout_seconds");
    });

    modelBuilder.Entity<DbCameraState>(e =>
    {
      e.ToTable("camera_states");
      e.HasKey(x => x.CameraId);
      e.Property(x => x.CameraId).HasColumnName("camera_id");
      e.Property(x => x.Armed).HasColumnName("armed");
    });
  }
}

public sealed class DbCameraStream
{
  public string CameraId { get; set; } = string.Empty;
  public string StreamName { get; set; } = string.Empty;
  public Guid? RecordingProfileId { get; set; }
  public Guid? OverrideProfileId { get; set; }
  public DateTime? AlarmOverrideUntil { get; set; }
  public DbRecordingProfile? RecordingProfile { get; set; }
}

public sealed class DbRecordingProfile
{
  public Guid Id { get; set; }
  public string Mode { get; set; } = string.Empty;
}

public sealed class DbOnvifAlarmFilter
{
  public Guid Id { get; set; }
  public string CameraId { get; set; } = string.Empty;
  public string FilterText { get; set; } = string.Empty;
  public int TimeoutSeconds { get; set; }
}

public sealed class DbCameraState
{
  public string CameraId { get; set; } = string.Empty;
  public bool Armed { get; set; }
}
