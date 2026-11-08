using System.Collections.Generic;
using System.Linq;
using Vms.Map;
using VmsMap.Domain;

namespace VmsMap.Demo
{
  /// The worlds we install. Today there is one: a data centre on the first floor of Biomolecular
  /// Sciences on the University of Maryland campus — a map, the floor as one room, racks standing in
  /// it, switches in the racks and ports in the switches, with the OnvifSim cameras looking on. The
  /// building's footprint and its doorway are the simulator's own (MarylandWorld.cs), so the plan on
  /// the map and the prism in the world cannot disagree.
  ///
  /// Everything is built from loops and constants: a rack's switches are the same rows for every
  /// rack, and so are their ports.
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
    // 12: the world is the real campus — the first floor of Biomolecular Sciences with its own
    //     footprint, the parking lot outside, the real roads — so every camera, Room 101 and the
    //     person's walk moved with the building, and the plans are drawn from the same constants.
    // 13: the floor is the room: Room 101 is gone as a plan of its own, and the hall pair and the
    //     landmarks stand on the first floor directly. What stands in the room is one diagram, "First
    //     floor", in place of Room 102's: its four racks are regions of it, and every rack is filled
    //     with switches, each with its ports. The camera on the car keeps no replica indoors — its
    //     place is the car on the map, which follows the camera it reports.
    // 14: the readings the demo shows are the indoor master's: a Device telemetry diagram of
    //     `sim-master` stands on the floor beside it — a camera that does stand still, so a diagram
    //     on a plan means something.
    public const int UmdDataCenterVersion = 14;

    /// The world a deployment's `DEMO_CONFIG` names, or null when there is none by that key.
    public static DemoWorld? For(string key) =>
      string.Equals(key, UmdDataCenterKey, StringComparison.Ordinal) ? UmdDataCenter() : null;

    // The building in WGS84: Biomolecular Sciences' real footprint (OSM way 24303349), the same
    // outline MarylandWorld builds its prism from. Its first floor is a plan nested in the map.
    private static readonly DemoPoint[] BuildingOutline =
    [
      new(38.9930303, -76.9379976), new(38.9929189, -76.9379965), new(38.9929180, -76.9380254),
      new(38.9928668, -76.9380242), new(38.9928677, -76.9379965), new(38.9928579, -76.9379965),
      new(38.9927492, -76.9379942), new(38.9927519, -76.9374255), new(38.9928641, -76.9374255),
      new(38.9928659, -76.9373943), new(38.9928731, -76.9373920), new(38.9929198, -76.9373920),
      new(38.9929198, -76.9374244), new(38.9929549, -76.9374232), new(38.9929549, -76.9376601),
      new(38.9929917, -76.9376613), new(38.9929917, -76.9376740), new(38.9929908, -76.9377619),
      new(38.9930312, -76.9377619),
    ];

    private const double BuildingSouth = 38.9927492;
    private const double BuildingNorth = 38.9930312;
    private const double BuildingWest = -76.9380254;
    private const double BuildingEast = -76.9373920;

    // The doorway out to the parking lot, on the north wall: the simulator's own opening, at x = -8
    // of its metres.
    private static readonly DemoPoint Door = new(38.9930307, -76.9378925);
    private const double DoorWidthM = 1.8;

    // The floor is the room: the hall pair and the person's first lap stand on the first floor's own
    // plan, with no plan of a room nested in it. What stands on the floor — the four racks — is a
    // diagram of the floor, not a place with coordinates of its own.
    private const string FloorLayout = "object/floor-layout";

    // A replica of the indoor master, showing the readings the camera itself reports. Its owner is
    // the camera's canonical node, so values written there appear here. It is a camera that stands
    // still, which is what makes a diagram on a plan honest: the one on the car is a car on the map.
    private const string MasterTelemetry = "object/telemetry-sim-master";

    private const string RackType = "type/rack";
    private const string SwitchType = "type/switch";
    private const string PortType = "type/port";
    private const string FloorLayoutType = "type/floor-layout";
    private const string CameraPanelType = "type/camera-rear-panel";

