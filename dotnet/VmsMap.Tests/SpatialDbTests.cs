using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Vms.Map;
using VmsMap.Clients;
using VmsMap.Db;
using VmsMap.Domain;
using VmsMap.PubSub;

namespace VmsMap.Tests;

// The spatial module on the in-memory provider: the tree mapping, the placement rules, landmarks,
// observations and plan metadata. The paths that need PostgreSQL -- the recursive subtree delete
// and the cascades -- are in SpatialPostgresTests.
public class SpatialDbTests
{
  private sealed class FakeCatalog : ICameraCatalog
  {
    public HashSet<string> Known { get; } = new(StringComparer.Ordinal) { "cam1", "cam2" };

    public Task<CameraInfo?> GetAsync(string cameraId, CancellationToken ct) =>
      Task.FromResult(Known.Contains(cameraId) ? new CameraInfo(cameraId, "Camera " + cameraId) : null);
  }

  private sealed class RecordingNotifier : IMapConfigNotifier
  {
    public List<(IReadOnlyCollection<string> Objects, IReadOnlyCollection<string> Cameras)> Events { get; } = [];

    public Task PublishAsync(
      IReadOnlyCollection<string> objectIds, IReadOnlyCollection<string> cameraIds, CancellationToken ct)
    {
      Events.Add((objectIds, cameraIds));
      return Task.CompletedTask;
    }
  }

  private sealed record Services(
    VmsMapDbContext Db, SpatialDb Spatial, FakeCatalog Catalog, RecordingNotifier Notifier);

  private static Services Create()
  {
    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options;

    var db = new VmsMapDbContext(options);
    var map = new MapDb(db);
    var stateService = new StateService(db);
    var states = new StatesUpdateService(stateService, map, db);
    var levels = new LevelService(db);
    var trackService = new TrackService(db, levels);
    var geo = new GeoService(db, levels);
    var tracks = new TracksUpdateService(map, trackService, geo);
    var updates = new MapUpdateService(map, tracks, states);
    var catalog = new FakeCatalog();
    var notifier = new RecordingNotifier();

    return new Services(db, new SpatialDb(db, map, updates, geo, catalog, notifier), catalog, notifier);
  }

  private static Space MapSpace(string name = "Main map") => new()
  {
    Name = name,
    Kind = SpaceKind.Map,
    RealCoordinates = true,
    View = new GeoBounds
    {
      SouthWest = new Coordinate { Lat = 0, Lon = 0 },
      NorthEast = new Coordinate { Lat = 1, Lon = 1 },
    },
  };

  private static Space PlanSpace(string name, string parentId, bool real = true, params (double Lat, double Lon)[] footprint)
  {
    var space = new Space
    {
      Name = name,
      ParentId = parentId,
      Kind = SpaceKind.Plan,
      RealCoordinates = real,
      View = new GeoBounds
      {
        SouthWest = new Coordinate { Lat = 0, Lon = 0 },
        NorthEast = new Coordinate { Lat = 1, Lon = 1 },
      },
    };
    space.Footprint.AddRange(footprint.Select(p => new Coordinate { Lat = p.Lat, Lon = p.Lon }));
    return space;
  }

  private static CameraPlacement Placement(
    string cameraId, string spaceId, bool reference, double lat = 0.5, double lon = 0.5) => new()
  {
    CameraId = cameraId,
    SpaceId = spaceId,
    IsReference = reference,
    Lat = lat,
    Lon = lon,
    HeightM = 3,
    HeadingDeg = 90,
    HfovDeg = 60,
    RangeM = 30,
  };

  private static Landmark LandmarkAt(string spaceId, string name, double lat = 0.4, double lon = 0.4) => new()
  {
    SpaceId = spaceId,
    Name = name,
    Lat = lat,
    Lon = lon,
    HeightM = 2,
  };

