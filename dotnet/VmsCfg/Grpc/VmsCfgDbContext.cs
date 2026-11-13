using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace VmsCfg;

public sealed class VmsCfgDbContext(DbContextOptions<VmsCfgDbContext> options) : DbContext(options)
{
  // Camera and storage ids are UUID columns; the gRPC contract and this service's own code carry
  // them as strings (a UUID formatted as text). The conversion lives here, at the one boundary
  // where the database type matters, so a malformed id fails on the way in rather than being
  // compared as text against a uuid column.
  private static readonly ValueConverter<string, Guid> UuidText =
    new(id => Guid.Parse(id), id => id.ToString());

  private static readonly ValueConverter<string?, Guid?> NullableUuidText =
    new(id => string.IsNullOrEmpty(id) ? null : Guid.Parse(id),
        id => id == null ? null : id.Value.ToString());

  public DbSet<DbCameraStream> CameraStreams { get; set; }
  public DbSet<DbRecordingProfile> RecordingProfiles { get; set; }
  public DbSet<DbOnvifAlarmFilter> OnvifAlarmFilters { get; set; }
  public DbSet<DbCameraState> CameraStates { get; set; }
  public DbSet<DbDeviceSettings> DeviceSettings { get; set; }
  public DbSet<DbAlarmDefinition> AlarmDefinitions { get; set; }
  public DbSet<DbStorage> Storages { get; set; }
  public DbSet<DbAnalyticsWatch> AnalyticsWatches { get; set; }
  public DbSet<DbPtzCalibration> PtzCalibrations { get; set; }
  public DbSet<DbPtzTrackingPolicy> PtzTrackingPolicies { get; set; }
  public DbSet<DbPtzSlaveBinding> PtzSlaveBindings { get; set; }
  public DbSet<DbPtzSlaveBindingPoint> PtzSlaveBindingPoints { get; set; }
  public DbSet<DbRecordingSchedule> RecordingSchedules { get; set; }
  public DbSet<DbRecordingScheduleWindow> RecordingScheduleWindows { get; set; }
  public DbSet<DbCameraSchedule> CameraSchedules { get; set; }
  public DbSet<DbCameraSetup> CameraSetups { get; set; }
  public DbSet<DbOnvifEventSubscription> OnvifEventSubscriptions { get; set; }
  public DbSet<DbMediaServerNode> MediaServerNodes { get; set; }
  public DbSet<DbDeployment> Deployments { get; set; }
  public DbSet<DbEventSubscription> EventSubscriptions { get; set; }
  public DbSet<DbSubscriptionAction> SubscriptionActions { get; set; }

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    modelBuilder.Entity<DbCameraStream>(e =>
    {
      e.ToTable("camera_stream");
      e.HasKey(x => new { x.CameraId, x.StreamName });
      e.Property(x => x.CameraId).HasColumnName("camera_id").HasConversion(UuidText);
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
      e.Property(x => x.CameraId).HasColumnName("camera_id").HasConversion(UuidText);
      e.Property(x => x.FilterText).HasColumnName("filter_text");
      e.Property(x => x.TimeoutSeconds).HasColumnName("timeout_seconds");
      e.Property(x => x.TextFilter).HasColumnName("text_filter");
    });

    modelBuilder.Entity<DbCameraState>(e =>
    {
      e.ToTable("camera_states");
      e.HasKey(x => x.CameraId);
      e.Property(x => x.CameraId).HasColumnName("camera_id").HasConversion(UuidText);
      e.Property(x => x.Armed).HasColumnName("armed");
      e.Property(x => x.ArmMode).HasColumnName("arm_mode");
    });

    modelBuilder.Entity<DbDeviceSettings>(e =>
    {
      e.ToTable("device_settings");
      e.HasKey(x => new { x.CameraId, x.Kind, x.Token });
      e.Property(x => x.CameraId).HasColumnName("camera_id").HasConversion(UuidText);
      e.Property(x => x.Kind).HasColumnName("kind");
      e.Property(x => x.Token).HasColumnName("token");
      e.Property(x => x.Name).HasColumnName("name");
      e.Property(x => x.ArmMode).HasColumnName("arm_mode");
      e.Property(x => x.TriggerOn).HasColumnName("trigger_on");
      e.Property(x => x.AlarmEnabled).HasColumnName("alarm_enabled");
    });

