using System.Collections.Generic;
using System.Linq;
using VmsMap.Demo;
using VmsMap.Domain;
using Xunit;

namespace VmsMap.Tests;

// The shape of the installed world, checked without a database: the depth the demo promises —
// map, building, room, rack, switch, port — and the things an administrator would look for.
public class DemoWorldTests
{
  private readonly DemoWorld _world = DemoWorlds.UmdDataCenter();

  [Fact]
  public void TheBuildingHoldsAPlanRoomAndADiagramRoom()
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

    // Room 101 is a plan of its own: a space nested in the building.
    var room = Assert.Single(_world.Spaces, space => space.ParentKey == building.Key);
    Assert.Equal("space/room-101", room.Key);
    Assert.NotNull(room.Footprint);
    Assert.NotNull(room.Plan);

    // Room 102 is a diagram standing on the floor plan: one more node between the building and
    // its racks, with a schematic of its own and an icon where the floor shows it.
    var roomDiagram = Assert.Single(_world.Nodes, node => node.Key == "object/room-102-layout");
    Assert.Equal(building.Key, roomDiagram.ParentKey);
    Assert.Equal("type/room-layout", roomDiagram.TypeKey);
    Assert.NotNull(roomDiagram.At);
    Assert.NotNull(roomDiagram.Canvas);
    Assert.NotNull(roomDiagram.VisualKey);
  }

  [Fact]
  public void RacksStandInThePlanAndInTheRoomDiagram()
  {
    var racks = _world.Nodes.Where(node => node.TypeKey == "type/rack").ToList();
    Assert.Equal(6, racks.Count);

    // On the plan, top-down: each rack is a point in the room's own coordinates.
    var onThePlan = racks.Where(rack => rack.ParentKey == "space/room-101").ToList();
    Assert.Equal(4, onThePlan.Count);
    Assert.All(onThePlan, rack =>
    {
      Assert.NotNull(rack.At);
      Assert.Null(rack.RegionKey);
      Assert.Equal("Rack", rack.ZoomLevel);
      Assert.Equal(240, rack.Canvas?.Width);
      Assert.Equal(720, rack.Canvas?.Height);
    });

    // In the room diagram, schematically: each rack takes a region of the layout's type and
    // stands nowhere on a map of its own.
    var inTheLayout = racks.Where(rack => rack.ParentKey == "object/room-102-layout").ToList();
    Assert.Equal(2, inTheLayout.Count);
    Assert.All(inTheLayout, rack =>
    {
      Assert.Null(rack.At);
      Assert.Matches("^rack-[12]$", rack.RegionKey);
      Assert.Equal(240, rack.Canvas?.Width);
      Assert.Equal(720, rack.Canvas?.Height);
    });

    var switches = _world.Nodes.Where(node => node.TypeKey == "type/switch").ToList();
    Assert.Equal(racks.Count, switches.Count);
    Assert.All(switches, computer =>
    {
      Assert.Contains(computer.ParentKey, racks.Select(rack => rack.Key));
      Assert.Equal("slot-1", computer.RegionKey);
    });

    var ports = _world.Nodes.Where(node => node.TypeKey == "type/port").ToList();
    Assert.Equal(switches.Count * 8, ports.Count);
    Assert.All(ports, port =>
    {
      Assert.Contains(port.ParentKey, switches.Select(computer => computer.Key));
      Assert.Matches("^port-[1-8]$", port.RegionKey);
    });

    var thirdPortOfTheFirstSwitch = ports.Single(port => port.Key == "object/port-rack-a-01-03");
    Assert.Equal("Port 3", thirdPortOfTheFirstSwitch.Name);
    Assert.Equal("port-3", thirdPortOfTheFirstSwitch.RegionKey);
    Assert.Equal("object/switch-rack-a-01", thirdPortOfTheFirstSwitch.ParentKey);
  }

  [Fact]
  public void TheTypesRegionsAreThePlacesTheNodesTake()
  {
    var rackType = _world.Types.Single(type => type.Key == "type/rack");
    Assert.Equal("Rack 42U", rackType.Name);
    Assert.Equal(6, rackType.Regions.Count);
    Assert.Contains(rackType.Regions, region => region.Key == "slot-1");

    // The camera's own readings, not the synthetic temperature and humidity the racks used to hold.
    var telemetry = _world.Types.Single(type => type.Key == DemoTelemetry.TypeKey);
    Assert.Equal(DemoTelemetry.Readings, telemetry.Regions.Select(region => region.Key));

    var switchType = _world.Types.Single(type => type.Key == "type/switch");
    Assert.Equal(8, switchType.Regions.Count);
    Assert.Contains(switchType.Regions, region => region.Key == "port-8");

    var roomLayout = _world.Types.Single(type => type.Key == "type/room-layout");
    Assert.Equal(["rack-1", "rack-2"], roomLayout.Regions.Select(region => region.Key));

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
    // The hall pair's master watches the line out of Room 101 to the doorway; the second pair works
    // in the parking lot outside: master2 stands north-west of the doorway, looking back along the
    // line the person crosses, and ptz2 on a pole of its own between the two, so it holds the doorway
    // at one side and the lot the person turns into at the other.
    Assert.Equal(12, _world.Version);

    var hall = _world.Placements.Single(placement => placement.CameraId == "sim-master");
    Assert.Equal("space/building", hall.SpaceKey);
    Assert.Equal(38.9928120, hall.Lat, 6);
    Assert.Equal(-76.9378925, hall.Lon, 6);
    Assert.Equal(2.8, hall.HeightM);
    Assert.Equal(0, hall.HeadingDeg);
    Assert.Equal(45, hall.HfovDeg);

    var hallSlave = _world.Placements.Single(placement => placement.CameraId == "sim-ptz");
    Assert.Equal("space/room-101", hallSlave.SpaceKey);
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
  public void TheCameraOnTheVehicleShowsItsReadingsOnAReplica()
  {
    var node = _world.Nodes.Single(candidate => candidate.Key == "object/telemetry-sim-vehicle");

    // A replica: its owner is the camera's canonical node, which the seeder resolves through
    // device_node. It carries the readings' type and stands as a diagram, so it takes no placement.
    Assert.Equal("sim-vehicle", node.OwnerCameraId);
    Assert.Equal(DemoTelemetry.TypeKey, node.TypeKey);
    Assert.NotNull(node.Canvas);
    Assert.NotNull(node.At);
    Assert.Null(node.RegionKey);
  }

  [Fact]
  public void ThePortIsCalmAndTheLevelsArePartOfTheWorld()
  {
    // The demo owns no device, so it writes no alarm. The port's row is CONNECTED, which also
    // replaces the DISCONNECTED an earlier world left behind.
    var port = Assert.Single(_world.States, state => state.NodeKey == "object/port-rack-a-01-03");
    Assert.Equal(["CONNECTED"], port.States);

    Assert.Equal(
      ["Campus", "Building", "Room", "Rack"],
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

    var roomType = _world.Types.Single(type => type.Key == "type/room-layout");
    Assert.Equal(DemoArt.RoomRack(1), roomType.Regions.Single(region => region.Key == "rack-1").Geometry);
    Assert.Equal(DemoArt.RoomRack(2), roomType.Regions.Single(region => region.Key == "rack-2").Geometry);
  }

  [Fact]
  public void ThePicturesAreVector()
  {
    // A raster background cannot be told from its regions by construction, and a scaled one blurs;
    // the diagrams the demo draws are SVG, whose features are the regions themselves. The plans are
    // drawn the same way — from the building's own constants — so they are vector too.
    foreach (var key in new[]
    {
      "file/room-layout-background", "file/rack-background", "file/switch-background", "file/port-icon",
      "file/device-telemetry-background", "file/floor-plan", "file/room-101-plan",
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
  public void TheFloorPlanDrawsTheRoomAndAGapInTheWall()
  {
    // The plan is the building's silhouette, not a picture of a room: the room lies in it at the
    // world's own 14 × 10 m, and the doorway is the world's 1.8 m cut out of the north wall. A
    // rectangle with a negative width is an SVG error and draws nothing at all, so a plan can look
    // like a silhouette with a floating "101" and still pass a check that only reads its header.
    var file = _world.Files.Single(candidate => candidate.Key == "file/floor-plan");
    var text = System.Text.Encoding.UTF8.GetString(file.Content);

    var building = _world.Spaces.Single(space => space.Key == "space/building");
    var room = _world.Spaces.Single(space => space.Key == "space/room-101");
    var perPixelX = (building.East - building.West) / DemoArt.FloorPlanWidth;
    var perPixelY = (building.North - building.South) / DemoArt.FloorPlanHeight;
    const double EarthRadiusM = 6378137.0;
    var metresPerDegreeLon = EarthRadiusM * Math.Cos(building.South * Math.PI / 180) * Math.PI / 180;
    var metresPerDegreeLat = EarthRadiusM * Math.PI / 180;

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

    // Room 101: the world's room, at the world's place in the floor.
    var rect = System.Text.RegularExpressions.Regex.Match(text,
      "<rect x=\"([-0-9.]+)\" y=\"([-0-9.]+)\" width=\"([-0-9.]+)\" height=\"([-0-9.]+)\"");
    Assert.True(rect.Success, "the plan draws no room");
    double Number(int group) => double.Parse(rect.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture);
    Assert.InRange(Number(3) * perPixelX * metresPerDegreeLon, 13.9, 14.1);
    Assert.InRange(Number(4) * perPixelY * metresPerDegreeLat, 9.9, 10.1);
    var roomX = (room.West - building.West) / perPixelX;
    var roomY = (building.North - room.North) / perPixelY;
    Assert.InRange(Number(1), roomX - 1, roomX + 1);
    Assert.InRange(Number(2), roomY - 1, roomY + 1);

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
