using System.Collections.Generic;
using System.Linq;
using VmsMap.Demo;
using VmsMap.Domain;
using Xunit;

namespace VmsMap.Tests;

// The shape of the installed world, checked without a database: the depth the demo promises —
// map, building, the floor's diagram, rack, switch, port — and the things an administrator would
// look for.
public class DemoWorldTests
{
  private readonly DemoWorld _world = DemoWorlds.UmdDataCenter();

  [Fact]
  public void TheFloorIsTheRoomAndItsDiagramShowsTheRacks()
  {
    var map = Assert.Single(_world.Spaces, space => space.ParentKey is null);
    Assert.Equal("space/umd", map.Key);

    var building = Assert.Single(_world.Spaces, space => space.Key == "space/building");
    Assert.Equal(map.Key, building.ParentKey);
    // The building is the real one, named by its floor: its footprint is the 19-corner outline of
    // Biomolecular Sciences, the same the simulator builds its prism from.
    Assert.Equal("First floor", building.Name);
    Assert.Equal(19, building.Footprint?.Count);
    Assert.NotNull(building.Plan);

    // The floor is the room: no plan is nested in it, and the hall pair stands on the floor's own
    // plan. What stands in the room is one diagram of the floor — its four racks are regions of it.
    var diagram = Assert.Single(_world.Nodes, node => node.Key == "object/floor-layout");
    Assert.Equal("First floor Diagram", diagram.Name);
    Assert.Equal(building.Key, diagram.ParentKey);
    Assert.Equal("type/floor-layout", diagram.TypeKey);
    Assert.NotNull(diagram.At);
    // The marker stands on the rack row the simulator builds along Room 101's west wall — not in
    // the east wing of the building, where an earlier world put it.
    Assert.Equal(38.9929167, diagram.At!.Value.Lat, 7);
    Assert.Equal(-76.9379687, diagram.At!.Value.Lon, 7);
    Assert.NotNull(diagram.Canvas);
    Assert.NotNull(diagram.VisualKey);
    Assert.DoesNotContain(_world.Spaces, space => space.ParentKey == building.Key);
  }

  [Fact]
  public void EveryRackIsAFilledCabinetOfTheFloorsDiagram()
  {
    var racks = _world.Nodes.Where(node => node.TypeKey == "type/rack").ToList();
    Assert.Equal(DemoArt.RackCount, racks.Count);

    // All four stand in the floor's schematic, each in a region of its type: a rack has no point of
    // its own on the plan.
    Assert.All(racks, rack =>
    {
      Assert.Equal("object/floor-layout", rack.ParentKey);
      Assert.Null(rack.At);
      Assert.Matches("^rack-[1-4]$", rack.RegionKey);
      Assert.Equal("Rack", rack.ZoomLevel);
      Assert.Equal(240, rack.Canvas?.Width);
      Assert.Equal(720, rack.Canvas?.Height);
    });

    // Every slot of every rack holds a switch, and every switch its eight ports.
    var switches = _world.Nodes.Where(node => node.TypeKey == "type/switch").ToList();
    Assert.Equal(racks.Count * DemoArt.RackSlots, switches.Count);
    Assert.All(switches, computer =>
    {
      Assert.Contains(computer.ParentKey, racks.Select(rack => rack.Key));
      Assert.Matches("^slot-[1-6]$", computer.RegionKey);
    });

    var ports = _world.Nodes.Where(node => node.TypeKey == "type/port").ToList();
    Assert.Equal(switches.Count * DemoArt.PortsPerSwitch, ports.Count);
    Assert.All(ports, port =>
    {
      Assert.Contains(port.ParentKey, switches.Select(computer => computer.Key));
      Assert.Matches("^port-[1-8]$", port.RegionKey);
    });

    var thirdPortOfTheFirstSwitch = ports.Single(port => port.Key == "object/port-rack-a-01-1-03");
    Assert.Equal("Port 3", thirdPortOfTheFirstSwitch.Name);
    Assert.Equal("port-3", thirdPortOfTheFirstSwitch.RegionKey);
    Assert.Equal("object/switch-rack-a-01-1", thirdPortOfTheFirstSwitch.ParentKey);
  }