  [Fact]
  public async Task SpaceIsATreeNodeWithItsFootprintOnTheParentCanvas()
  {
    var test = Create();

    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    var plan = await test.Spatial.UpsertSpaceAsync(
      PlanSpace("Floor", map.Id, real: true, (0.1, 0.1), (0.2, 0.1), (0.1, 0.2)),
      CancellationToken.None);

    var marker = await test.Db.Markers.SingleAsync(m => m.id == Guid.Parse(plan.Id));
    Assert.Equal("Floor", marker.name);
    Assert.Equal(Guid.Parse(map.Id), marker.parent_id!.Value);

    var row = await test.Db.Spaces.SingleAsync(s => s.object_id == Guid.Parse(plan.Id));
    Assert.Equal(SpaceKinds.Plan, row.kind);

    var figure = await test.Db.GeoObjects.SingleAsync(g => g.id == Guid.Parse(plan.Id));
    Assert.IsType<NetTopologySuite.Geometries.Polygon>(figure.figure);
    // The footprint is drawn on the map, the nearest canvas ancestor.
    Assert.Equal(Guid.Parse(map.Id), figure.canvas_id);

    var spaces = await test.Spatial.ListSpacesAsync(CancellationToken.None);
    Assert.Equal(2, spaces.Spaces.Count);
    Assert.Equal(3, spaces.Spaces.Single(s => s.Id == plan.Id).Footprint.Count);
  }

  [Fact]
  public async Task MapCannotBeNestedAndASpaceCannotChangeKind()
  {
    var test = Create();
    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);

    var nestedMap = MapSpace("Nested");
    nestedMap.ParentId = map.Id;
    await Assert.ThrowsAsync<SpatialConfigException>(
      () => test.Spatial.UpsertSpaceAsync(nestedMap, CancellationToken.None));

