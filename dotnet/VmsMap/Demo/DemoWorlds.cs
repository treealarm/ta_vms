using System.Collections.Generic;
using System.Linq;
using Vms.Map;
using VmsMap.Domain;

namespace VmsMap.Demo
{
  /// The worlds we install. Today there is one: a fictional data centre in the A.V. Williams
  /// building on the University of Maryland campus — a map, a floor, two rooms, racks standing in
  /// them, switches in the racks and ports in the switches, with the OnvifSim cameras looking on.
  ///
  /// Everything is built from loops and constants: a rack's ports are the same rows for every
  /// rack, and an empty slot is simply a region nothing was put in.
  public static class DemoWorlds
  {
    public const string UmdDataCenterKey = "umd-datacenter";
    public const int UmdDataCenterVersion = 1;

    // The building, in WGS84. Its floor is a plan nested in the map; the rooms are plans nested in
    // the floor, in coordinates of their own that happen to be the same numbers.
    private const double BuildingSouth = 38.9918;
    private const double BuildingNorth = 38.9928;
    private const double BuildingWest = -76.9390;
    private const double BuildingEast = -76.9365;

    private const string Room101 = "space/room-101";
    private const string Room102 = "space/room-102";

    private const string RackType = "type/rack";
    private const string SwitchType = "type/switch";
    private const string PortType = "type/port";

    public static DemoWorld UmdDataCenter()
    {
      var files = new List<DemoFile>
      {
        new("file/floor-plan", "building-floor.png", "image/png"),
        new("file/room-101-plan", "room-101.png", "image/png"),
        new("file/room-102-plan", "room-102.png", "image/png"),
        new("file/rack-background", "rack-42u.png", "image/png"),
        new("file/switch-background", "switch-9300.png", "image/png"),
        new("file/port-icon", "rj45-port.png", "image/png"),
      };

      var types = new List<DemoDiagramType>
      {
        RackTypeDefinition(),
        SwitchTypeDefinition(),
        new(PortType, "RJ45 Port", "file/port-icon", []),
      };

      var spaces = new List<DemoSpace>
      {
        new("space/umd", "UMD Data Center (demo)", null, SpaceKind.Map,
          38.9908, -76.9408, 38.9936, -76.9348),
        new("space/building", "A.V. Williams Building", "space/umd", SpaceKind.Plan,
          BuildingSouth, BuildingWest, BuildingNorth, BuildingEast,
          RealCoordinates: true,
          Footprint: Rectangle(BuildingSouth, BuildingWest, BuildingNorth, BuildingEast),
          Plan: new DemoPlan("file/floor-plan", 1200, 600,
            new DemoPoint(BuildingNorth, BuildingWest),
            new DemoPoint(BuildingNorth, BuildingEast),
            new DemoPoint(BuildingSouth, BuildingWest))),
        RoomPlan(Room101, "Room 101", 38.99238, -76.93855, 38.99250, -76.93835, "file/room-101-plan"),
        RoomPlan(Room102, "Room 102", 38.99238, -76.93760, 38.99250, -76.93740, "file/room-102-plan"),
      };

      var nodes = new List<DemoNode>();
      var states = new List<DemoState>();

      // Four racks stand in Room 101, two in Room 102. Every rack holds one switch in its first
      // slot and eight ports on that switch; the rest of the rack's slots stay empty on purpose.
      var racks = new (string Key, string Name, string Room, double Lat, double Lon)[]
      {
        ("object/rack-a-01", "Rack A-01", Room101, 38.99247, -76.93851),
        ("object/rack-a-02", "Rack A-02", Room101, 38.99247, -76.93845),
        ("object/rack-a-03", "Rack A-03", Room101, 38.99241, -76.93851),
        ("object/rack-a-04", "Rack A-04", Room101, 38.99241, -76.93845),
        ("object/rack-b-01", "Rack B-01", Room102, 38.99247, -76.93756),
        ("object/rack-b-02", "Rack B-02", Room102, 38.99247, -76.93750),
      };

      foreach (var rack in racks)
      {
        nodes.Add(new DemoNode(rack.Key, rack.Name, rack.Room, RackType,
          Canvas: new DemoRect(0, 0, 240, 720),
          At: new DemoPoint(rack.Lat, rack.Lon),
          ZoomLevel: "Rack"));

        var slug = rack.Key["object/".Length..];
        var switchKey = $"object/switch-{slug}";
        nodes.Add(new DemoNode(switchKey, "Switch " + rack.Name[5..], rack.Key, SwitchType,
          RegionKey: "slot-1",
          Canvas: new DemoRect(0, 0, 600, 140)));

        for (var port = 1; port <= 8; port += 1)
        {
          nodes.Add(new DemoNode($"object/port-{slug}-{port:00}", $"Port {port}", switchKey, PortType,
            RegionKey: $"port-{port}"));
        }
      }

      // The plot: the third port of the first rack's switch has lost its link. The alarm climbs
      // from the port through the switch, the rack, the room, the building and the map.
      states.Add(new DemoState("object/port-rack-a-01-03", "DISCONNECTED"));

      var placements = new List<DemoPlacement>
      {
        new("sim-master", Room101, 38.99240, -76.93852, HeightM: 2.8, HeadingDeg: 55, HfovDeg: 70, RangeM: 12),
        new("sim-ptz", Room102, 38.99240, -76.93757, HeightM: 2.8, HeadingDeg: 50, HfovDeg: 65, RangeM: 15),
        new("sim-master2", "space/building", 38.99276, -76.93895, HeightM: 3, HeadingDeg: 135, HfovDeg: 80, RangeM: 60),
        new("sim-ptz2", "space/umd", 38.99250, -76.93950, HeightM: 6, HeadingDeg: 90, HfovDeg: 60, RangeM: 150),
      };

      return new DemoWorld(
        UmdDataCenterKey,
        UmdDataCenterVersion,
        files,
        types,
        spaces,
        nodes,
        [
          new DemoLevel("Campus", 9, 12),
          new DemoLevel("Building", 13, 15),
          new DemoLevel("Room", 16, 17),
          new DemoLevel("Rack", 18, 22),
        ],
        placements,
        states,
        [new DemoRight("space/umd", "user", RightActions.View)]);
    }