  [Fact]
  public void TheTypesRegionsAreThePlacesTheNodesTake()
  {
    var rackType = _world.Types.Single(type => type.Key == "type/rack");
    Assert.Equal("Rack 42U", rackType.Name);
    Assert.Equal(DemoArt.RackSlots, rackType.Regions.Count);
    Assert.Contains(rackType.Regions, region => region.Key == "slot-1");

    // The camera's own readings, not the synthetic temperature and humidity the racks used to hold.
    var telemetry = _world.Types.Single(type => type.Key == DemoTelemetry.TypeKey);
    Assert.Equal(DemoTelemetry.Readings, telemetry.Regions.Select(region => region.Key));

    var switchType = _world.Types.Single(type => type.Key == "type/switch");
    Assert.Equal(DemoArt.PortsPerSwitch, switchType.Regions.Count);
    Assert.Contains(switchType.Regions, region => region.Key == "port-8");

    var floorLayout = _world.Types.Single(type => type.Key == "type/floor-layout");
    Assert.Equal(["rack-1", "rack-2", "rack-3", "rack-4"], floorLayout.Regions.Select(region => region.Key));

    // Every region lies on its picture: a child in a region outside it would be drawn in nothing.
    foreach (var region in _world.Types.SelectMany(type => type.Regions))
    {
      Assert.True(region.Geometry.Left >= 0 && region.Geometry.Top >= 0);
      Assert.True(region.Geometry.Left + region.Geometry.Width <= 1.0001);
      Assert.True(region.Geometry.Top + region.Geometry.Height <= 1.0001);
      Assert.True(region.Geometry.Width > 0 && region.Geometry.Height > 0);
    }
  }

  [Fact]
  public void TheFiveCamerasArePlacedInTheWorld()
  {
    Assert.Equal(
      ["sim-master", "sim-master2", "sim-ptz", "sim-ptz2", "sim-vehicle"],
      _world.Placements.Select(placement => placement.CameraId).OrderBy(id => id, System.StringComparer.Ordinal));

    var spaceKeys = _world.Spaces.Select(space => space.Key).ToHashSet();
    Assert.All(_world.Placements, placement => Assert.Contains(placement.SpaceKey, spaceKeys));
    Assert.All(_world.Placements, placement => Assert.True(placement.IsReference));
  }

  [Fact]
  public void TheWorldCarriesTheLandmarksItsCamerasAreCalibratedOn()
  {
    // The points MarylandWorld marks with coloured blocks: five in the hall, five out on the lot,
    // each on the ground. The camera poses and these are the same constants, so a landmark cannot
    // move on the map without the world moving with it.
    var hall = _world.Landmarks.Where(landmark => landmark.SpaceKey == "space/building").ToList();
    var road = _world.Landmarks.Where(landmark => landmark.SpaceKey == "space/umd").ToList();

    Assert.Equal(5, hall.Count);
    Assert.Equal(5, road.Count);
    Assert.Equal(["L1 red (hall)", "L2 yellow (hall)", "L3 blue (hall)", "L4 white (hall)", "L5 purple (hall)"],
      hall.Select(landmark => landmark.Name));
    // The two sets are drawn in one tree, one under the map and one under the plan: a name that
    // both share reads as two identical rows.
    Assert.Equal(
      _world.Landmarks.Count,
      _world.Landmarks.Select(landmark => landmark.Name).Distinct(StringComparer.Ordinal).Count());
    Assert.All(_world.Landmarks, landmark => Assert.Equal(0, landmark.HeightM));

    // The hall's L1 is world (-10.6, -71.6), the first of the blocks the simulator stands there.
    var l1 = hall.Single(landmark => landmark.Name.StartsWith("L1"));
    Assert.Equal(38.9929432, l1.Lat, 7);
    Assert.Equal(-76.9379225, l1.Lon, 7);
  }

  [Fact]
  public void EveryCalibrationNamesALandmarkAndACameraTheWorldHas()
  {
    var landmarks = _world.Landmarks.Select(landmark => landmark.Key).ToHashSet(StringComparer.Ordinal);
    var cameras = _world.Placements.Select(placement => placement.CameraId).ToHashSet(StringComparer.Ordinal);

    Assert.All(_world.GroundMarks, mark =>
    {
      Assert.Contains(mark.LandmarkKey, landmarks);
      Assert.Contains(mark.CameraKey, cameras);
      // A mark is a point inside the picture.
      Assert.InRange(mark.X, 0, 1);
      Assert.InRange(mark.Y, 0, 1);
    });
    Assert.All(_world.PtzObservations, observation =>
    {
      Assert.Contains(observation.LandmarkKey, landmarks);
      Assert.Contains(observation.CameraKey, cameras);
      Assert.InRange(observation.Pan, -1, 1);
      Assert.InRange(observation.Tilt, -1, 1);
      if (observation.Zoom is { } zoom) Assert.InRange(zoom, 0, 1);
    });

    // Every landmark is marked on its master and observed by its slave: one set per pair.
    Assert.Equal(_world.Landmarks.Count, _world.GroundMarks.Count);
    Assert.Equal(_world.Landmarks.Count, _world.PtzObservations.Count);

    // The hall's slave frames a person at each landmark; the yard's keeps the wide lens.
    Assert.All(_world.PtzObservations.Where(observation => observation.CameraKey == "sim-ptz"),
      observation => Assert.NotNull(observation.Zoom));
    Assert.All(_world.PtzObservations.Where(observation => observation.CameraKey == "sim-ptz2"),
      observation => Assert.Null(observation.Zoom));
  }