    modelBuilder.Entity<DbAlarmDefinition>(e =>
    {
      e.ToTable("alarm_definitions");
      e.HasKey(x => x.Id);
      e.Property(x => x.Id).HasColumnName("id");
      e.Property(x => x.Name).HasColumnName("name");
      e.Property(x => x.Severity).HasColumnName("severity");
      e.Property(x => x.AckPolicy).HasColumnName("ack_policy");
    });

    modelBuilder.Entity<DbStorage>(e =>
    {
      e.ToTable("storage");
      e.HasKey(x => x.StorageId);
      e.Property(x => x.StorageId).HasColumnName("storage_id").HasConversion(UuidText);
      e.Property(x => x.RootPath).HasColumnName("root_path");
      e.Property(x => x.ReservedBytes).HasColumnName("reserved_bytes");
    });

    modelBuilder.Entity<DbAnalyticsWatch>(e =>
    {
      e.ToTable("analytics_watch");
      e.HasKey(x => x.CameraId);
      e.Property(x => x.CameraId).HasColumnName("camera_id").HasConversion(UuidText);
      e.Property(x => x.StreamName).HasColumnName("stream_name");
      e.Property(x => x.Enabled).HasColumnName("enabled");
      e.Property(x => x.Classes).HasColumnName("classes");
      e.Property(x => x.MinConfidence).HasColumnName("min_confidence");
      e.Property(x => x.SampleFps).HasColumnName("sample_fps");
      e.Property(x => x.AttachDebugCrops).HasColumnName("attach_debug_crops");
      e.Property(x => x.TimeoutSeconds).HasColumnName("timeout_seconds");
      e.Property(x => x.StoreEmbeddings).HasColumnName("store_embeddings");
    });

    modelBuilder.Entity<DbPtzCalibration>(e =>
    {
      e.ToTable("ptz_calibration");
      e.HasKey(x => x.CameraId);
      e.Property(x => x.CameraId).HasColumnName("camera_id").HasConversion(UuidText);
      e.Property(x => x.PanDx).HasColumnName("pan_dx");
      e.Property(x => x.PanDy).HasColumnName("pan_dy");
      e.Property(x => x.TiltDx).HasColumnName("tilt_dx");
      e.Property(x => x.TiltDy).HasColumnName("tilt_dy");
      e.Property(x => x.Confidence).HasColumnName("confidence");
      e.Property(x => x.CalibratedAt).HasColumnName("calibrated_at");
      e.Property(x => x.Method).HasColumnName("method");
    });

    modelBuilder.Entity<DbPtzTrackingPolicy>(e =>
    {
      e.ToTable("ptz_tracking_policy");
      e.HasKey(x => x.CameraId);
      e.Property(x => x.CameraId).HasColumnName("camera_id").HasConversion(UuidText);
      e.Property(x => x.Enabled).HasColumnName("enabled");
      e.Property(x => x.Kinds).HasColumnName("kinds");
      e.Property(x => x.Sources).HasColumnName("sources");
      e.Property(x => x.MasterCameraId).HasColumnName("master_camera_id").HasConversion(NullableUuidText);
    });

    modelBuilder.Entity<DbPtzSlaveBinding>(e =>
    {
      e.ToTable("ptz_slave_binding");
      e.HasKey(x => x.SlaveCameraId);
      e.Property(x => x.SlaveCameraId).HasColumnName("slave_camera_id").HasConversion(UuidText);
      e.Property(x => x.MasterCameraId).HasColumnName("master_camera_id").HasConversion(UuidText);
      e.Property(x => x.Method).HasColumnName("method");
      e.Property(x => x.CalibratedAt).HasColumnName("calibrated_at");
      e.HasMany(x => x.Points)
       .WithOne()
       .HasForeignKey(x => x.SlaveCameraId);
    });

    modelBuilder.Entity<DbPtzSlaveBindingPoint>(e =>
    {
      e.ToTable("ptz_slave_binding_point");
      e.HasKey(x => new { x.SlaveCameraId, x.Ordinal });
      e.Property(x => x.SlaveCameraId).HasColumnName("slave_camera_id").HasConversion(UuidText);
      e.Property(x => x.Ordinal).HasColumnName("ordinal");
      e.Property(x => x.MasterX).HasColumnName("master_x");
      e.Property(x => x.MasterY).HasColumnName("master_y");
      e.Property(x => x.Pan).HasColumnName("pan");
      e.Property(x => x.Tilt).HasColumnName("tilt");
      e.Property(x => x.PresetToken).HasColumnName("preset_token");
      e.Property(x => x.PresetName).HasColumnName("preset_name");
    });