    var plan = PlanSpace("Floor", map.Id);
    await test.Spatial.UpsertSpaceAsync(plan, CancellationToken.None);
    var changed = plan.Clone();
    changed.Kind = SpaceKind.Map;
    await Assert.ThrowsAsync<SpatialConfigException>(
      () => test.Spatial.UpsertSpaceAsync(changed, CancellationToken.None));
  }

  [Fact]
  public async Task SiblingSpacesCannotShareAName()
  {
    var test = Create();
    await test.Spatial.UpsertSpaceAsync(MapSpace("Main"), CancellationToken.None);
    await Assert.ThrowsAsync<SpatialConfigException>(
      () => test.Spatial.UpsertSpaceAsync(MapSpace("Main"), CancellationToken.None));
  }

  [Fact]
  public async Task ASpaceCannotBePutInsideItsOwnDescendant()
  {
    var test = Create();
    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    var plan = await test.Spatial.UpsertSpaceAsync(PlanSpace("Floor", map.Id), CancellationToken.None);

    map.ParentId = plan.Id;
    await Assert.ThrowsAsync<SpatialConfigException>(
      () => test.Spatial.UpsertSpaceAsync(map, CancellationToken.None));
  }

  [Fact]
  public async Task FirstPlacementBecomesTheReferenceOnTheCameraNode()
  {
    var test = Create();
    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);

    // The client asked for a picture, but this is the camera's first placement.
    await test.Spatial.UpsertPlacementAsync(Placement("cam1", map.Id, reference: false), CancellationToken.None);

    var canonical = Guid.Parse(CameraObjectId.For("cam1"));
    var row = await test.Db.CameraPlacements.SingleAsync(p => p.camera_id == "cam1");
    Assert.Equal(canonical, row.object_id);
    Assert.True(row.is_reference);
    Assert.Equal(90.0, row.heading_deg);

    var marker = await test.Db.Markers.SingleAsync(m => m.id == canonical);
    Assert.Equal(Guid.Parse(map.Id), marker.parent_id!.Value);
    Assert.Equal("Camera cam1", marker.name);
    Assert.Equal(canonical, (await test.Db.GeoObjects.SingleAsync(g => g.id == canonical)).id);

    var reference = await test.Spatial.GetReferencePlacementAsync("cam1", CancellationToken.None);
    Assert.NotNull(reference);
    Assert.Equal(0.5, reference!.Value.Placement.Lat);
  }

  [Fact]
  public async Task APictureInAnotherSpaceIsAReplicaOfTheCameraNode()
  {
    var test = Create();
    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    var plan = await test.Spatial.UpsertSpaceAsync(PlanSpace("Floor", map.Id), CancellationToken.None);

    await test.Spatial.UpsertPlacementAsync(Placement("cam1", map.Id, reference: true), CancellationToken.None);
    await test.Spatial.UpsertPlacementAsync(
      Placement("cam1", plan.Id, reference: false, lat: 0.2, lon: 0.2), CancellationToken.None);

    var canonical = Guid.Parse(CameraObjectId.For("cam1"));
    var picture = await test.Db.CameraPlacements.SingleAsync(p => p.camera_id == "cam1" && !p.is_reference);
    Assert.NotEqual(canonical, picture.object_id);

    var replica = await test.Db.Markers.SingleAsync(m => m.id == picture.object_id);
    Assert.Equal(canonical, replica.owner_id);
    Assert.Equal(Guid.Parse(plan.Id), replica.parent_id!.Value);

    var placements = await test.Spatial.ListPlacementsAsync(CancellationToken.None);
    Assert.Equal(2, placements.Count);
    Assert.Single(placements, p => p.IsReference);
  }

  [Fact]
  public async Task MovingTheReferenceLeavesAPictureWhereTheCameraStood()
  {
    var test = Create();
    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    var plan = await test.Spatial.UpsertSpaceAsync(PlanSpace("Floor", map.Id), CancellationToken.None);

    await test.Spatial.UpsertPlacementAsync(
      Placement("cam1", map.Id, reference: true, lat: 0.3, lon: 0.3), CancellationToken.None);
    await test.Spatial.UpsertPlacementAsync(
      Placement("cam1", plan.Id, reference: true, lat: 0.2, lon: 0.2), CancellationToken.None);

    var canonical = Guid.Parse(CameraObjectId.For("cam1"));
    var rows = await test.Db.CameraPlacements.Where(p => p.camera_id == "cam1").ToListAsync();
    Assert.Equal(2, rows.Count);
    Assert.Single(rows, r => r.is_reference && r.object_id == canonical);
    Assert.Equal(Guid.Parse(plan.Id), (await test.Db.Markers.SingleAsync(m => m.id == canonical)).parent_id!.Value);

    var picture = rows.Single(r => !r.is_reference);
    var replica = await test.Db.Markers.SingleAsync(m => m.id == picture.object_id);
    Assert.Equal(canonical, replica.owner_id);
    Assert.Equal(Guid.Parse(map.Id), replica.parent_id);
  }

  [Fact]
  public async Task PromotingAPictureTakesItsPlaceAndKeepsTheOldOneAsAPicture()
  {
    var test = Create();
    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    var plan = await test.Spatial.UpsertSpaceAsync(PlanSpace("Floor", map.Id), CancellationToken.None);

    await test.Spatial.UpsertPlacementAsync(Placement("cam1", map.Id, reference: true), CancellationToken.None);
    await test.Spatial.UpsertPlacementAsync(Placement("cam1", plan.Id, reference: false), CancellationToken.None);

    // Promote the picture: the node moves here, and the old place stays drawn.
    await test.Spatial.UpsertPlacementAsync(Placement("cam1", plan.Id, reference: true), CancellationToken.None);

    var canonical = Guid.Parse(CameraObjectId.For("cam1"));
    var rows = await test.Db.CameraPlacements.Where(p => p.camera_id == "cam1").ToListAsync();
    Assert.Equal(2, rows.Count);
    Assert.Single(rows, r => r.is_reference);
    Assert.Equal(canonical, rows.Single(r => r.is_reference).object_id);
    Assert.Equal(Guid.Parse(plan.Id), (await test.Db.Markers.SingleAsync(m => m.id == canonical)).parent_id!.Value);
  }

  [Fact]
  public async Task ClearingTheReferenceLeavesTheCameraWithoutOne()
  {
    var test = Create();
    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    await test.Spatial.UpsertPlacementAsync(Placement("cam1", map.Id, reference: true), CancellationToken.None);

    await test.Spatial.UpsertPlacementAsync(Placement("cam1", map.Id, reference: false), CancellationToken.None);

    Assert.False((await test.Db.CameraPlacements.SingleAsync(p => p.camera_id == "cam1")).is_reference);
    Assert.Null(await test.Spatial.GetReferencePlacementAsync("cam1", CancellationToken.None));
    Assert.False((await test.Spatial.GetCameraSpatialModelAsync("cam1", CancellationToken.None)).Found);
  }

  [Fact]
  public async Task RemovingTheReferenceUnparentsTheNodeAndKeepsPictures()
  {
    var test = Create();
    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    var plan = await test.Spatial.UpsertSpaceAsync(PlanSpace("Floor", map.Id), CancellationToken.None);
    await test.Spatial.UpsertPlacementAsync(Placement("cam1", map.Id, reference: true), CancellationToken.None);
    await test.Spatial.UpsertPlacementAsync(Placement("cam1", plan.Id, reference: false), CancellationToken.None);

    Assert.True(await test.Spatial.RemovePlacementAsync("cam1", Guid.Parse(map.Id), CancellationToken.None));

    var canonical = Guid.Parse(CameraObjectId.For("cam1"));
    var marker = await test.Db.Markers.SingleAsync(m => m.id == canonical);
    Assert.Null(marker.parent_id);
    Assert.False(await test.Db.GeoObjects.AnyAsync(g => g.id == canonical));
    Assert.False(await test.Db.CameraPlacements.AnyAsync(p => p.object_id == canonical && p.is_reference));

    // The picture on the plan stays, and it still points at the node.
    var picture = await test.Db.CameraPlacements.SingleAsync(p => p.camera_id == "cam1");
    Assert.False(picture.is_reference);
    Assert.Equal(canonical, (await test.Db.Markers.SingleAsync(m => m.id == picture.object_id)).owner_id);
  }

  [Fact]
  public async Task RemovingAPictureDeletesItsNode()
  {
    var test = Create();
    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    var plan = await test.Spatial.UpsertSpaceAsync(PlanSpace("Floor", map.Id), CancellationToken.None);
    await test.Spatial.UpsertPlacementAsync(Placement("cam1", map.Id, reference: true), CancellationToken.None);
    await test.Spatial.UpsertPlacementAsync(Placement("cam1", plan.Id, reference: false), CancellationToken.None);

    Assert.True(await test.Spatial.RemovePlacementAsync("cam1", Guid.Parse(plan.Id), CancellationToken.None));

    var picture = await test.Db.CameraPlacements.SingleAsync(p => p.camera_id == "cam1");
    Assert.True(picture.is_reference);
    Assert.Equal(1, await test.Db.CameraPlacements.CountAsync());
    Assert.Equal(1, await test.Db.Markers.CountAsync(m => m.name == "Camera cam1"));
  }

  [Fact]
  public async Task UnknownCameraIsRejected()
  {
    var test = Create();
    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);

    await Assert.ThrowsAsync<SpatialConfigException>(
      () => test.Spatial.UpsertPlacementAsync(Placement("ghost", map.Id, reference: true), CancellationToken.None));
  }

  [Fact]
  public async Task LandmarkIsANodeAndCannotMoveToAnotherSpace()
  {
    var test = Create();
    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    var plan = await test.Spatial.UpsertSpaceAsync(PlanSpace("Floor", map.Id), CancellationToken.None);

    var landmark = await test.Spatial.UpsertLandmarkAsync(LandmarkAt(map.Id, "Corner"), CancellationToken.None);
    var marker = await test.Db.Markers.SingleAsync(m => m.id == Guid.Parse(landmark.Id));
    Assert.Equal("Corner", marker.name);
    Assert.Equal(Guid.Parse(map.Id), marker.parent_id!.Value);
    Assert.Equal(2.0, (await test.Db.Landmarks.SingleAsync(l => l.object_id == Guid.Parse(landmark.Id))).height_m);

    landmark.SpaceId = plan.Id;
    await Assert.ThrowsAsync<SpatialConfigException>(
      () => test.Spatial.UpsertLandmarkAsync(landmark, CancellationToken.None));

    Assert.Single(await test.Spatial.ListLandmarksAsync(Guid.Parse(map.Id), CancellationToken.None));
    Assert.True(await test.Spatial.RemoveLandmarkAsync(Guid.Parse(landmark.Id), CancellationToken.None));
    Assert.False(await test.Db.Markers.AnyAsync(m => m.id == Guid.Parse(landmark.Id)));
  }

  [Fact]
  public async Task ObservationsNeedAReferenceAndSharedGeometry()
  {
    var test = Create();
    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    var plan = await test.Spatial.UpsertSpaceAsync(PlanSpace("Floor", map.Id), CancellationToken.None);
    var world = await test.Spatial.UpsertSpaceAsync(PlanSpace("Own world", map.Id, real: false), CancellationToken.None);
    var landmark = await test.Spatial.UpsertLandmarkAsync(LandmarkAt(map.Id, "Corner"), CancellationToken.None);
    var foreign = await test.Spatial.UpsertLandmarkAsync(LandmarkAt(world.Id, "Elsewhere"), CancellationToken.None);

    var observations = new PtzGeoObservations { CameraId = "cam1" };
    observations.Observations.Add(new PtzGeoObservation { LandmarkId = landmark.Id, Pan = 10, Tilt = -5 });
    await Assert.ThrowsAsync<SpatialConfigException>(
      () => test.Spatial.SetObservationsAsync(observations, CancellationToken.None));

    await test.Spatial.UpsertPlacementAsync(Placement("cam1", map.Id, reference: true), CancellationToken.None);
    await test.Spatial.SetObservationsAsync(observations, CancellationToken.None);

    var foreignObservations = new PtzGeoObservations { CameraId = "cam1" };
    foreignObservations.Observations.Add(new PtzGeoObservation { LandmarkId = foreign.Id, Pan = 10, Tilt = -5 });
    await Assert.ThrowsAsync<SpatialConfigException>(
      () => test.Spatial.SetObservationsAsync(foreignObservations, CancellationToken.None));

    // A landscape in real coordinates is shared, a made-up plan is a world of its own.
    var planLandmark = await test.Spatial.UpsertLandmarkAsync(LandmarkAt(plan.Id, "Plan corner"), CancellationToken.None);
    observations.Observations.Add(new PtzGeoObservation { LandmarkId = planLandmark.Id, Pan = 20, Tilt = 5, Zoom = 3 });
    await test.Spatial.SetObservationsAsync(observations, CancellationToken.None);
    Assert.Equal(2, (await test.Spatial.GetObservationsAsync("cam1", CancellationToken.None)).Observations.Count);

    // The set replaces the camera's whole set, it does not merge.
    var single = new PtzGeoObservations { CameraId = "cam1" };
    single.Observations.Add(new PtzGeoObservation { LandmarkId = landmark.Id, Pan = 10, Tilt = -5 });
    await test.Spatial.SetObservationsAsync(single, CancellationToken.None);
    Assert.Single((await test.Spatial.GetObservationsAsync("cam1", CancellationToken.None)).Observations);
  }

  [Fact]
  public async Task CameraSpatialModelCarriesElevationAndFiltersLandmarks()
  {
    var test = Create();
    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    var floor = PlanSpace("Floor", map.Id);
    floor.ElevationM = 5;
    var plan = await test.Spatial.UpsertSpaceAsync(floor, CancellationToken.None);
    var world = await test.Spatial.UpsertSpaceAsync(PlanSpace("Own world", map.Id, real: false), CancellationToken.None);

    await test.Spatial.UpsertPlacementAsync(Placement("cam1", plan.Id, reference: true), CancellationToken.None);
    var landmark = await test.Spatial.UpsertLandmarkAsync(LandmarkAt(plan.Id, "Corner"), CancellationToken.None);
    await test.Spatial.UpsertLandmarkAsync(LandmarkAt(world.Id, "Elsewhere"), CancellationToken.None);

    var observations = new PtzGeoObservations { CameraId = "cam1" };
    observations.Observations.Add(new PtzGeoObservation { LandmarkId = landmark.Id, Pan = 10, Tilt = -5 });
    await test.Spatial.SetObservationsAsync(observations, CancellationToken.None);

    var model = await test.Spatial.GetCameraSpatialModelAsync("cam1", CancellationToken.None);
    Assert.True(model.Found);
    Assert.Equal(5.0, model.Space.ElevationM);
    var observation = Assert.Single(model.Observations);
    Assert.Equal(7.0, observation.HeightAboveGroundM);
    Assert.Equal(10f, observation.Pan);
  }

  [Fact]
  public async Task PlanImageKeepsItsEtagAndCorners()
  {
    var test = Create();
    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    var plan = await test.Spatial.UpsertSpaceAsync(PlanSpace("Floor", map.Id), CancellationToken.None);
    var image = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };

    var header = new SpacePlanUploadHeader
    {
      SpaceId = plan.Id,
      WidthPx = 100,
      HeightPx = 50,
      Mime = "image/png",
      Corners = new PlanCorners
      {
        TopLeft = new Coordinate { Lat = 1, Lon = 0 },
        TopRight = new Coordinate { Lat = 1, Lon = 1 },
        BottomLeft = new Coordinate { Lat = 0, Lon = 0 },
      },
    };
    var saved = await test.Spatial.SavePlanAsync(header, image, CancellationToken.None);
    var expected = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(image));
    Assert.Equal(expected, saved.Plan.Etag);

    var notModified = await test.Spatial.GetPlanImageAsync(
      Guid.Parse(plan.Id), [saved.Plan.Etag], CancellationToken.None);
    Assert.Empty(notModified!.Value.Image);

    var full = await test.Spatial.GetPlanImageAsync(Guid.Parse(plan.Id), [], CancellationToken.None);
    Assert.Equal(image, full!.Value.Image);

    await test.Spatial.SetPlanCornersAsync(Guid.Parse(plan.Id), new PlanCorners
    {
      TopLeft = new Coordinate { Lat = 2, Lon = 0 },
      TopRight = new Coordinate { Lat = 2, Lon = 2 },
      BottomLeft = new Coordinate { Lat = 0, Lon = 0 },
    }, CancellationToken.None);
    Assert.Equal(2.0, (await test.Spatial.GetSpaceAsync(Guid.Parse(plan.Id), CancellationToken.None))!.Plan.Corners.TopLeft.Lat);

    Assert.True(await test.Spatial.RemovePlanAsync(Guid.Parse(plan.Id), CancellationToken.None));

    // A map has no picture to draw.
    await Assert.ThrowsAsync<SpatialConfigException>(() => test.Spatial.SavePlanAsync(
      new SpacePlanUploadHeader { SpaceId = map.Id, WidthPx = 1, HeightPx = 1, Mime = "image/png", Corners = header.Corners },
      image, CancellationToken.None));
  }

  [Fact]
  public async Task TheCanvasFollowsTheNearestSpaceAncestorWhenThePlanMoves()
  {
    var test = Create();
    var first = await test.Spatial.UpsertSpaceAsync(MapSpace("First"), CancellationToken.None);
    var second = await test.Spatial.UpsertSpaceAsync(MapSpace("Second"), CancellationToken.None);
    var plan = await test.Spatial.UpsertSpaceAsync(
      PlanSpace("Floor", first.Id, real: true, (0.1, 0.1), (0.2, 0.1), (0.1, 0.2)),
      CancellationToken.None);
    await test.Spatial.UpsertPlacementAsync(Placement("cam1", plan.Id, reference: true), CancellationToken.None);

    var planId = Guid.Parse(plan.Id);
    var cameraId = Guid.Parse(CameraObjectId.For("cam1"));
    Assert.Equal(first.Id, (await test.Db.GeoObjects.SingleAsync(g => g.id == planId)).canvas_id.ToString());
    Assert.Equal(plan.Id, (await test.Db.GeoObjects.SingleAsync(g => g.id == cameraId)).canvas_id.ToString());

    var moved = (await test.Spatial.GetSpaceAsync(planId, CancellationToken.None))!;
    moved.ParentId = second.Id;
    await test.Spatial.UpsertSpaceAsync(moved, CancellationToken.None);

    // The plan's own footprint is now on the other map; its children still draw on the plan.
    Assert.Equal(second.Id, (await test.Db.GeoObjects.SingleAsync(g => g.id == planId)).canvas_id.ToString());
    Assert.Equal(plan.Id, (await test.Db.GeoObjects.SingleAsync(g => g.id == cameraId)).canvas_id.ToString());
  }

  [Fact]
  public async Task SpaceChangesAnnounceAllCamerasAndPlacementChangesNameTheCamera()
  {
    var test = Create();
    var map = await test.Spatial.UpsertSpaceAsync(MapSpace(), CancellationToken.None);
    Assert.Equal(map.Id, Assert.Single(test.Notifier.Events[0].Objects));
    Assert.Empty(test.Notifier.Events[0].Cameras);

    await test.Spatial.UpsertPlacementAsync(Placement("cam1", map.Id, reference: true), CancellationToken.None);
    var placement = test.Notifier.Events.Last();
    Assert.Contains("cam1", placement.Cameras);
    Assert.Contains(CameraObjectId.For("cam1"), placement.Objects);
  }
}