  [Fact]
  public void TheCameraPanelIsATemplateForOneModel()
  {
    // A device's diagram: the model it is for, and the part each socket stands for, found by the
    // token the camera itself reports.
    var panel = _world.Types.Single(type => type.Name == "Camera rear panel");

    Assert.Equal("camera", panel.SourceKind);
    Assert.Equal("OnvifSim", panel.DeviceManufacturer);
    Assert.Equal("Simulated fixed camera", panel.DeviceModel);
    Assert.True(panel.AutoAttach);

    var bindings = panel.Regions.ToDictionary(region => region.Key, region => region.Part);
    Assert.Equal(4, bindings.Count);
    Assert.All(bindings.Values, part => Assert.Equal("token", part!.MatchBy));
    Assert.Equal("digital_input", bindings["input"]!.PartKind);
    Assert.Equal("relay", bindings["lamp"]!.PartKind);
    Assert.Equal("relay", bindings["peer"]!.PartKind);
    Assert.Equal("relay", bindings["loop"]!.PartKind);
  }

  [Fact]
  public void TheSecondPairStandsInTheParkingLot_ItsSlaveBetweenTheDoorwayAndTheMaster()
  {
    // The hall pair's master watches the line out of the hall to the doorway; the second pair works
    // in the parking lot outside: master2 stands north-west of the doorway, looking back along the
    // line the person crosses, and ptz2 on a pole of its own between the two, so it holds the doorway
    // at one side and the lot the person turns into at the other.
    Assert.Equal(18, _world.Version);

    var hall = _world.Placements.Single(placement => placement.CameraId == "sim-master");
    Assert.Equal("space/building", hall.SpaceKey);
    Assert.Equal(38.9928120, hall.Lat, 6);
    Assert.Equal(-76.9378925, hall.Lon, 6);
    Assert.Equal(2.8, hall.HeightM);
    Assert.Equal(0, hall.HeadingDeg);
    Assert.Equal(45, hall.HfovDeg);

    var hallSlave = _world.Placements.Single(placement => placement.CameraId == "sim-ptz");
    Assert.Equal("space/building", hallSlave.SpaceKey);
    Assert.Equal(38.9928722, hallSlave.Lat, 6);
    Assert.Equal(-76.9378196, hallSlave.Lon, 6);
    Assert.Equal(310, hallSlave.HeadingDeg);

    var master = _world.Placements.Single(placement => placement.CameraId == "sim-master2");
    Assert.Equal("space/umd", master.SpaceKey);
    Assert.Equal(38.9933061, master.Lat, 6);
    Assert.Equal(-76.9381467, master.Lon, 6);
    Assert.Equal(6, master.HeightM);
    Assert.Equal(125, master.HeadingDeg);
    Assert.Equal(55, master.HfovDeg);

    var ptz = _world.Placements.Single(placement => placement.CameraId == "sim-ptz2");
    Assert.Equal("space/umd", ptz.SpaceKey);
    Assert.Equal(38.9931085, ptz.Lat, 6);
    Assert.Equal(-76.9380312, ptz.Lon, 6);
    Assert.Equal(5.5, ptz.HeightM);
    Assert.Equal(88, ptz.HeadingDeg);
    Assert.Equal(60, ptz.HfovDeg);
  }

  [Fact]
  public void TheIndoorMasterShowsItsReadingsOnAReplica()
  {
    // A replica: its owner is the camera's canonical node, which the seeder resolves through
    // device_node. It carries the readings' type and stands as a diagram, so it takes no placement.
    var node = _world.Nodes.Single(candidate => candidate.Key == "object/telemetry-sim-master");
    // A name of its own: the camera's placement replica is already called "Camera sim-master", and
    // two identical names under one plan are indistinguishable in the tree.
    Assert.Equal("Camera sim-master readings", node.Name);
    Assert.Equal("sim-master", node.OwnerCameraId);
    // An icon of its own: without one the node is a plain dot on the plan, which is what made the
    // second replica look like a stray copy of the first.
    Assert.Equal("file/readings-icon", node.VisualKey);
    Assert.Equal(DemoTelemetry.TypeKey, node.TypeKey);
    Assert.Equal("space/building", node.ParentKey);
    Assert.NotNull(node.At);
    Assert.NotNull(node.Canvas);
    Assert.Null(node.RegionKey);
  }