    /// A plan on the floor: its footprint there, its own picture, its own view.
    private static DemoSpace RoomPlan(
      string key, string name, double south, double west, double north, double east, string fileKey) =>
      new(key, name, "space/building", SpaceKind.Plan, south, west, north, east,
        RealCoordinates: true,
        Footprint: Rectangle(south, west, north, east),
        Plan: new DemoPlan(fileKey, 800, 600,
          new DemoPoint(north, west),
          new DemoPoint(north, east),
          new DemoPoint(south, west)));

    private static IReadOnlyList<DemoPoint> Rectangle(double south, double west, double north, double east) =>
    [
      new DemoPoint(south, west),
      new DemoPoint(south, east),
      new DemoPoint(north, east),
      new DemoPoint(north, west),
    ];

    /// A rack cabinet with six equipment slots and the two readings the feeder keeps alive.
    private static DemoDiagramType RackTypeDefinition()
    {
      var regions = new List<DemoRegion>();
      for (var slot = 1; slot <= 6; slot += 1)
      {
        regions.Add(new DemoRegion($"slot-{slot}",
          new DemoRect(0.12, 0.06 + (slot - 1) * 0.10, 0.76, 0.075),
          new Dictionary<string, string> { ["color"] = "#d9e2ec", ["fontSize"] = "12px" }));
      }

      regions.Add(new DemoRegion("temp",
        new DemoRect(0.12, 0.78, 0.34, 0.05),
        new Dictionary<string, string> { ["color"] = "#ffd54f", ["fontSize"] = "14px" }));
      regions.Add(new DemoRegion("humidity",
        new DemoRect(0.54, 0.78, 0.34, 0.05),
        new Dictionary<string, string> { ["color"] = "#81d4fa", ["fontSize"] = "14px" }));

      return new(RackType, "Rack 42U", "file/rack-background", regions);
    }

    /// A Catalyst's front panel: eight ports in one row.
    private static DemoDiagramType SwitchTypeDefinition()
    {
      var regions = Enumerable.Range(1, 8)
        .Select(port => new DemoRegion($"port-{port}",
          new DemoRect(0.025 + (port - 1) * 0.12, 0.36, 0.10, 0.28),
          new Dictionary<string, string> { ["color"] = "#b0bec5", ["fontSize"] = "10px" }))
        .ToList();

      return new(SwitchType, "Cisco Catalyst 9300", "file/switch-background", regions);
    }
  }
}
