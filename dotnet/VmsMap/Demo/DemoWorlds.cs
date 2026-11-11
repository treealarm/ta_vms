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
    // 15: the diagram is named apart from the floor it stands on, and its marker stands on the rack
    //     row the simulator builds along the room's west wall, not in the building's east wing; the
    //     readings replica is named by what it shows.
    // 16: the camera's device diagram is the camera itself — body, lens, cable and the terminal
    //     block its parts are wired to — rather than a schematic panel of sockets.
    // 17: the landmarks name the place they stand in, so the tree's two sets are told apart.
    // 18: the indoor master's device diagram stands on the floor beside it, as an object of the
    //     plan rather than a child of the camera in the iron tree: a diagram is a logical object,
    //     and what hangs under the server is hardware.
    // 19: the world no longer brings zoom layers of its own: the dictionary names the map's scales
    //     (`World` … `Room`) and nothing in the demo used the three it wrote.
    // 20: the camera's diagram hangs under the camera's picture on the floor, not beside it as a
    //     child of the plan; the readings replica is gone — it repeated the picture of the camera,
    //     only drawn as an icon — with the Device telemetry type and the feeder behind it, and the
    //     cameras are drawn from a picture instead of the map's dot.
    public const int UmdDataCenterVersion = 20;

    /// The world a deployment's `DEMO_CONFIG` names, or null when there is none by that key.
    public static DemoWorld? For(string key) =>
      string.Equals(key, UmdDataCenterKey, StringComparison.Ordinal) ? UmdDataCenter() : null;

    // Where the racks stand in the world: `rack-1` … `rack-4` of MarylandWorld are boxes along the
    // west wall of Room 101, at x = -14.6, z = -72.4 … -64.9, and the diagram's marker stands at
    // the middle of that row — the plan draws no room, so the marker is the only thing that says
    // where the equipment is. Pinned in WGS84, as the camera poses are; the simulator's constants
    // and this point are checked against each other by tests/onvifsim.
    private static readonly DemoPoint Racks = new(38.9929167, -76.9379687);

    // The calibration landmarks of the two pairs: the points MarylandWorld marks with coloured
    // blocks, on the floor of the hall and out on the lot. Their names are what an operator reads
    // — `L1 red` — plus the place they stand in, because the tree shows both sets side by side and
    // `L1 red` twice says nothing; their keys are what the marks and observations name.
    private static List<DemoLandmark> Landmarks() =>
    [
      new("landmark/hall/l1", "L1 red (hall)", "space/building", 38.9929432, -76.9379225),
      new("landmark/hall/l2", "L2 yellow (hall)", "space/building", 38.9929432, -76.9378740),
      new("landmark/hall/l3", "L3 blue (hall)", "space/building", 38.9929100, -76.9379225),
      new("landmark/hall/l4", "L4 white (hall)", "space/building", 38.9929100, -76.9378740),
      new("landmark/hall/l5", "L5 purple (hall)", "space/building", 38.9929270, -76.9378982),
      new("landmark/road/l1", "L1 red (road)", "space/umd", 38.9930546, -76.9378925),
      new("landmark/road/l2", "L2 yellow (road)", "space/umd", 38.9931175, -76.9379271),
      new("landmark/road/l3", "L3 blue (road)", "space/umd", 38.9931265, -76.9378462),
      new("landmark/road/l4", "L4 white (road)", "space/umd", 38.9931803, -76.9378925),
      new("landmark/road/l5", "L5 purple (road)", "space/umd", 38.9931983, -76.9377769),
    ];

    // The calibration of the four cameras, computed once from the simulator's own world: the feet
    // of a person standing on each landmark in the master's picture, and the head position (and,
    // for the hall's slave, the framing zoom) that centres it on the slave. The world is
    // deterministic — cameras bolted down, landmarks fixed — so these are constants, and
    // tests/onvifsim/world.py projects the same points again to catch a drift.
    private static readonly DemoGroundMark[] GroundMarks =
    [
      new("sim-master", "landmark/hall/l1", 0.288942, 0.606953),
      new("sim-master", "landmark/hall/l2", 0.630300, 0.606953),
      new("sim-master", "landmark/hall/l3", 0.219821, 0.740750),
      new("sim-master", "landmark/hall/l4", 0.672973, 0.740750),
      new("sim-master", "landmark/hall/l5", 0.454114, 0.662673),
      new("sim-master2", "landmark/road/l1", 0.786424, 0.514470),
      new("sim-master2", "landmark/road/l2", 0.714613, 0.582389),
      new("sim-master2", "landmark/road/l3", 0.542411, 0.526134),
      new("sim-master2", "landmark/road/l4", 0.458604, 0.603585),
      new("sim-master2", "landmark/road/l5", 0.256542, 0.523149),
    ];

    private static readonly DemoPtzObservation[] PtzObservations =
    [
      new("sim-ptz", "landmark/hall/l1", 0.008838, 0.395735, 0.208905),
      new("sim-ptz", "landmark/hall/l2", 0.106873, 0.328499, 0.133485),
      new("sim-ptz", "landmark/hall/l3", -0.081842, 0.347742, 0.153152),
      new("sim-ptz", "landmark/hall/l4", 0.009885, 0.201190, 0.029643),
      new("sim-ptz", "landmark/hall/l5", 0.010502, 0.326694, 0.131707),
      // The yard's slave keeps the wide lens: its landmarks are 10–25 m away along a walk that
      // leaves the frame at both ends, and a framing zoom would follow the person into the zoom
      // limit rather than hold the stretch of road they walk.
      new("sim-ptz2", "landmark/road/l1", 0.158695, 0.231184),
      new("sim-ptz2", "landmark/road/l2", -0.024035, 0.067931),
      new("sim-ptz2", "landmark/road/l3", -0.028428, 0.293989),
      new("sim-ptz2", "landmark/road/l4", -0.175978, 0.256873),
      new("sim-ptz2", "landmark/road/l5", -0.124647, 0.403267),
    ];

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
        DemoFile.Asset("file/server-room-icon", "server-room-icon.png", "image/png"),
        // The cameras are drawn from a picture rather than the map's dot; the map turns it by the
        // camera's heading, so it points the way its view cone does.
        new("file/camera-icon", DemoArt.CameraGlyph(), "image/svg+xml"),
        // The diagram backgrounds are vector, and their regions are drawn from the same geometry:
        // see DemoArt, where a socket and the region a port takes are one rectangle.
        new("file/floor-layout-background", DemoArt.FloorLayout(), "image/svg+xml"),
        new("file/rack-background", DemoArt.Rack(), "image/svg+xml"),
        new("file/switch-background", DemoArt.Switch(), "image/svg+xml"),
        new("file/port-icon", DemoArt.Port(), "image/svg+xml"),
        new("file/camera-panel-background", DemoArt.CameraRearPanel(), "image/svg+xml"),
      };

      var types = new List<DemoDiagramType>
      {
        FloorLayoutTypeDefinition(),
        RackTypeDefinition(),
        SwitchTypeDefinition(),
        new(PortType, "RJ45 Port", "file/port-icon", []),
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
      nodes.Add(new DemoNode(FloorLayout, "First floor Diagram", "space/building", FloorLayoutType,
        Canvas: new DemoRect(0, 0, DemoArt.FloorLayoutWidth, DemoArt.FloorLayoutHeight),
        At: Racks,
        VisualKey: "file/server-room-icon", VisualWidth: 44, VisualHeight: 44));

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
        placements,
        Landmarks(),
        GroundMarks,
        PtzObservations,
        [
          // The indoor master, drawn as the camera it is: body, lens, cable and the terminal block
          // its relay and input parts are wired to. It stands on the floor beside the camera, a
          // diagram of the device rather than the device's node in the tree.
          new DemoDeviceDiagram(
            "object/camera-panel-sim-master",
            "sim-master",
            CameraPanelType,
            "space/building",
            new DemoPoint(38.9927950, -76.9379400),
            "file/camera-panel-background",
            VisualWidth: 44,
            VisualHeight: 28),
        ],
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