  [Fact]
  public void TheCameraOnTheCarKeepsNoReplica()
  {
    // A diagram stands where it is put, and this camera drives the ring road: its place is the car
    // on the map, which follows what the camera reports (geo_from_device). Nothing of the demo's
    // tree copies it, and nothing indoors shows its readings.
    Assert.DoesNotContain(_world.Nodes, node => node.OwnerCameraId == "sim-vehicle");
  }

  [Fact]
  public void ThePortIsCalmAndTheLevelsArePartOfTheWorld()
  {
    // The demo owns no device, so it writes no alarm. The port's row is CONNECTED, which also
    // replaces the DISCONNECTED an earlier world left behind.
    var port = Assert.Single(_world.States, state => state.NodeKey == "object/port-rack-a-01-1-03");
    Assert.Equal(["CONNECTED"], port.States);

    Assert.Equal(
      ["Campus", "Building", "Rack"],
      _world.Levels.Select(level => level.Name));
    Assert.All(_world.Levels, level => Assert.True(level.Min <= level.Max));

    Assert.Contains(_world.Rights, right =>
      right.NodeKey == "space/umd" && right.Subject == "user" && right.Action == RightActions.View);
  }

  [Fact]
  public void EveryRegionIsAFeatureOfThePictureUnderIt()
  {
    // The types' regions and the vector backgrounds are drawn from the same geometry (DemoArt),
    // which is what keeps a port inside its socket. These assertions are the seam: a region that
    // stopped coming from DemoArt would drift from the picture with nothing else to notice.
    var rackType = _world.Types.Single(type => type.Key == "type/rack");
    for (var slot = 1; slot <= 6; slot += 1)
    {
      Assert.Equal(DemoArt.RackSlot(slot), rackType.Regions.Single(region => region.Key == $"slot-{slot}").Geometry);
    }
    var telemetry = _world.Types.Single(type => type.Key == DemoTelemetry.TypeKey);
    foreach (var reading in DemoTelemetry.Readings)
    {
      Assert.Equal(
        DemoArt.TelemetryReading(reading),
        telemetry.Regions.Single(region => region.Key == reading).Geometry);
    }

    var switchType = _world.Types.Single(type => type.Key == "type/switch");
    for (var port = 1; port <= 8; port += 1)
    {
      Assert.Equal(DemoArt.SwitchPort(port), switchType.Regions.Single(region => region.Key == $"port-{port}").Geometry);
    }

    var floorType = _world.Types.Single(type => type.Key == "type/floor-layout");
    for (var rack = 1; rack <= DemoArt.RackCount; rack += 1)
    {
      Assert.Equal(DemoArt.RackRegion(rack),
        floorType.Regions.Single(region => region.Key == $"rack-{rack}").Geometry);
    }
  }

  [Fact]
  public void ThePicturesAreVector()
  {
    // A raster background cannot be told from its regions by construction, and a scaled one blurs;
    // the diagrams the demo draws are SVG, whose features are the regions themselves. The plans are
    // drawn the same way — from the building's own constants — so they are vector too.
    foreach (var key in new[]
    {
      "file/floor-layout-background", "file/rack-background", "file/switch-background", "file/port-icon",
      "file/device-telemetry-background", "file/floor-plan",
    })
    {
      var file = _world.Files.Single(candidate => candidate.Key == key);
      Assert.Equal("image/svg+xml", file.Mime);

      var text = System.Text.Encoding.UTF8.GetString(file.Content);
      Assert.StartsWith("<svg", text);
      Assert.EndsWith("</svg>", text);
    }
  }

