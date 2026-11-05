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
    Assert.NotNull(building.Footprint);
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
  public void TheCameraOnTheVehicleShowsItsReadingsOnAReplica()
  {
    var node = _world.Nodes.Single(candidate => candidate.Key == "object/telemetry-sim-vehicle");

    // A replica: its owner is the camera's canonical node, which the seeder resolves through
    // camera_node. It carries the readings' type and stands as a diagram, so it takes no placement.
    Assert.Equal("sim-vehicle", node.OwnerCameraId);
    Assert.Equal(DemoTelemetry.TypeKey, node.TypeKey);
    Assert.NotNull(node.Canvas);
    Assert.NotNull(node.At);
    Assert.Null(node.RegionKey);
  }

  [Fact]
  public void ThePortAlarmAndTheLevelsArePartOfTheWorld()
  {
    Assert.Contains(_world.States, state =>
      state.NodeKey == "object/port-rack-a-01-03" && state.States.Contains("DISCONNECTED"));

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
  public void TheDiagramBackgroundsAreVector()
  {
    // A raster background cannot be told from its regions by construction, and a scaled one blurs;
    // the diagrams the demo draws are SVG, whose features are the regions themselves.
    foreach (var key in new[]
    {
      "file/room-layout-background", "file/rack-background", "file/switch-background", "file/port-icon",
      "file/device-telemetry-background",
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