    public static DemoWorld UmdDataCenter()
    {
      var files = new List<DemoFile>
      {
        // The plan is drawn from the building's own constants: the first floor over its real
        // footprint, with the doorway the simulator cuts in its north wall.
        new("file/floor-plan", DemoArt.FloorPlan(BuildingOutline, Door, DoorWidthM), "image/svg+xml"),
        DemoFile.Asset("file/room-icon", "room-icon.png", "image/png"),
        // The diagram backgrounds are vector, and their regions are drawn from the same geometry:
        // see DemoArt, where a socket and the region a port takes are one rectangle.
        new("file/floor-layout-background", DemoArt.FloorLayout(), "image/svg+xml"),
        new("file/rack-background", DemoArt.Rack(), "image/svg+xml"),
        new("file/switch-background", DemoArt.Switch(), "image/svg+xml"),
        new("file/port-icon", DemoArt.Port(), "image/svg+xml"),
        new("file/device-telemetry-background", DemoArt.Telemetry(), "image/svg+xml"),
        new("file/camera-panel-background", DemoArt.CameraRearPanel(), "image/svg+xml"),
      };

      var types = new List<DemoDiagramType>
      {
        FloorLayoutTypeDefinition(),
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
        new("space/building", "First floor", "space/umd", SpaceKind.Plan,
          BuildingSouth, BuildingWest, BuildingNorth, BuildingEast,
          RealCoordinates: true,
          Footprint: BuildingOutline,
          Plan: new DemoPlan("file/floor-plan", DemoArt.FloorPlanWidth, DemoArt.FloorPlanHeight,
            new DemoPoint(BuildingNorth, BuildingWest),
            new DemoPoint(BuildingNorth, BuildingEast),
            new DemoPoint(BuildingSouth, BuildingWest))),
      };

      var nodes = new List<DemoNode>();
      var states = new List<DemoState>();

      // What stands on the floor is one diagram: the first floor in schematic, its four racks drawn
      // into its regions. Each rack opens on its own canvas, and so does every switch inside it.
      // No layer: like the plan's own footprint, the diagram's marker stands on the floor at every
      // zoom the floor is looked at.
      nodes.Add(new DemoNode(FloorLayout, "First floor", "space/building", FloorLayoutType,
        Canvas: new DemoRect(0, 0, DemoArt.FloorLayoutWidth, DemoArt.FloorLayoutHeight),
        At: new DemoPoint(38.9928210, -76.9376266),
        VisualKey: "file/room-icon", VisualWidth: 44, VisualHeight: 44));

      // The indoor master's own readings, on a replica standing beside it on the floor: its place,
      // how fast it moves (nought — it is bolted to the wall), and the relay and sensor wired around
      // it. `DemoValueFeeder` keeps the values fresh from what the camera reports over ONVIF.
      nodes.Add(new DemoNode(MasterTelemetry, "Camera sim-master", "space/building",
        DemoTelemetry.TypeKey,
        Canvas: new DemoRect(0, 0, DemoArt.TelemetryWidth, DemoArt.TelemetryHeight),
        At: new DemoPoint(38.9928060, -76.9379290),
        OwnerCameraId: "sim-master"));

      // The four racks stand in the floor's schematic, filled with switches: every slot takes one,
      // and every switch eight ports. A rack has no point of its own — its place is its region of
      // the diagram, which is where it is drawn.
      for (var index = 1; index <= DemoArt.RackCount; index += 1)
      {
        var rackKey = $"object/rack-a-{index:00}";
        var rackName = $"Rack A-{index:00}";
        nodes.Add(new DemoNode(rackKey, rackName, FloorLayout, RackType,
          RegionKey: $"rack-{index}",
          Canvas: new DemoRect(0, 0, DemoArt.RackWidth, DemoArt.RackHeight),
          ZoomLevel: "Rack"));

        var slug = rackKey["object/".Length..];
        for (var slot = 1; slot <= DemoArt.RackSlots; slot += 1)
        {
          var switchKey = $"object/switch-{slug}-{slot}";
          nodes.Add(new DemoNode(switchKey, $"Switch A-{index:00} U{slot}", rackKey, SwitchType,
            RegionKey: $"slot-{slot}",
            Canvas: new DemoRect(0, 0, DemoArt.SwitchWidth, DemoArt.SwitchHeight)));

          for (var port = 1; port <= DemoArt.PortsPerSwitch; port += 1)
          {
            nodes.Add(new DemoNode($"object/port-{slug}-{slot}-{port:00}", $"Port {port}", switchKey, PortType,
              RegionKey: $"port-{port}",
              Canvas: new DemoRect(0, 0, DemoArt.PortWidth, DemoArt.PortHeight)));
          }
        }
      }

      // The camera on the vehicle has no replica of its own: it is a node under its server, and
      // what it reports is the car's place on the map, which follows it (geo_from_device). A
      // diagram standing still on a floor while the camera drives round the block says the wrong
      // thing.

      // The ports are calm: a state is what an owner reports, and the demo owns no device. The
      // row is still written so the seeder replaces the older world's DISCONNECTED instead of
      // leaving it behind — CONNECTED carries no alarm.
      states.Add(new DemoState("object/port-rack-a-01-1-03", "CONNECTED"));

      var placements = new List<DemoPlacement>
      {
        // The pair works as one: the master's boxes drive the slave's aim. The master stands south
        // of the hall, on the line the person walks out by, and one lens holds the hall and the
        // doorway at the far end; the slave is in the hall's south-east corner, looking across it.
        new("sim-master", "space/building", 38.9928120, -76.9378925, HeightM: 2.8, HeadingDeg: 0, HfovDeg: 45, RangeM: 60),
        new("sim-ptz", "space/building", 38.9928722, -76.9378196, HeightM: 2.8, HeadingDeg: 310, HfovDeg: 65, RangeM: 15),
        // The second pair works as one out in the yard: master2 stands on a pole between the doorway
        // and the ring road, looking back along the line the person walks — its boxes are what aim the
        // slave; ptz2 hangs on a pole of its own between that doorway and the master, nearer the
        // master, so it holds the doorway at one side of its picture and the yard at the other.
        new("sim-master2", "space/umd", 38.9933061, -76.9381467, HeightM: 6, HeadingDeg: 125, HfovDeg: 55, RangeM: 60),
        new("sim-ptz2", "space/umd", 38.9931085, -76.9380312, HeightM: 5.5, HeadingDeg: 88, HfovDeg: 60, RangeM: 100),
        // On a vehicle: this is only where it starts. The camera is flagged geo_from_device, so the
        // map follows the position it reports from then on.
        new("sim-vehicle", "space/umd", 38.9936565, -76.9373030, HeightM: 2, HeadingDeg: 270, HfovDeg: 70, RangeM: 40),
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
          new DemoLevel("Rack", 18, 22),
        ],
        placements,
        states,
        [new DemoRight("space/umd", "user", RightActions.View)]);
    }

    /// The first floor drawn as a schematic instead of a plan: its rack slots are the regions its
    /// racks fill, and each of them opens on the rack's own canvas.
    private static DemoDiagramType FloorLayoutTypeDefinition()
    {
      var regions = new List<DemoRegion>();
      for (var rack = 1; rack <= DemoArt.RackCount; rack += 1)
      {
        regions.Add(new DemoRegion($"rack-{rack}", DemoArt.RackRegion(rack),
          new Dictionary<string, string> { ["color"] = "#4fc3f7", ["fontSize"] = "12px" }));
      }

      return new(FloorLayoutType, "Floor layout", "file/floor-layout-background", regions);
    }

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

    /// A rack cabinet with six equipment slots, one switch in every slot.
    private static DemoDiagramType RackTypeDefinition()
    {
      var regions = new List<DemoRegion>();
      for (var slot = 1; slot <= DemoArt.RackSlots; slot += 1)
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
