using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using System.Collections.Generic;
using System.Text.Json;

namespace VmsMap.Db
{
  // The map database. The schema itself is created and upgraded by the SQL migrations under
  // Grpc/migrations, not by EF: the model below describes the same tables for queries and must
  // follow them, not the other way round.
  public class VmsMapDbContext : DbContext
  {
    public DbSet<DBGeoObject> GeoObjects { get; set; } = null!;
    public DbSet<DBDiagram> Diagrams { get; set; } = null!;
    public DbSet<DBDiagramTypeRegion> DiagramTypeRegions { get; set; } = null!;
    public DbSet<DBDiagramType> DiagramTypes { get; set; } = null!;
    public DbSet<DBTrackPoint> Tracks { get; set; } = null!;
    public DbSet<DBMarkerProp> Properties { get; set; } = null!;
    public DbSet<DBMarker> Markers { get; set; } = null!;
    public DbSet<DBLevel> Levels { get; set; } = null!;
    public DbSet<DBRight> Rights { get; set; } = null!;
    public DbSet<DBRightsMode> RightsModes { get; set; } = null!;
    public DbSet<DBVisual> Visuals { get; set; } = null!;
    public DbSet<DBFile> Files { get; set; } = null!;

    public DbSet<DBSpace> Spaces { get; set; } = null!;
    public DbSet<DBSpacePlan> SpacePlans { get; set; } = null!;
    public DbSet<DBCameraPlacement> CameraPlacements { get; set; } = null!;
    public DbSet<DBLandmark> Landmarks { get; set; } = null!;
    public DbSet<DBPtzGeoObservation> PtzGeoObservations { get; set; } = null!;
    public DbSet<DBGroundObservation> GroundObservations { get; set; } = null!;

    public DbSet<DBEvent> Events { get; set; } = null!;

    public DbSet<DBValue> Values { get; set; } = null!;

    public DbSet<DBObjectState> ObjectStates { get; set; } = null!;
    public DbSet<DBObjectStateDescription> ObjectStateDescriptions { get; set; } = null!;
    public DbSet<DBAlarmState> AlarmStates { get; set; } = null!;

    public VmsMapDbContext(DbContextOptions<VmsMapDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
      base.OnModelCreating(modelBuilder);

      ConfigureValues(modelBuilder);
      ConfigureEvents(modelBuilder);
      ConfigureStates(modelBuilder);
      ConfigureRights(modelBuilder);
      ConfigureLevels(modelBuilder);
      ConfigureMarkers(modelBuilder);
      ConfigureProperties(modelBuilder);
      ConfigureTrackPoints(modelBuilder);
      ConfigureDiagramTypes(modelBuilder);
      ConfigureDiagrams(modelBuilder);
      ConfigureGeoObjects(modelBuilder);
      ConfigureVisuals(modelBuilder);
      ConfigureFiles(modelBuilder);
      ConfigureSpaces(modelBuilder);
      ConfigureSpacePlans(modelBuilder);
      ConfigureCameraPlacements(modelBuilder);
      ConfigureLandmarks(modelBuilder);
      ConfigureGeoObservations(modelBuilder);
    }

    private void ConfigureGeoObjects(ModelBuilder modelBuilder)
    {
      modelBuilder.Entity<DBGeoObject>(entity =>
      {
        entity.ToTable("geo_objects");
        entity.HasKey(e => e.id);

        entity.Property(e => e.figure)
              .IsRequired()
              .HasColumnName("figure")
              .HasColumnType("geometry");

        entity.Property(e => e.radius).HasColumnName("radius");
        entity.Property(e => e.zoom_level).HasColumnName("zoom_level");
        entity.Property(e => e.canvas_id).HasColumnName("canvas_id");
      });
    }

