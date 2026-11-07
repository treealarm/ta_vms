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
    // 5: a camera's own readings — position, speed, relay, sensor — on a Device telemetry diagram,
    //    replacing the racks' synthetic temperature and humidity.
    // 6: the port no longer alarms — the demo has no synthetic state, so its map starts calm.
    // 7: the second master/slave pair moved out to the road: master2 watches the ring road from the
    //    building's north wall, ptz2 turns after the car.
    // 8: a device's diagram — the camera's rear panel, drawn from its model, its regions the parts
    //    the demo configured.
    // 9: the person walks out of a doorway in the building's north wall; the road pair moved west to
    //    see both the road and the ground outside the door, and follows the person as well as the car.
    // 10: both masters turned to face the person's line rather than a room or a road: the hall master
    //     stands south of Room 101 on the line out to the doorway, and the yard pair's slave hangs over
    //     that doorway while its master watches the line from between the door and the ring road.
    // 11: the yard slave moved off the doorway onto a pole of its own between the door and the master,
    //     and the person turns right in the yard to walk a stretch along the ring road.
    public const int UmdDataCenterVersion = 11;

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
    private const string CameraPanelType = "type/camera-rear-panel";

    // A replica of the camera on the vehicle, showing the readings the camera itself reports. Its
    // owner is the camera's canonical node, so values written there appear here.
    private const string VehicleTelemetry = "object/telemetry-sim-vehicle";

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
        new("file/device-telemetry-background", DemoArt.Telemetry(), "image/svg+xml"),
        new("file/camera-panel-background", DemoArt.CameraRearPanel(), "image/svg+xml"),
      };

      var types = new List<DemoDiagramType>
      {
        RoomLayoutTypeDefinition(),
        RackTypeDefinition(),
        SwitchTypeDefinition(),
        new(PortType, "RJ45 Port", "file/port-icon", []),
        DeviceTelemetryTypeDefinition(),
        CameraPanelTypeDefinition(),
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

      // The camera on the vehicle reports its own place, its speed and the state of the relay and
      // sensor wired around it. This diagram is a replica — it shows the camera node's values and
      // owns none of them — and it stands on the plan, because a diagram's canvas is not a space.
      nodes.Add(new DemoNode(VehicleTelemetry, "Camera sim-vehicle", "space/building",
        DemoTelemetry.TypeKey,
        Canvas: new DemoRect(0, 0, DemoArt.TelemetryWidth, DemoArt.TelemetryHeight),
        At: new DemoPoint(38.99206, -76.93875),
        OwnerCameraId: "sim-vehicle"));

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

      // The ports are calm: a state is what an owner reports, and the demo owns no device. The
      // row is still written so the seeder replaces the older world's DISCONNECTED instead of
      // leaving it behind — CONNECTED carries no alarm.
      states.Add(new DemoState("object/port-rack-a-01-03", "CONNECTED"));

      var placements = new List<DemoPlacement>
      {
        // The pair works as one: the master's boxes drive the slave's aim. The master stands south of
        // Room 101, on the line the person walks out by, and one lens holds the room and the doorway at
        // the far end of the hall; the slave is the room's north-east corner, looking back at the room.
        new("sim-master", "space/building", 38.9923000, -76.9383779, HeightM: 2.8, HeadingDeg: 342.3, HfovDeg: 45, RangeM: 60),
        new("sim-ptz", Room101, 38.992484, -76.938372, HeightM: 2.8, HeadingDeg: 235, HfovDeg: 65, RangeM: 15),
        // The second pair works as one out in the yard: master2 stands on a pole between the doorway
        // and the ring road, looking back along the line the person walks — its boxes are what aim the
        // slave; ptz2 hangs on a pole of its own between that doorway and the master, nearer the
        // master, so it holds the doorway at one side of its picture and the yard at the other.
        new("sim-master2", "space/umd", 38.9929468, -76.9388171, HeightM: 6, HeadingDeg: 88.8, HfovDeg: 55, RangeM: 60),
        new("sim-ptz2", "space/umd", 38.9928749, -76.9385397, HeightM: 5.5, HeadingDeg: 0, HfovDeg: 60, RangeM: 100),
        // On a vehicle: this is only where it starts. The camera is flagged geo_from_device, so the
        // map follows the position it reports from then on.
        new("sim-vehicle", "space/umd", 38.99200, -76.93930, HeightM: 2, HeadingDeg: 45, HfovDeg: 60, RangeM: 40),
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

    /// A camera as a switch is drawn: the model's rear panel, one region per terminal. The parts
    /// are found by their tokens — `sim-master` declares exactly these four — and the type attaches
    /// itself to any device that reports being this model. `sim-vehicle` is the same model and has
    /// nothing configured, so it gets no diagram.
    private static DemoDiagramType CameraPanelTypeDefinition() => new(
      CameraPanelType,
      "Camera rear panel",
      "file/camera-panel-background",
      [
        new DemoRegion("input", DemoArt.CameraPanelSocket("input"),
          PanelStyle, new DemoRegionPart("digital_input", "token", "input")),
        new DemoRegion("lamp", DemoArt.CameraPanelSocket("lamp"),
          PanelStyle, new DemoRegionPart("relay", "token", "lamp")),
        new DemoRegion("peer", DemoArt.CameraPanelSocket("peer"),
          PanelStyle, new DemoRegionPart("relay", "token", "peer")),
        new DemoRegion("loop", DemoArt.CameraPanelSocket("loop"),
          PanelStyle, new DemoRegionPart("relay", "token", "loop")),
      ],
      SourceKind: "camera",
      DeviceManufacturer: "OnvifSim",
      DeviceModel: "Simulated fixed camera",
      AutoAttach: true);

    private static readonly Dictionary<string, string> PanelStyle =
      new() { ["color"] = "#4fc3f7", ["fontSize"] = "12px" };

    /// A rack cabinet with six equipment slots. The synthetic readings the demo used to keep here
    /// are gone: the world now shows the camera's own, on its Device telemetry diagram.
    private static DemoDiagramType RackTypeDefinition()
    {
      var regions = new List<DemoRegion>();
      for (var slot = 1; slot <= 6; slot += 1)
      {
        regions.Add(new DemoRegion($"slot-{slot}", DemoArt.RackSlot(slot),
          new Dictionary<string, string> { ["color"] = "#d9e2ec", ["fontSize"] = "12px" }));
      }

      return new(RackType, "Rack 42U", "file/rack-background", regions);
    }

    /// The camera's own readings, one region per value. The region and the label in the picture come
    /// from one geometry (DemoArt.TelemetryReading), so a value is never written beside its name.
    private static DemoDiagramType DeviceTelemetryTypeDefinition() => new(
      DemoTelemetry.TypeKey,
      "Device telemetry",
      "file/device-telemetry-background",
      DemoTelemetry.Readings
        .Select(key => new DemoRegion(key, DemoArt.TelemetryReading(key),
          new Dictionary<string, string> { ["color"] = "#e3f2fd", ["fontSize"] = "12px" }))
        .ToList());

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
