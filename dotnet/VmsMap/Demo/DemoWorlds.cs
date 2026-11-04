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
    // 2: Room 102 became a room diagram on the floor plan instead of a plan of its own.
    // 3: the diagram backgrounds are vector and their regions are the same geometry as the picture.
    // 4: the racks' points are the centres of their rectangles in the plan picture.
    public const int UmdDataCenterVersion = 4;

    /// The world a deployment's `DEMO_CONFIG` names, or null when there is none by that key.
    public static DemoWorld? For(string key) =>
      string.Equals(key, UmdDataCenterKey, StringComparison.Ordinal) ? UmdDataCenter() : null;

    // The building, in WGS84. Its floor is a plan nested in the map.
    private const double BuildingSouth = 38.9918;
    private const double BuildingNorth = 38.9928;
    private const double BuildingWest = -76.9390;
    private const double BuildingEast = -76.9365;

    // A room is a plan (Room 101, top-down) or a diagram on the floor (Room 102, schematic): both
    // are rooms in the same building, and the tree alone says which representation was chosen.
    private const string Room101 = "space/room-101";
    private const string Room102Layout = "object/room-102-layout";

    private const string RackType = "type/rack";
    private const string SwitchType = "type/switch";
    private const string PortType = "type/port";
    private const string RoomLayoutType = "type/room-layout";

    public static DemoWorld UmdDataCenter()
    {
      var files = new List<DemoFile>
      {
        DemoFile.Asset("file/floor-plan", "building-floor.png", "image/png"),
        DemoFile.Asset("file/room-101-plan", "room-101.png", "image/png"),
        DemoFile.Asset("file/room-icon", "room-icon.png", "image/png"),
        // The diagram backgrounds are vector, and their regions are drawn from the same geometry:
        // see DemoArt, where a socket and the region a port takes are one rectangle.
        new("file/room-layout-background", DemoArt.RoomLayout(), "image/svg+xml"),
        new("file/rack-background", DemoArt.Rack(), "image/svg+xml"),
        new("file/switch-background", DemoArt.Switch(), "image/svg+xml"),
        new("file/port-icon", DemoArt.Port(), "image/svg+xml"),
      };

      var types = new List<DemoDiagramType>
      {
        RoomLayoutTypeDefinition(),
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
      };

      var nodes = new List<DemoNode>();
      var states = new List<DemoState>();

      // Room 102 has no plan of its own: it is a diagram standing on the floor plan, and its two
      // racks are elements of that schematic. Each rack still opens on its own canvas.
      // No layer: like a nested plan's footprint, the room's entrance is on the floor at every
      // zoom the floor is looked at. A "Room" layer here would hide it as soon as the plan is
      // zoomed to its own detail.
      nodes.Add(new DemoNode(Room102Layout, "Room 102", "space/building", RoomLayoutType,
        Canvas: new DemoRect(0, 0, DemoArt.RoomLayoutWidth, DemoArt.RoomLayoutHeight),
        At: new DemoPoint(38.99244, -76.93750),
        VisualKey: "file/room-icon", VisualWidth: 44, VisualHeight: 44));

      // Four racks stand in Room 101 (on its plan, top-down), two in Room 102's schematic. Every
      // rack holds one switch in its first slot and eight ports on that switch; the rest of the
      // rack's slots stay empty on purpose. Each point is the centre of the rack's own rectangle
      // in room-101.png, so the dot the editor moves sits on the cabinet it opens.
      var racks = new (string Key, string Name, string Parent, string? Region, double? Lat, double? Lon)[]
      {
        ("object/rack-a-01", "Rack A-01", Room101, null, 38.992468, -76.938516),
        ("object/rack-a-02", "Rack A-02", Room101, null, 38.992468, -76.938471),
        ("object/rack-a-03", "Rack A-03", Room101, null, 38.992436, -76.938516),
        ("object/rack-a-04", "Rack A-04", Room101, null, 38.992436, -76.938471),
        ("object/rack-b-01", "Rack B-01", Room102Layout, "rack-1", null, null),
        ("object/rack-b-02", "Rack B-02", Room102Layout, "rack-2", null, null),
      };

      foreach (var rack in racks)
      {
        nodes.Add(new DemoNode(rack.Key, rack.Name, rack.Parent, RackType,
          RegionKey: rack.Region,
          Canvas: new DemoRect(0, 0, DemoArt.RackWidth, DemoArt.RackHeight),
          At: rack.Lat is { } lat && rack.Lon is { } lon ? new DemoPoint(lat, lon) : null,
          ZoomLevel: "Rack"));

        var slug = rack.Key["object/".Length..];
        var switchKey = $"object/switch-{slug}";
        nodes.Add(new DemoNode(switchKey, "Switch " + rack.Name[5..], rack.Key, SwitchType,
          RegionKey: "slot-1",
          Canvas: new DemoRect(0, 0, DemoArt.SwitchWidth, DemoArt.SwitchHeight)));

        for (var port = 1; port <= 8; port += 1)
        {
          nodes.Add(new DemoNode($"object/port-{slug}-{port:00}", $"Port {port}", switchKey, PortType,
            RegionKey: $"port-{port}",
            Canvas: new DemoRect(0, 0, DemoArt.PortWidth, DemoArt.PortHeight)));
        }
      }

      // The plot: the third port of the first rack's switch has lost its link. The alarm climbs
      // from the port through the switch, the rack, the room, the building and the map.
      states.Add(new DemoState("object/port-rack-a-01-03", "DISCONNECTED"));

      var placements = new List<DemoPlacement>
      {
        new("sim-master", Room101, 38.99240, -76.93852, HeightM: 2.8, HeadingDeg: 55, HfovDeg: 70, RangeM: 12),
        // Room 102 is drawn on the floor plan rather than as a space; its camera stands there.
        new("sim-ptz", "space/building", 38.99240, -76.93757, HeightM: 2.8, HeadingDeg: 50, HfovDeg: 65, RangeM: 15),
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

    /// A room drawn as a schematic instead of a plan: its rack slots are the regions its racks
    /// fill, and each of them opens on the rack's own canvas.
    private static DemoDiagramType RoomLayoutTypeDefinition() => new(
      RoomLayoutType,
      "Room layout",
      "file/room-layout-background",
      [
        new DemoRegion("rack-1", DemoArt.RoomRack(1),
          new Dictionary<string, string> { ["color"] = "#4fc3f7", ["fontSize"] = "12px" }),
        new DemoRegion("rack-2", DemoArt.RoomRack(2),
          new Dictionary<string, string> { ["color"] = "#4fc3f7", ["fontSize"] = "12px" }),
      ]);

    /// A rack cabinet with six equipment slots and the two readings the feeder keeps alive.
    private static DemoDiagramType RackTypeDefinition()
    {
      var regions = new List<DemoRegion>();
      for (var slot = 1; slot <= 6; slot += 1)
      {
        regions.Add(new DemoRegion($"slot-{slot}", DemoArt.RackSlot(slot),
          new Dictionary<string, string> { ["color"] = "#d9e2ec", ["fontSize"] = "12px" }));
      }

      regions.Add(new DemoRegion("temp", DemoArt.RackTemperature,
        new Dictionary<string, string> { ["color"] = "#ffd54f", ["fontSize"] = "11px" }));
      regions.Add(new DemoRegion("humidity", DemoArt.RackHumidity,
        new Dictionary<string, string> { ["color"] = "#81d4fa", ["fontSize"] = "11px" }));

      return new(RackType, "Rack 42U", "file/rack-background", regions);
    }

    /// A Catalyst's front panel: eight ports in one row.
    private static DemoDiagramType SwitchTypeDefinition()
    {
      var regions = Enumerable.Range(1, 8)
        .Select(port => new DemoRegion($"port-{port}", DemoArt.SwitchPort(port),
          new Dictionary<string, string> { ["color"] = "#b0bec5", ["fontSize"] = "10px" }))
        .ToList();

      return new(SwitchType, "Cisco Catalyst 9300", "file/switch-background", regions);
    }
  }
}