    modelBuilder.Entity<DbRecordingSchedule>(e =>
    {
      e.ToTable("recording_schedule");
      e.HasKey(x => x.Id);
      e.Property(x => x.Id).HasColumnName("id");
      e.Property(x => x.Name).HasColumnName("name");
      e.Property(x => x.Timezone).HasColumnName("timezone");
      e.Property(x => x.LeadInSeconds).HasColumnName("lead_in_seconds");
      e.HasMany(x => x.Windows)
       .WithOne()
       .HasForeignKey(x => x.ScheduleId);
    });

    modelBuilder.Entity<DbRecordingScheduleWindow>(e =>
    {
      e.ToTable("recording_schedule_window");
      e.HasKey(x => x.Id);
      e.Property(x => x.Id).HasColumnName("id");
      e.Property(x => x.ScheduleId).HasColumnName("schedule_id");
      e.Property(x => x.Kind).HasColumnName("kind");
      e.Property(x => x.DayOfWeek).HasColumnName("day_of_week");
      e.Property(x => x.DateValue).HasColumnName("date_value");
      e.Property(x => x.StartSecond).HasColumnName("start_second");
      e.Property(x => x.EndSecond).HasColumnName("end_second");
    });

    modelBuilder.Entity<DbCameraSchedule>(e =>
    {
      e.ToTable("camera_schedule");
      e.HasKey(x => new { x.CameraId, x.ScheduleId });
      e.Property(x => x.CameraId).HasColumnName("camera_id").HasConversion(UuidText);
      e.Property(x => x.ScheduleId).HasColumnName("schedule_id");
    });

    // Read-only projection of camera_setup: the schedule ticker needs each camera's media server
    // to notify. The rest of camera_setup is still handled by raw SQL in CameraStorage.
    modelBuilder.Entity<DbCameraSetup>(e =>
    {
      e.ToTable("camera_setup");
      e.HasKey(x => x.CameraId);
      e.Property(x => x.CameraId).HasColumnName("camera_id").HasConversion(UuidText);
      e.Property(x => x.ServerId).HasColumnName("server_id");
      e.Property(x => x.OnvifMetadataDisabled).HasColumnName("onvif_metadata_disabled");
    });

    modelBuilder.Entity<DbOnvifEventSubscription>(e =>
    {
      e.ToTable("onvif_event_subscription");
      e.HasKey(x => x.CameraId);
      e.Property(x => x.CameraId).HasColumnName("camera_id").HasConversion(UuidText);
      e.Property(x => x.Subscribed).HasColumnName("subscribed");
      e.Property(x => x.PollTimeoutSeconds).HasColumnName("poll_timeout_seconds");
      e.Property(x => x.MessageLimit).HasColumnName("message_limit");
    });

    modelBuilder.Entity<DbMediaServerNode>(e =>
    {
      e.ToTable("media_server_node");
      e.HasKey(x => x.ServerId);
      e.Property(x => x.ServerId).HasColumnName("server_id");
      e.Property(x => x.FsId).HasColumnName("fs_id");
      e.Property(x => x.OnvifId).HasColumnName("onvif_id");
      e.Property(x => x.LoggerId).HasColumnName("logger_id");
      e.Property(x => x.RoiId).HasColumnName("roi_id");
      e.Property(x => x.Name).HasColumnName("name");
      e.Property(x => x.Address).HasColumnName("address");
      e.Property(x => x.MediaMtx).HasColumnName("media_mtx");
      e.Property(x => x.Configured).HasColumnName("configured");
      e.Property(x => x.EnrolledAt).HasColumnName("enrolled_at");
    });

    modelBuilder.Entity<DbDeployment>(e =>
    {
      e.ToTable("deployment");
      e.HasKey(x => x.Id);
      e.Property(x => x.Id).HasColumnName("id");
      e.Property(x => x.EnrollmentOpen).HasColumnName("enrollment_open");
    });