  [Fact]
  public void TheFloorPlanIsTheSilhouetteWithTheDoorway()
  {
    // The plan is the building's silhouette, and the doorway is the world's 1.8 m cut out of the
    // north wall. The floor is the room, so nothing is drawn inside it: a plan with no room in it is
    // the honest picture of one room.
    var file = _world.Files.Single(candidate => candidate.Key == "file/floor-plan");
    var text = System.Text.Encoding.UTF8.GetString(file.Content);

    var building = _world.Spaces.Single(space => space.Key == "space/building");
    var perPixelX = (building.East - building.West) / DemoArt.FloorPlanWidth;
    var perPixelY = (building.North - building.South) / DemoArt.FloorPlanHeight;
    const double EarthRadiusM = 6378137.0;
    var metresPerDegreeLon = EarthRadiusM * Math.Cos(building.South * Math.PI / 180) * Math.PI / 180;

    // The outline is the whole picture: its own corners land on the canvas's.
    var polygon = System.Text.RegularExpressions.Regex.Match(text, "<polygon points=\"([^\"]+)\"");
    Assert.True(polygon.Success, "the plan draws no building");
    var corners = polygon.Groups[1].Value.Split(' ')
      .Select(pair => pair.Split(',').Select(value => double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray())
      .ToList();
    Assert.Equal(building.Footprint!.Count, corners.Count);
    Assert.InRange(corners.Min(corner => corner[0]), 0, 1);
    Assert.InRange(corners.Max(corner => corner[0]), DemoArt.FloorPlanWidth - 1, DemoArt.FloorPlanWidth);
    Assert.InRange(corners.Min(corner => corner[1]), 0, 1);
    Assert.InRange(corners.Max(corner => corner[1]), DemoArt.FloorPlanHeight - 1, DemoArt.FloorPlanHeight);

    Assert.DoesNotContain("<rect", text);

    // The doorway is a gap in the north wall, as wide as the world's door.
    var line = System.Text.RegularExpressions.Regex.Match(text, "<line x1=\"([-0-9.]+)\" y1=\"([-0-9.]+)\" x2=\"([-0-9.]+)\" y2=\"([-0-9.]+)\"");
    Assert.True(line.Success, "the plan draws no doorway");
    double Point(int group) => double.Parse(line.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);
    Assert.InRange((Point(3) - Point(1)) * perPixelX * metresPerDegreeLon, 1.7, 1.9);
    Assert.InRange(Point(2), corners.Min(corner => corner[1]) - 1, corners.Min(corner => corner[1]) + 4);
    Assert.Equal(Point(2), Point(4), 1);
  }

  [Fact]
  public void EveryReferenceTheWorldMakesPointsAtSomethingItWrites()
  {
    var nodeKeys = _world.Nodes.Select(node => node.Key).ToHashSet();
    var spaceKeys = _world.Spaces.Select(space => space.Key).ToHashSet();
    var typeKeys = _world.Types.Select(type => type.Key).ToHashSet();
    var fileKeys = _world.Files.Select(file => file.Key).ToHashSet();

    Assert.Equal(nodeKeys.Count, _world.Nodes.Count);
    Assert.Equal(spaceKeys.Count, _world.Spaces.Count);

    bool AnyParent(string? key) => key is null || nodeKeys.Contains(key) || spaceKeys.Contains(key);

    Assert.All(_world.Nodes, node =>
    {
      Assert.True(AnyParent(node.ParentKey), $"'{node.Key}' names a parent that is not in the world");
      if (node.TypeKey is not null) Assert.Contains(node.TypeKey, typeKeys);
      if (node.IconKey is not null) Assert.Contains(node.IconKey, fileKeys);
    });

    Assert.All(_world.Spaces, space =>
    {
      Assert.True(AnyParent(space.ParentKey));
      if (space.Plan is not null) Assert.Contains(space.Plan.FileKey, fileKeys);
    });

    Assert.All(_world.Types, type =>
    {
      if (type.BackgroundKey is not null) Assert.Contains(type.BackgroundKey, fileKeys);
    });

    Assert.All(_world.States, state => Assert.Contains(state.NodeKey, nodeKeys));
    Assert.All(_world.Rights, right => Assert.Contains(right.NodeKey, nodeKeys.Union(spaceKeys)));
    Assert.All(_world.Placements, placement => Assert.Contains(placement.SpaceKey, spaceKeys));

    // A node that takes a region takes one of the type of the diagram it stands in.
    foreach (var node in _world.Nodes.Where(n => n.RegionKey is not null))
    {
      var parent = _world.Nodes.Single(candidate => candidate.Key == node.ParentKey);
      var type = _world.Types.Single(t => t.Key == parent.TypeKey);
      Assert.Contains(type.Regions, region => region.Key == node.RegionKey);
    }
  }
}

// The id derivation is a contract with VmsCfg's DemoCameraIds, which builds the video wall from the
// very same cameras; the two services have no shared project, so the value is pinned on both sides.
public class DemoIdsTests
{
  [Fact]
  public void ACameraIdIsTheOneVmsCfgDerives()
  {
    Assert.Equal("94681efe-57da-5093-b420-1bed3b28dae8", DemoIds.Camera("sim-master"));
  }
}