    private void ConfigureSpaces(ModelBuilder modelBuilder)
    {
      modelBuilder.Entity<DBSpace>(entity =>
      {
        entity.ToTable("spaces");
        entity.HasKey(e => e.object_id);

        entity.Property(e => e.object_id).HasColumnName("object_id");
        entity.Property(e => e.kind).HasColumnName("kind").IsRequired();
        entity.Property(e => e.real_coordinates).HasColumnName("real_coordinates");
        entity.Property(e => e.ordinal).HasColumnName("ordinal");
        entity.Property(e => e.elevation_m).HasColumnName("elevation_m");
        entity.Property(e => e.view_south).HasColumnName("view_south");
        entity.Property(e => e.view_west).HasColumnName("view_west");
        entity.Property(e => e.view_north).HasColumnName("view_north");
        entity.Property(e => e.view_east).HasColumnName("view_east");
      });
    }

    private void ConfigureSpacePlans(ModelBuilder modelBuilder)
    {
      modelBuilder.Entity<DBSpacePlan>(entity =>
      {
        entity.ToTable("space_plan");
        entity.HasKey(e => e.space_id);

        entity.Property(e => e.space_id).HasColumnName("space_id");
        entity.Property(e => e.mime).HasColumnName("mime").IsRequired();
        entity.Property(e => e.width_px).HasColumnName("width_px");
        entity.Property(e => e.height_px).HasColumnName("height_px");
        entity.Property(e => e.tl_lat).HasColumnName("tl_lat");
        entity.Property(e => e.tl_lon).HasColumnName("tl_lon");
        entity.Property(e => e.tr_lat).HasColumnName("tr_lat");
        entity.Property(e => e.tr_lon).HasColumnName("tr_lon");
        entity.Property(e => e.bl_lat).HasColumnName("bl_lat");
        entity.Property(e => e.bl_lon).HasColumnName("bl_lon");
        entity.Property(e => e.etag).HasColumnName("etag").IsRequired();
        entity.Property(e => e.image).HasColumnName("image").IsRequired();
      });
    }

    private void ConfigureCameraPlacements(ModelBuilder modelBuilder)
    {
      modelBuilder.Entity<DBCameraPlacement>(entity =>
      {
        entity.ToTable("camera_placement");
        entity.HasKey(e => e.object_id);

        entity.Property(e => e.object_id).HasColumnName("object_id");
        entity.Property(e => e.camera_id).HasColumnName("camera_id").IsRequired();
        entity.Property(e => e.is_reference).HasColumnName("is_reference");
        entity.Property(e => e.height_m).HasColumnName("height_m");
        entity.Property(e => e.heading_deg).HasColumnName("heading_deg");
        entity.Property(e => e.hfov_deg).HasColumnName("hfov_deg");
        entity.Property(e => e.range_m).HasColumnName("range_m");
      });
    }

    private void ConfigureLandmarks(ModelBuilder modelBuilder)
    {
      modelBuilder.Entity<DBLandmark>(entity =>
      {
        entity.ToTable("landmark");
        entity.HasKey(e => e.object_id);

        entity.Property(e => e.object_id).HasColumnName("object_id");
        entity.Property(e => e.height_m).HasColumnName("height_m");
      });
    }

    private void ConfigureGeoObservations(ModelBuilder modelBuilder)
    {
      modelBuilder.Entity<DBPtzGeoObservation>(entity =>
      {
        entity.ToTable("ptz_geo_observation");
        entity.HasKey(e => new { e.camera_id, e.landmark_id });

        entity.Property(e => e.camera_id).HasColumnName("camera_id").IsRequired();
        entity.Property(e => e.landmark_id).HasColumnName("landmark_id");
        entity.Property(e => e.pan).HasColumnName("pan");
        entity.Property(e => e.tilt).HasColumnName("tilt");
        entity.Property(e => e.zoom).HasColumnName("zoom");
      });

      modelBuilder.Entity<DBGroundObservation>(entity =>
      {
        entity.ToTable("ground_observation");
        entity.HasKey(e => new { e.camera_id, e.landmark_id });

        entity.Property(e => e.camera_id).HasColumnName("camera_id").IsRequired();
        entity.Property(e => e.landmark_id).HasColumnName("landmark_id");
        entity.Property(e => e.x).HasColumnName("x");
        entity.Property(e => e.y).HasColumnName("y");
      });
    }