    modelBuilder.Entity<DbEventSubscription>(e =>
    {
      e.ToTable("event_subscriptions");
      e.HasKey(x => x.Id);
      e.Property(x => x.Id).HasColumnName("id");
      e.Property(x => x.Name).HasColumnName("name");
      e.Property(x => x.Enabled).HasColumnName("enabled");
      e.Property(x => x.EventName).HasColumnName("event_name");
      e.Property(x => x.SourceFilter).HasColumnName("source_filter").HasColumnType("jsonb");
      e.Property(x => x.ArmedCondition).HasColumnName("armed_condition");
      e.Property(x => x.CooldownMs).HasColumnName("cooldown_ms");
      e.Property(x => x.Version).HasColumnName("version");
      e.HasMany(x => x.Actions)
       .WithOne()
       .HasForeignKey(x => x.SubscriptionId)
       .OnDelete(DeleteBehavior.Cascade);
    });

    modelBuilder.Entity<DbSubscriptionAction>(e =>
    {
      e.ToTable("subscription_actions");
      e.HasKey(x => new { x.SubscriptionId, x.Position });
      e.Property(x => x.SubscriptionId).HasColumnName("subscription_id");
      e.Property(x => x.Position).HasColumnName("position");
      e.Property(x => x.Kind).HasColumnName("kind");
      e.Property(x => x.Params).HasColumnName("params").HasColumnType("jsonb");
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
  public string TextFilter { get; set; } = string.Empty;
}

public sealed class DbCameraState
{
  public string CameraId { get; set; } = string.Empty;
  public bool Armed { get; set; }
  /// <summary>The administrator's intent: `inherit` leaves the flag to the map's derivation.</summary>
  public string ArmMode { get; set; } = DeviceSettingsRules.Inherit;
}

public sealed class DbDeviceSettings
{
  public string CameraId { get; set; } = string.Empty;
  public string Kind { get; set; } = string.Empty;
  public string Token { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public string ArmMode { get; set; } = DeviceSettingsRules.Inherit;
  public string? TriggerOn { get; set; }
  public bool AlarmEnabled { get; set; }
}

public sealed class DbAlarmDefinition
{
  public Guid Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public string Severity { get; set; } = "warning";
  public string AckPolicy { get; set; } = "required";
}

public sealed class DbStorage
{
  public string StorageId { get; set; } = string.Empty;
  public string RootPath { get; set; } = string.Empty;
  public long ReservedBytes { get; set; }
}

public sealed class DbAnalyticsWatch
{
  public string CameraId { get; set; } = string.Empty;
  public string StreamName { get; set; } = string.Empty;
  public bool Enabled { get; set; }
  public string Classes { get; set; } = "person";
  public float MinConfidence { get; set; } = 0.5f;
  public int SampleFps { get; set; } = 1;
  public bool AttachDebugCrops { get; set; }
  /// Seconds of recording kept after the last detection before the event closes.
  public int TimeoutSeconds { get; set; } = 10;
  /// Persist this camera's detection embeddings for forensic search.
  public bool StoreEmbeddings { get; set; }
}

public sealed class DbPtzCalibration
{
  public string CameraId { get; set; } = string.Empty;
  public float PanDx { get; set; }
  public float PanDy { get; set; }
  public float TiltDx { get; set; }
  public float TiltDy { get; set; }
  public float Confidence { get; set; }
  public DateTime CalibratedAt { get; set; } = DateTime.UtcNow;
  public string Method { get; set; } = "auto";
}

/// <summary>Whether a camera follows objects on its own, and which ones.</summary>
/// <remarks>
/// Both lists are stored as comma-separated text rather than as Postgres arrays, following
/// <see cref="DbAnalyticsWatch.Classes"/>: they are short, they are read whole or not at all, and
/// nothing queries inside them.
/// </remarks>
public sealed class DbPtzTrackingPolicy
{
  public string CameraId { get; set; } = string.Empty;
  public bool Enabled { get; set; }
  /// Priority order; empty means any object will do.
  public string Kinds { get; set; } = string.Empty;
  public string Sources { get; set; } = "cv,onvif";
  /// Whose detections to follow. Null means this camera's own.
  public string? MasterCameraId { get; set; }
}

/// <summary>The hand-marked survey that lets a slave be aimed at a master's objects.</summary>
public sealed class DbPtzSlaveBinding
{
  public string SlaveCameraId { get; set; } = string.Empty;
  public string MasterCameraId { get; set; } = string.Empty;
  /// "absolute" | "presets" | "geo".
  public string Method { get; set; } = "absolute";
  public DateTime CalibratedAt { get; set; } = DateTime.UtcNow;
  public List<DbPtzSlaveBindingPoint> Points { get; set; } = [];
}

/// <summary>One marked correspondence between a spot in the master's frame and the slave's aim.</summary>
public sealed class DbPtzSlaveBindingPoint
{
  public string SlaveCameraId { get; set; } = string.Empty;
  /// Position in the survey. Carries no meaning beyond keeping the operator's list order stable.
  public int Ordinal { get; set; }
  public float MasterX { get; set; }
  public float MasterY { get; set; }
  /// Null when the camera would not tell us where it was pointing.
  public float? Pan { get; set; }
  public float? Tilt { get; set; }
  public string? PresetToken { get; set; }
  public string? PresetName { get; set; }
}

public sealed class DbRecordingSchedule
{
  public Guid Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public string Timezone { get; set; } = "UTC";
  public int LeadInSeconds { get; set; } = 5;
  public List<DbRecordingScheduleWindow> Windows { get; set; } = [];
}

public sealed class DbRecordingScheduleWindow
{
  public Guid Id { get; set; }
  public Guid ScheduleId { get; set; }
  /// "weekly" | "date".
  public string Kind { get; set; } = "weekly";
  /// 0=Sunday..6=Saturday for "weekly", null for "date".
  public short? DayOfWeek { get; set; }
  /// Set for "date" windows only.
  public DateOnly? DateValue { get; set; }
  /// Seconds from local midnight (0..86400). EndSecond is exclusive; EndSecond <= StartSecond
  /// wraps past midnight.
  public int StartSecond { get; set; }
  public int EndSecond { get; set; }
}

public sealed class DbCameraSchedule
{
  public string CameraId { get; set; } = string.Empty;
  public Guid ScheduleId { get; set; }
}

public sealed class DbCameraSetup
{
  public string CameraId { get; set; } = string.Empty;
  public string ServerId { get; set; } = string.Empty;
  /// Drop the object boxes this camera puts in its own RTSP metadata track, before they are parsed.
  public bool OnvifMetadataDisabled { get; set; }
}

/// Whether VmsOnvif keeps a pull-point subscription open for this camera, and how it polls.
/// A camera with no row here is not managed for ONVIF events at all.
public sealed class DbOnvifEventSubscription
{
  public string CameraId { get; set; } = string.Empty;
  public bool Subscribed { get; set; }
  public int PollTimeoutSeconds { get; set; }
  public int MessageLimit { get; set; }
}

public sealed class DbMediaServerNode
{
  public string ServerId { get; set; } = string.Empty;
  public string FsId { get; set; } = string.Empty;
  public string OnvifId { get; set; } = string.Empty;
  public string LoggerId { get; set; } = string.Empty;
  public string RoiId { get; set; } = string.Empty;
  public string Name { get; set; } = string.Empty;
  public string Address { get; set; } = string.Empty;
  public bool MediaMtx { get; set; }
  public bool Configured { get; set; }
  public DateTime EnrolledAt { get; set; }
}

public sealed class DbDeployment
{
  public int Id { get; set; }
  public bool EnrollmentOpen { get; set; }
}

/// <summary>One configured reaction to the journal; its actions are rows of
/// <see cref="DbSubscriptionAction"/>.</summary>
public sealed class DbEventSubscription
{
  public Guid Id { get; set; }
  public string Name { get; set; } = string.Empty;
  public bool Enabled { get; set; } = true;
  public string EventName { get; set; } = string.Empty;
  /// JSON selector; see the table's comment in the baseline migration.
  public string SourceFilter { get; set; } = "{}";
  public string ArmedCondition { get; set; } = EventSubscriptionRules.Any;
  public int CooldownMs { get; set; }
  public int Version { get; set; } = 1;
  public List<DbSubscriptionAction> Actions { get; set; } = [];
}

/// <summary>One action, in run order. <see cref="Params"/> is per-kind JSON, validated on save.</summary>
public sealed class DbSubscriptionAction
{
  public Guid SubscriptionId { get; set; }
  public int Position { get; set; }
  public string Kind { get; set; } = string.Empty;
  public string Params { get; set; } = "{}";
}
