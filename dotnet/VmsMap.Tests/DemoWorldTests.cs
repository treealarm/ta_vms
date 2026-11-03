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
  public void TheBuildingHoldsTwoRoomsOnTheMap()
  {
    var map = Assert.Single(_world.Spaces, space => space.ParentKey is null);
    Assert.Equal("space/umd", map.Key);

    var building = Assert.Single(_world.Spaces, space => space.Key == "space/building");
    Assert.Equal(map.Key, building.ParentKey);
    Assert.NotNull(building.Footprint);
    Assert.NotNull(building.Plan);

    var rooms = _world.Spaces.Where(space => space.ParentKey == building.Key).ToList();
    Assert.Equal(2, rooms.Count);
    Assert.All(rooms, room =>
    {
      Assert.NotNull(room.Footprint);
      Assert.NotNull(room.Plan);
    });
  }

  [Fact]
  public void RacksStandInRoomsAndSwitchesInThemAndPortsOnTheSwitches()
  {
    var roomKeys = _world.Spaces
      .Where(space => space.ParentKey == "space/building")
      .Select(space => space.Key)
      .ToHashSet();

    var racks = _world.Nodes.Where(node => node.TypeKey == "type/rack").ToList();
    Assert.Equal(6, racks.Count);
    Assert.All(racks, rack =>
    {
      Assert.Contains(rack.ParentKey!, roomKeys);
      Assert.Equal("Rack", rack.ZoomLevel);
      Assert.NotNull(rack.At);
      Assert.NotNull(rack.Canvas);
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
    Assert.Equal(8, rackType.Regions.Count);
    Assert.Contains(rackType.Regions, region => region.Key == "slot-1");
    Assert.Contains(rackType.Regions, region => region.Key == "temp");
    Assert.Contains(rackType.Regions, region => region.Key == "humidity");

    var switchType = _world.Types.Single(type => type.Key == "type/switch");
    Assert.Equal(8, switchType.Regions.Count);
    Assert.Contains(switchType.Regions, region => region.Key == "port-8");

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
  public void TheFourCamerasArePlacedInTheWorld()
  {
    Assert.Equal(
      ["sim-master", "sim-master2", "sim-ptz", "sim-ptz2"],
      _world.Placements.Select(placement => placement.CameraId).OrderBy(id => id, System.StringComparer.Ordinal));

    var spaceKeys = _world.Spaces.Select(space => space.Key).ToHashSet();
    Assert.All(_world.Placements, placement => Assert.Contains(placement.SpaceKey, spaceKeys));
    Assert.All(_world.Placements, placement => Assert.True(placement.IsReference));
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