    private void ConfigureDiagrams(ModelBuilder modelBuilder)
    {
      modelBuilder.Entity<DBDiagram>(entity =>
      {
        entity.ToTable("diagrams");
        entity.HasKey(e => e.id);

        entity.Property(e => e.dgr_type).HasColumnName("dgr_type").IsRequired();

        entity.Property(e => e.geometry)
              .HasColumnName("geometry")
              .HasColumnType("jsonb")
              .HasConversion(
                  v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                  v => JsonSerializer.Deserialize<DBDiagramCoord>(v, (JsonSerializerOptions?)null)!);

        entity.Property(e => e.region_id).HasColumnName("region_id");
        entity.Property(e => e.file_id).HasColumnName("file_id");
      });
    }

    private void ConfigureDiagramTypes(ModelBuilder modelBuilder)
    {
      modelBuilder.Entity<DBDiagramType>(entity =>
      {
        entity.ToTable("diagram_types");
        entity.HasKey(e => e.id);

        entity.Property(e => e.id).HasColumnName("id");
        entity.Property(e => e.name).HasColumnName("name").IsRequired();
        entity.Property(e => e.file_id).HasColumnName("file_id");

        entity.HasMany(d => d.regions)
              .WithOne(r => r.diagram_type)
              .HasForeignKey(r => r.diagram_type_id)
              .OnDelete(DeleteBehavior.Cascade);
      });

      modelBuilder.Entity<DBDiagramTypeRegion>(entity =>
      {
        entity.ToTable("diagram_type_regions");
        entity.HasKey(e => new { e.diagram_type_id, e.region_key });

        entity.Property(e => e.diagram_type_id).HasColumnName("diagram_type_id");
        entity.Property(e => e.region_key).HasColumnName("region_key").IsRequired();

        entity.Property(e => e.geometry)
              .HasColumnName("geometry")
              .HasColumnType("jsonb")
              .HasConversion(
                  v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                  v => JsonSerializer.Deserialize<DBDiagramCoord>(v, (JsonSerializerOptions?)null)!);

        entity.Property(e => e.styles)
              .HasColumnName("styles")
              .HasColumnType("jsonb")
              .HasConversion(
                  v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                  v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, (JsonSerializerOptions?)null),
                  // A dictionary is a reference type: without a comparer EF cannot tell an edited
                  // region's styles from the loaded ones.
                  new ValueComparer<Dictionary<string, string>?>(
                      (a, b) => a == null ? b == null : b != null && a.Count == b.Count && !a.Except(b).Any(),
                      d => d == null ? 0 : d.Aggregate(0, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value)),
                      d => d == null ? null : new Dictionary<string, string>(d)));
      });
    }

    private void ConfigureTrackPoints(ModelBuilder modelBuilder)
    {
      modelBuilder.Entity<DBTrackPoint>(entity =>
      {
        entity.ToTable("track_points");
        entity.HasKey(e => e.id);

        entity.Property(e => e.object_id).HasColumnName("object_id");

        entity.Property(e => e.timestamp)
              .IsRequired()
              .HasColumnName("timestamp");

        entity.Property(e => e.figure)
              .IsRequired()
              .HasColumnName("figure")
              .HasColumnType("geometry");

        entity.Property(e => e.radius).HasColumnName("radius");
        entity.Property(e => e.zoom_level).HasColumnName("zoom_level");

        entity.Property(e => e.extra_props)
              .HasColumnName("extra_props")
              .HasColumnType("jsonb");
      });
    }

    private void ConfigureProperties(ModelBuilder modelBuilder)
    {
      modelBuilder.Entity<DBMarkerProp>(entity =>
      {
        entity.ToTable("properties");
        entity.HasKey(e => e.id);

        entity.Property(e => e.prop_name).IsRequired();
        entity.Property(e => e.str_val);
        entity.Property(e => e.visual_type);
        entity.Property(e => e.object_id).IsRequired();
      });
    }

    private void ConfigureMarkers(ModelBuilder modelBuilder)
    {
      modelBuilder.Entity<DBMarker>(entity =>
      {
        entity.ToTable("objects");
        entity.HasKey(e => e.id);
      });
    }

    private void ConfigureLevels(ModelBuilder modelBuilder)
    {
      modelBuilder.Entity<DBLevel>(entity =>
      {
        entity.ToTable("levels");
        entity.HasKey(e => e.id);

        entity.Property(e => e.id)
            .HasColumnName("id")
            .HasDefaultValueSql("gen_random_uuid()");

        entity.Property(e => e.zoom_level).HasColumnName("zoom_level").IsRequired();
        entity.Property(e => e.zoom_min).HasColumnName("zoom_min").IsRequired();
        entity.Property(e => e.zoom_max).HasColumnName("zoom_max").IsRequired();
      });
    }

    private void ConfigureRights(ModelBuilder modelBuilder)
    {
      modelBuilder.Entity<DBRight>(entity =>
      {
        entity.ToTable("rights");

        entity.HasKey(e => new { e.object_id, e.subject, e.action });

        entity.Property(e => e.object_id).IsRequired();
        entity.Property(e => e.subject).IsRequired();
        entity.Property(e => e.action).IsRequired();
      });

      modelBuilder.Entity<DBRightsMode>(entity =>
      {
        entity.ToTable("rights_mode");

        entity.HasKey(e => e.id);
        entity.Property(e => e.id).ValueGeneratedNever();
        entity.Property(e => e.mode).HasColumnName("mode").IsRequired();
      });
    }

    private void ConfigureVisuals(ModelBuilder modelBuilder)
    {
      modelBuilder.Entity<DBVisual>(entity =>
      {
        entity.ToTable("visual");
        entity.HasKey(e => e.object_id);
        entity.Property(e => e.object_id).HasColumnName("object_id");
      });
    }

    private void ConfigureFiles(ModelBuilder modelBuilder)
    {
      modelBuilder.Entity<DBFile>(entity =>
      {
        entity.ToTable("files");
        entity.HasKey(e => e.id);
        entity.Property(e => e.id).HasDefaultValueSql("gen_random_uuid()");
        entity.Property(e => e.mime).IsRequired();
        entity.Property(e => e.bytes).IsRequired();
      });
    }

    private void ConfigureValues(ModelBuilder modelBuilder)
    {
      modelBuilder.Entity<DBValue>(entity =>
      {
        entity.ToTable("db_values");
        entity.HasKey(e => e.id);

        entity.Property(e => e.owner_id).IsRequired();
        entity.Property(e => e.name).IsRequired();
        entity.Property(e => e.value).HasColumnType("jsonb");
      });
    }

    private void ConfigureEvents(ModelBuilder modelBuilder)
    {
      modelBuilder.Entity<DBEvent>(entity =>
      {
        entity.ToTable("events");

        entity.HasMany(e => e.extra_props)
              .WithOne()
              .HasForeignKey(ep => ep.owner_id)
              .OnDelete(DeleteBehavior.Cascade);
      });

      modelBuilder.Entity<EventProp>(entity =>
      {
        entity.ToTable("event_props");
      });
    }

    private void ConfigureStates(ModelBuilder modelBuilder)
    {
      modelBuilder.Entity<DBObjectState>(entity =>
      {
        entity.ToTable("object_states");
        entity.HasKey(e => e.id);

        entity.Property(e => e.timestamp)
              .IsRequired()
              .HasDefaultValueSql("now()");

        entity.HasMany(e => e.states)
              .WithOne()
              .HasForeignKey(e => e.object_id)
              .OnDelete(DeleteBehavior.Cascade);
      });

      modelBuilder.Entity<DBObjectStateValue>(entity =>
      {
        entity.ToTable("object_state_values");
        entity.HasKey(e => e.id);

        entity.Property(e => e.state).IsRequired();
        entity.Property(e => e.object_id).IsRequired();
      });

      modelBuilder.Entity<DBObjectStateDescription>(entity =>
      {
        entity.ToTable("object_state_descriptions");
        entity.HasKey(e => e.id);

        entity.Property(e => e.alarm)
              .IsRequired()
              .HasDefaultValue(false);

        entity.Property(e => e.state).IsRequired();
        entity.Property(e => e.state_descr);
        entity.Property(e => e.state_color);
      });

      modelBuilder.Entity<DBAlarmState>(entity =>
      {
        entity.ToTable("alarm_states");
        entity.HasKey(e => e.id);

        entity.Property(e => e.children_alarms).IsRequired();
      });
    }
  }
}
