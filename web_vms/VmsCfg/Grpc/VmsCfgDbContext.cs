using Microsoft.EntityFrameworkCore;

namespace VmsCfg;

public sealed class VmsCfgDbContext(DbContextOptions<VmsCfgDbContext> options) : DbContext(options)
{
  public DbSet<DbCameraStream> CameraStreams { get; set; }
  public DbSet<DbRecordingProfile> RecordingProfiles { get; set; }
  public DbSet<DbOnvifAlarmFilter> OnvifAlarmFilters { get; set; }
  public DbSet<DbCameraState> CameraStates { get; set; }
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
  public DbSet<DbUserCameraPermission> UserCameraPermissions { get; set; }
  public DbSet<DbUserAllCamerasPermission> UserAllCamerasPermissions { get; set; }
  public DbSet<DbCameraAclSetting> CameraAclSettings { get; set; }
  public DbSet<DbOnvifEventSubscription> OnvifEventSubscriptions { get; set; }
  public DbSet<DbSpace> Spaces { get; set; }
  public DbSet<DbSpacePlan> SpacePlans { get; set; }
  public DbSet<DbCameraPlacement> CameraPlacements { get; set; }
  public DbSet<DbLandmark> Landmarks { get; set; }
  public DbSet<DbPtzGeoObservation> PtzGeoObservations { get; set; }
  public DbSet<DbGroundObservation> GroundObservations { get; set; }

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
      e.Property(x => x.TextFilter).HasColumnName("text_filter");
    });

    modelBuilder.Entity<DbCameraState>(e =>
    {
      e.ToTable("camera_states");
      e.HasKey(x => x.CameraId);
      e.Property(x => x.CameraId).HasColumnName("camera_id");
      e.Property(x => x.Armed).HasColumnName("armed");
    });

    modelBuilder.Entity<DbStorage>(e =>
    {
      e.ToTable("storage");
      e.HasKey(x => x.StorageId);
      e.Property(x => x.StorageId).HasColumnName("storage_id");
      e.Property(x => x.RootPath).HasColumnName("root_path");
      e.Property(x => x.ReservedBytes).HasColumnName("reserved_bytes");
    });

    modelBuilder.Entity<DbAnalyticsWatch>(e =>
    {
      e.ToTable("analytics_watch");
      e.HasKey(x => x.CameraId);
      e.Property(x => x.CameraId).HasColumnName("camera_id");
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
      e.Property(x => x.CameraId).HasColumnName("camera_id");
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
      e.Property(x => x.CameraId).HasColumnName("camera_id");
      e.Property(x => x.Enabled).HasColumnName("enabled");
      e.Property(x => x.Kinds).HasColumnName("kinds");
      e.Property(x => x.Sources).HasColumnName("sources");
      e.Property(x => x.MasterCameraId).HasColumnName("master_camera_id");
    });

    modelBuilder.Entity<DbPtzSlaveBinding>(e =>
    {
      e.ToTable("ptz_slave_binding");
      e.HasKey(x => x.SlaveCameraId);
      e.Property(x => x.SlaveCameraId).HasColumnName("slave_camera_id");
      e.Property(x => x.MasterCameraId).HasColumnName("master_camera_id");
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
      e.Property(x => x.SlaveCameraId).HasColumnName("slave_camera_id");
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
      e.Property(x => x.CameraId).HasColumnName("camera_id");
      e.Property(x => x.ScheduleId).HasColumnName("schedule_id");
    });

    // Read-only projection of camera_setup: the schedule ticker needs each camera's media server
    // to notify. The rest of camera_setup is still handled by raw SQL in CameraStorage.
    modelBuilder.Entity<DbCameraSetup>(e =>
    {
      e.ToTable("camera_setup");
      e.HasKey(x => x.CameraId);
      e.Property(x => x.CameraId).HasColumnName("camera_id");
      e.Property(x => x.ServerId).HasColumnName("server_id");
      e.Property(x => x.OnvifMetadataDisabled).HasColumnName("onvif_metadata_disabled");
    });

    modelBuilder.Entity<DbOnvifEventSubscription>(e =>
    {
      e.ToTable("onvif_event_subscription");
      e.HasKey(x => x.CameraId);
      e.Property(x => x.CameraId).HasColumnName("camera_id");
      e.Property(x => x.Subscribed).HasColumnName("subscribed");
      e.Property(x => x.PollTimeoutSeconds).HasColumnName("poll_timeout_seconds");
      e.Property(x => x.MessageLimit).HasColumnName("message_limit");
    });

    modelBuilder.Entity<DbUserCameraPermission>(e =>
    {
      e.ToTable("user_camera_permission");
      e.HasKey(x => new { x.Username, x.CameraId });
      e.Property(x => x.Username).HasColumnName("username");
      e.Property(x => x.CameraId).HasColumnName("camera_id");
      e.Property(x => x.ViewLive).HasColumnName("view_live");
      e.Property(x => x.ViewArchive).HasColumnName("view_archive");
      e.Property(x => x.Ptz).HasColumnName("ptz");
      e.Property(x => x.Control).HasColumnName("control");
      e.Property(x => x.Export).HasColumnName("export");
    });

    modelBuilder.Entity<DbUserAllCamerasPermission>(e =>
    {
      e.ToTable("user_all_cameras_permission");
      e.HasKey(x => x.Username);
      e.Property(x => x.Username).HasColumnName("username");
      e.Property(x => x.ViewLive).HasColumnName("view_live");
      e.Property(x => x.ViewArchive).HasColumnName("view_archive");
      e.Property(x => x.Ptz).HasColumnName("ptz");
      e.Property(x => x.Control).HasColumnName("control");
      e.Property(x => x.Export).HasColumnName("export");
    });

    modelBuilder.Entity<DbCameraAclSetting>(e =>
    {
      e.ToTable("camera_acl_setting");
      e.HasKey(x => x.Id);
      e.Property(x => x.Id).HasColumnName("id");
      e.Property(x => x.Mode).HasColumnName("mode");
    });

    modelBuilder.Entity<DbSpace>(e =>
    {
      e.ToTable("space");
      e.HasKey(x => x.Id);
      e.Property(x => x.Id).HasColumnName("id");
      e.Property(x => x.ParentId).HasColumnName("parent_id");
      e.Property(x => x.Name).HasColumnName("name");
      e.Property(x => x.Kind).HasColumnName("kind");
      e.Property(x => x.RealCoordinates).HasColumnName("real_coordinates");
      e.Property(x => x.Ordinal).HasColumnName("ordinal");
      e.Property(x => x.ElevationM).HasColumnName("elevation_m");
      e.Property(x => x.ViewSouth).HasColumnName("view_south");
      e.Property(x => x.ViewWest).HasColumnName("view_west");
      e.Property(x => x.ViewNorth).HasColumnName("view_north");
      e.Property(x => x.ViewEast).HasColumnName("view_east");
      e.Property(x => x.Footprint).HasColumnName("footprint").HasColumnType("jsonb");
    });

    modelBuilder.Entity<DbSpacePlan>(e =>
    {
      e.ToTable("space_plan");
      e.HasKey(x => x.SpaceId);
      e.Property(x => x.SpaceId).HasColumnName("space_id");
      e.Property(x => x.Mime).HasColumnName("mime");
      e.Property(x => x.WidthPx).HasColumnName("width_px");
      e.Property(x => x.HeightPx).HasColumnName("height_px");
      e.Property(x => x.TlLat).HasColumnName("tl_lat");
      e.Property(x => x.TlLon).HasColumnName("tl_lon");
      e.Property(x => x.TrLat).HasColumnName("tr_lat");
      e.Property(x => x.TrLon).HasColumnName("tr_lon");
      e.Property(x => x.BlLat).HasColumnName("bl_lat");
      e.Property(x => x.BlLon).HasColumnName("bl_lon");
      e.Property(x => x.Etag).HasColumnName("etag");
      e.Property(x => x.Image).HasColumnName("image");
    });

    modelBuilder.Entity<DbCameraPlacement>(e =>
    {
      e.ToTable("camera_placement");
      e.HasKey(x => new { x.CameraId, x.SpaceId });
      e.Property(x => x.CameraId).HasColumnName("camera_id");
      e.Property(x => x.SpaceId).HasColumnName("space_id");
      e.Property(x => x.IsReference).HasColumnName("is_reference");
      e.Property(x => x.Lat).HasColumnName("lat");
      e.Property(x => x.Lon).HasColumnName("lon");
      e.Property(x => x.HeightM).HasColumnName("height_m");
      e.Property(x => x.HeadingDeg).HasColumnName("heading_deg");
      e.Property(x => x.HfovDeg).HasColumnName("hfov_deg");
      e.Property(x => x.RangeM).HasColumnName("range_m");
    });

    modelBuilder.Entity<DbLandmark>(e =>
    {
      e.ToTable("landmark");
      e.HasKey(x => x.Id);
      e.Property(x => x.Id).HasColumnName("id");
      e.Property(x => x.SpaceId).HasColumnName("space_id");
      e.Property(x => x.Name).HasColumnName("name");
      e.Property(x => x.Lat).HasColumnName("lat");
      e.Property(x => x.Lon).HasColumnName("lon");
      e.Property(x => x.HeightM).HasColumnName("height_m");
    });

    modelBuilder.Entity<DbPtzGeoObservation>(e =>
    {
      e.ToTable("ptz_geo_observation");
      e.HasKey(x => new { x.CameraId, x.LandmarkId });
      e.Property(x => x.CameraId).HasColumnName("camera_id");
      e.Property(x => x.LandmarkId).HasColumnName("landmark_id");
      e.Property(x => x.Pan).HasColumnName("pan");
      e.Property(x => x.Tilt).HasColumnName("tilt");
    });

    modelBuilder.Entity<DbGroundObservation>(e =>
    {
      e.ToTable("ground_observation");
      e.HasKey(x => new { x.CameraId, x.LandmarkId });
      e.Property(x => x.CameraId).HasColumnName("camera_id");
      e.Property(x => x.LandmarkId).HasColumnName("landmark_id");
      e.Property(x => x.X).HasColumnName("x");
      e.Property(x => x.Y).HasColumnName("y");
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

/// One user's grants on one camera. Username is a Keycloak preferred_username.
public sealed class DbUserCameraPermission
{
  public string Username { get; set; } = string.Empty;
  public string CameraId { get; set; } = string.Empty;
  public bool ViewLive { get; set; }
  public bool ViewArchive { get; set; }
  public bool Ptz { get; set; }
  public bool Control { get; set; }
  public bool Export { get; set; }
}

/// Blanket grants covering every camera, present and future. OR-ed with the per-camera row.
public sealed class DbUserAllCamerasPermission
{
  public string Username { get; set; } = string.Empty;
  public bool ViewLive { get; set; }
  public bool ViewArchive { get; set; }
  public bool Ptz { get; set; }
  public bool Control { get; set; }
  public bool Export { get; set; }
}

/// Single-row table: "open" (everyone sees everything) or "enforced" (grants decide).
public sealed class DbCameraAclSetting
{
  public bool Id { get; set; } = true;
  public string Mode { get; set; } = CameraAclModes.Open;
}

public static class CameraAclModes
{
  public const string Open = "open";
  public const string Enforced = "enforced";
}

/// A space cameras are placed in. See migrations/0002_spatial.sql.
/// One world things are placed in: a real map, or a plan nested in another space. See 0002_spatial.sql.
public sealed class DbSpace
{
  public Guid Id { get; set; }
  public Guid? ParentId { get; set; }
  public string Name { get; set; } = string.Empty;
  public string Kind { get; set; } = SpaceKinds.Plan;
  public bool RealCoordinates { get; set; }
  public int Ordinal { get; set; }
  /// Height of this space's floor above the ground.
  public double ElevationM { get; set; }
  /// What the space shows when it is opened.
  public double ViewSouth { get; set; }
  public double ViewWest { get; set; }
  public double ViewNorth { get; set; }
  public double ViewEast { get; set; }
  /// Polygon on the parent as JSON [[lat, lon], ...]; null for a root or a space with none drawn.
  public string? Footprint { get; set; }
}

public static class SpaceKinds
{
  public const string Map = "map";
  public const string Plan = "plan";
}

/// A plan space's picture and the three corners that place it in that space.
public sealed class DbSpacePlan
{
  public Guid SpaceId { get; set; }
  public string Mime { get; set; } = string.Empty;
  public int WidthPx { get; set; }
  public int HeightPx { get; set; }
  public double TlLat { get; set; }
  public double TlLon { get; set; }
  public double TrLat { get; set; }
  public double TrLon { get; set; }
  public double BlLat { get; set; }
  public double BlLon { get; set; }
  public string Etag { get; set; } = string.Empty;
  public byte[] Image { get; set; } = [];
}

/// A camera drawn in one space. The one with IsReference is where it really stands.
public sealed class DbCameraPlacement
{
  public string CameraId { get; set; } = string.Empty;
  public Guid SpaceId { get; set; }
  public bool IsReference { get; set; }
  public double Lat { get; set; }
  public double Lon { get; set; }
  public double HeightM { get; set; }
  /// Compass bearing: 0 = north, clockwise.
  public double HeadingDeg { get; set; }
  public double HfovDeg { get; set; }
  public double RangeM { get; set; }
}

public sealed class DbLandmark
{
  public Guid Id { get; set; }
  public Guid SpaceId { get; set; }
  public string Name { get; set; } = string.Empty;
  public double Lat { get; set; }
  public double Lon { get; set; }
  /// Above the floor of its space.
  public double HeightM { get; set; }
}

/// Where a PTZ camera pointed when centred on a landmark, in the camera's own units.
public sealed class DbPtzGeoObservation
{
  public string CameraId { get; set; } = string.Empty;
  public Guid LandmarkId { get; set; }
  public float Pan { get; set; }
  public float Tilt { get; set; }
}

public sealed class DbGroundObservation
{
  public string CameraId { get; set; } = string.Empty;
  public Guid LandmarkId { get; set; }
  public float X { get; set; }
  public float Y { get; set; }
}
