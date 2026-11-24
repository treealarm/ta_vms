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
  // The fixtures these tests share.
  private const string MapSpaceKey = "space/umd";
  private const string BuildingSpaceKey = "space/building";
  private const string RackTypeKey = "type/rack";
  private const string SwitchTypeKey = "type/switch";
  private const string FloorLayoutTypeKey = "type/floor-layout";
  private const string FloorLayoutKey = "object/floor-layout";
  private const string CamPanelsTypeKey = "type/cam-panels";
  private const string CamPanelsKey = "object/cam-panels";
  private const string SpareKey = "object/spare-diagram";
  private const string CameraPanelTypeKey = "type/camera-rear-panel";
  private const string CamPanelsBackgroundKey = "file/cam-panels-background";
  private const string LeftBay = "left";
  private const string RightBay = "right";
  private const string PortKey = "object/port-rack-a-01-1-03";
  private const string FloorPlanFileKey = "file/floor-plan";
  private const string MasterCameraId = "sim-master";
  private const string SecondMasterCameraId = "sim-master2";
  private const string PtzCameraId = "sim-ptz";
  private const string SecondPtzCameraId = "sim-ptz2";
  private const string RelayPart = "relay";
  private const string InputToken = "input";
  private const string ButtonToken = "button";
  private const string ClosedTrigger = "closed";
  private const string OperatorSubject = "user";

  private readonly DemoWorld _world = DemoWorlds.UmdDataCenter();

  [Fact]
  public void TheFloorIsTheRoomAndItsDiagramShowsTheRacks()
  {
    var map = Assert.Single(_world.Spaces, space => space.ParentKey is null);
    Assert.Equal(MapSpaceKey, map.Key);

    var building = Assert.Single(_world.Spaces, space => space.Key == BuildingSpaceKey);
    Assert.Equal(map.Key, building.ParentKey);
    // The building is the real one, named by its floor: its footprint is the 19-corner outline of
    // Biomolecular Sciences, the same the simulator builds its prism from.
    Assert.Equal("plan1", building.Name);
    Assert.Equal(19, building.Footprint?.Count);
    Assert.NotNull(building.Plan);

    // The floor is the room: no plan is nested in it, and the hall pair stands on the floor's own
    // plan. What stands in the room is one diagram of the floor — its four racks are regions of it.
    var diagram = Assert.Single(_world.Nodes, node => node.Key == FloorLayoutKey);
    Assert.Equal("diag1", diagram.Name);
    Assert.Equal(building.Key, diagram.ParentKey);
    Assert.Equal(FloorLayoutTypeKey, diagram.TypeKey);
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
    var racks = _world.Nodes.Where(node => node.TypeKey == RackTypeKey).ToList();
    Assert.Equal(DemoArt.RackCount, racks.Count);

    // All four stand in the floor's schematic, each in a region of its type: a rack has no point of
    // its own on the plan.
    Assert.All(racks, rack =>
    {
      Assert.Equal(FloorLayoutKey, rack.ParentKey);
      Assert.Null(rack.At);
      Assert.Matches("^rack-[1-4]$", rack.RegionKey);
      Assert.Equal("Rack", rack.ZoomLevel);
      Assert.Equal(240, rack.Canvas?.Width);
      Assert.Equal(720, rack.Canvas?.Height);
    });

    // Every slot of every rack holds a switch, and every switch its eight ports.
    var switches = _world.Nodes.Where(node => node.TypeKey == SwitchTypeKey).ToList();
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

    var thirdPortOfTheFirstSwitch = ports.Single(port => port.Key == PortKey);
    Assert.Equal("port3", thirdPortOfTheFirstSwitch.Name);
    Assert.Equal("port-3", thirdPortOfTheFirstSwitch.RegionKey);
    Assert.Equal("object/switch-rack-a-01-1", thirdPortOfTheFirstSwitch.ParentKey);
  }

  [Fact]
  public void TheTypesRegionsAreThePlacesTheNodesTake()
  {
    var rackType = _world.Types.Single(type => type.Key == RackTypeKey);
    Assert.Equal("Rack 42U", rackType.Name);
    Assert.Equal(DemoArt.RackSlots, rackType.Regions.Count);
    Assert.Contains(rackType.Regions, region => region.Key == "slot-1");

    var switchType = _world.Types.Single(type => type.Key == SwitchTypeKey);
    Assert.Equal(DemoArt.PortsPerSwitch, switchType.Regions.Count);
    Assert.Contains(switchType.Regions, region => region.Key == "port-8");

    var floorLayout = _world.Types.Single(type => type.Key == FloorLayoutTypeKey);
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
      [MasterCameraId, SecondMasterCameraId, PtzCameraId, SecondPtzCameraId, "sim-vehicle"],
      _world.Placements.Select(placement => placement.CameraId).OrderBy(id => id, System.StringComparer.Ordinal));

    var spaceKeys = _world.Spaces.Select(space => space.Key).ToHashSet();
    Assert.All(_world.Placements, placement => Assert.Contains(placement.SpaceKey, spaceKeys));
    Assert.All(_world.Placements, placement => Assert.True(placement.IsReference));
  }

  [Fact]
  public void TheWorldInstallsTheSensorsBehaviourAndNotOnlyTheirNames()
  {
    // The routing rule watches sim-ptz's input: it must be armed on its own, because no zone is
    // involved, and its contact that means "fired" is closed. The master's input is the operator's
    // sensor — it follows its disarmed camera until somebody arms it from the card — and its rule
    // is the same contact, so closing `loop` raises.
    var placed = _world.Placements.Select(placement => placement.CameraId).ToHashSet();

    var sensor = Assert.Single(_world.Devices, device => device.CameraKey == PtzCameraId);
    Assert.Equal((InputToken, "armed", ClosedTrigger, true),
      (sensor.Token, sensor.ArmMode, sensor.TriggerOn, sensor.AlarmEnabled));

    var master = Assert.Single(_world.Devices, device => device.CameraKey == MasterCameraId);
    Assert.Equal((InputToken, "inherit", ClosedTrigger, true),
      (master.Token, master.ArmMode, master.TriggerOn, master.AlarmEnabled));

    // A device the world did not place has no camera in the deployment to configure.
    Assert.All(_world.Devices, device => Assert.Contains(device.CameraKey, placed));
  }

  [Fact]
  public void TheWorldCarriesTheLandmarksItsCamerasAreCalibratedOn()
  {
    // The points MarylandWorld marks with coloured blocks: five in the hall, five out on the lot,
    // each on the ground. The camera poses and these are the same constants, so a landmark cannot
    // move on the map without the world moving with it.
    var hall = _world.Landmarks.Where(landmark => landmark.SpaceKey == BuildingSpaceKey).ToList();
    var road = _world.Landmarks.Where(landmark => landmark.SpaceKey == MapSpaceKey).ToList();

    Assert.Equal(5, hall.Count);
    Assert.Equal(5, road.Count);
    Assert.Equal(["L1 red", "L2 yellow", "L3 blue", "L4 white", "L5 purple"],
      hall.Select(landmark => landmark.Name));
    // The two sets are drawn in one tree, one under the map and one under the plan. A name is a
    // type and a number and carries no place, so `L1 red` appears twice — once per space, which is
    // where the operator reads it, and never twice under the same parent.
    Assert.Equal(
      _world.Landmarks.Count,
      _world.Landmarks.Select(landmark => (landmark.SpaceKey, landmark.Name)).Distinct().Count());
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
    Assert.All(_world.PtzObservations.Where(observation => observation.CameraKey == PtzCameraId),
      observation => Assert.NotNull(observation.Zoom));
    Assert.All(_world.PtzObservations.Where(observation => observation.CameraKey == SecondPtzCameraId),
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
    Assert.Equal(5, bindings.Count);
    Assert.All(bindings.Values, part => Assert.Equal("token", part!.MatchBy));
    Assert.Equal("digital_input", bindings[InputToken]!.PartKind);
    Assert.Equal(RelayPart, bindings["lamp"]!.PartKind);
    Assert.Equal(RelayPart, bindings["peer"]!.PartKind);
    Assert.Equal(RelayPart, bindings["loop"]!.PartKind);
    Assert.Equal(RelayPart, bindings[ButtonToken]!.PartKind);
  }

  [Fact]
  public void TheMastersFourthRelayStandsOnTheMapAsAReplica()
  {
    // A part placed by hand and a part the world places are the same row: a node whose owner is
    // the part's canonical node, standing in a space with a point of its own. The name carries the
    // owner's and its own number, as an administrator's placement would.
    var replica = Assert.Single(_world.PartReplicas);
    Assert.Equal((MasterCameraId, RelayPart, ButtonToken, MapSpaceKey),
      (replica.CameraKey, replica.PartKind, replica.Token, replica.SpaceKey));
    Assert.Equal("relay4 rep1", replica.Name);
    Assert.StartsWith("relay4 rep", replica.Name);

    var space = _world.Spaces.Single(candidate => candidate.Key == replica.SpaceKey);
    Assert.InRange(replica.Lat, space.South, space.North);
    Assert.InRange(replica.Lon, space.West, space.East);

    // The panel the replica sits on has a terminal for it, and that terminal is bound to the relay
    // by the token the simulator reports.
    var panel = _world.Types.Single(type => type.Key == "type/camera-rear-panel");
    var region = Assert.Single(panel.Regions, candidate => candidate.Part?.MatchValue == replica.Token);
    Assert.Equal((RelayPart, replica.PartKind), (region.Part!.PartKind, replica.PartKind));
    Assert.Equal(region.Geometry, DemoArt.CameraPanelSocket(replica.Token));
  }

  [Fact]
  public void TheSharedBoardHoldsTwoCamerasSideBySide()
  {
    // One canvas, two cameras: each bay is a region of the board's type and holds a representation
    // of a camera's canonical diagram, so the two panels open on the content their diagrams own.
    var type = _world.Types.Single(candidate => candidate.Key == CamPanelsTypeKey);
    Assert.Equal("Cam diagrams", type.Name);
    Assert.Equal(CamPanelsBackgroundKey, type.BackgroundKey);
    Assert.Equal([LeftBay, RightBay], type.Regions.Select(region => region.Key));
    // A bay is a place, not a slot: the panel's own type carries the sockets the parts fill.
    Assert.All(type.Regions, region => Assert.Null(region.Part));
    Assert.Equal(DemoArt.CamPanelBay(1), type.Regions[0].Geometry);
    Assert.Equal(DemoArt.CamPanelBay(2), type.Regions[1].Geometry);

    var board = _world.Nodes.Single(node => node.Key == CamPanelsKey);
    Assert.Equal("diag2", board.Name);
    Assert.Equal(BuildingSpaceKey, board.ParentKey);
    Assert.Equal(CamPanelsTypeKey, board.TypeKey);
    Assert.NotNull(board.At);
    Assert.DoesNotContain(_world.Nodes, node =>
      node.Key != CamPanelsKey && node.At is { } at && at == board.At);

    // The blank diagram waits on the board for the acceptance run: a diagram of its own, drawn by
    // nobody, with no region to fill and no place on the plan.
    var spare = _world.Nodes.Single(node => node.Key == SpareKey);
    Assert.Equal("spare", spare.Name);
    Assert.Equal(CamPanelsKey, spare.ParentKey);
    Assert.Equal(CameraPanelTypeKey, spare.TypeKey);
    Assert.Null(spare.At);
    Assert.Null(spare.RegionKey);
    Assert.NotNull(spare.Canvas);

    // The master's panel is the one the world keeps under its picture, shown on the board as well;
    // the slave has no other diagram, so showing it on the board is where its canonical one is made.
    Assert.Equal(
      [(MasterCameraId, LeftBay), (PtzCameraId, RightBay)],
      _world.DeviceRepresentations.Select(shown => (shown.CameraKey, shown.RegionKey)));
    Assert.All(_world.DeviceRepresentations, shown => Assert.Equal(CameraPanelTypeKey, shown.TypeKey));
    var master = _world.DeviceRepresentations.Single(shown => shown.CameraKey == MasterCameraId);
    Assert.Null(master.CanonKey);
    var slave = _world.DeviceRepresentations.Single(shown => shown.CameraKey == PtzCameraId);
    Assert.NotNull(slave.CanonKey);
    Assert.NotNull(slave.VisualKey);
  }

  [Fact]
  public void TheSecondPairStandsInTheParkingLot_ItsSlaveBetweenTheDoorwayAndTheMaster()
  {
    // The hall pair's master watches the line out of the hall to the doorway; the second pair works
    // in the parking lot outside: master2 stands north-west of the doorway, looking back along the
    // line the person crosses, and ptz2 on a pole of its own between the two, so it holds the doorway
    // at one side and the lot the person turns into at the other.
    Assert.Equal(23, _world.Version);

    var hall = _world.Placements.Single(placement => placement.CameraId == MasterCameraId);
    Assert.Equal(BuildingSpaceKey, hall.SpaceKey);
    Assert.Equal(38.9928120, hall.Lat, 6);
    Assert.Equal(-76.9378925, hall.Lon, 6);
    Assert.Equal(2.8, hall.HeightM);
    Assert.Equal(0, hall.HeadingDeg);
    Assert.Equal(45, hall.HfovDeg);

    var hallSlave = _world.Placements.Single(placement => placement.CameraId == PtzCameraId);
    Assert.Equal(BuildingSpaceKey, hallSlave.SpaceKey);
    Assert.Equal(38.9928722, hallSlave.Lat, 6);
    Assert.Equal(-76.9378196, hallSlave.Lon, 6);
    Assert.Equal(310, hallSlave.HeadingDeg);

    var master = _world.Placements.Single(placement => placement.CameraId == SecondMasterCameraId);
    Assert.Equal(MapSpaceKey, master.SpaceKey);
    Assert.Equal(38.9933061, master.Lat, 6);
    Assert.Equal(-76.9381467, master.Lon, 6);
    Assert.Equal(6, master.HeightM);
    Assert.Equal(125, master.HeadingDeg);
    Assert.Equal(55, master.HfovDeg);

    var ptz = _world.Placements.Single(placement => placement.CameraId == SecondPtzCameraId);
    Assert.Equal(MapSpaceKey, ptz.SpaceKey);
    Assert.Equal(38.9931085, ptz.Lat, 6);
    Assert.Equal(-76.9380312, ptz.Lon, 6);
    Assert.Equal(5.5, ptz.HeightM);
    Assert.Equal(88, ptz.HeadingDeg);
    Assert.Equal(60, ptz.HfovDeg);
  }

  [Fact]
  public void NothingInTheWorldCopiesACamera()
  {
    // The readings panel the world used to ship repeated the picture of the indoor master as an
    // icon; the camera's picture on the plan is the one thing that stands for it, and the device's
    // diagram hangs under that picture.
    Assert.DoesNotContain(_world.Nodes, node => node.Key.StartsWith("object/telemetry"));
    Assert.DoesNotContain(_world.Types, type => type.Name == "Device telemetry");
  }

  [Fact]
  public void ThePortIsCalmAndTheLevelsArePartOfTheWorld()
  {
    // The demo owns no device, so it writes no alarm. The port's row is CONNECTED, which also
    // replaces the DISCONNECTED an earlier world left behind.
    var port = Assert.Single(_world.States, state => state.NodeKey == PortKey);
    Assert.Equal(["CONNECTED"], port.States);

    Assert.Contains(_world.Rights, right =>
      right.NodeKey == MapSpaceKey && right.Subject == OperatorSubject && right.Action == RightActions.View);

    // The operator acts too: `control` on the floor, the space the master's reference replica
    // stands in — a right on the map above it does not reach a device's node.
    Assert.Contains(_world.Rights, right =>
      right.NodeKey == BuildingSpaceKey && right.Subject == OperatorSubject && right.Action == RightActions.Control);
    Assert.DoesNotContain(_world.Rights, right =>
      right.NodeKey == MapSpaceKey && right.Action == RightActions.Control);
  }

  [Fact]
  public void EveryRegionIsAFeatureOfThePictureUnderIt()
  {
    // The types' regions and the vector backgrounds are drawn from the same geometry (DemoArt),
    // which is what keeps a port inside its socket. These assertions are the seam: a region that
    // stopped coming from DemoArt would drift from the picture with nothing else to notice.
    var rackType = _world.Types.Single(type => type.Key == RackTypeKey);
    for (var slot = 1; slot <= DemoArt.RackSlots; slot += 1)
    {
      Assert.Equal(DemoArt.RackSlot(slot), rackType.Regions.Single(region => region.Key == $"slot-{slot}").Geometry);
    }
    var switchType = _world.Types.Single(type => type.Key == SwitchTypeKey);
    for (var port = 1; port <= DemoArt.PortsPerSwitch; port += 1)
    {
      Assert.Equal(DemoArt.SwitchPort(port), switchType.Regions.Single(region => region.Key == $"port-{port}").Geometry);
    }

    var floorType = _world.Types.Single(type => type.Key == FloorLayoutTypeKey);
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
      "file/camera-icon", FloorPlanFileKey, CamPanelsBackgroundKey,
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
    var file = _world.Files.Single(candidate => candidate.Key == FloorPlanFileKey);
    var text = System.Text.Encoding.UTF8.GetString(file.Content);

    var building = _world.Spaces.Single(space => space.Key == BuildingSpaceKey);
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

    // A device's diagram shown on a canvas names rows the world writes: the canvas, the template,
    // the camera it draws, and the diagram the copy shows when the world registers one.
    var cameraKeys = _world.Placements.Select(placement => placement.CameraId).ToHashSet(StringComparer.Ordinal);
    foreach (var shown in _world.DeviceRepresentations)
    {
      Assert.Contains(shown.CameraKey, cameraKeys);
      Assert.Contains(shown.TypeKey, typeKeys);
      Assert.Contains(shown.CanvasKey, nodeKeys);
      var board = _world.Nodes.Single(node => node.Key == shown.CanvasKey);
      var type = _world.Types.Single(t => t.Key == board.TypeKey);
      Assert.Contains(type.Regions, region => region.Key == shown.RegionKey);
      if (shown.CanonKey is not null)
        Assert.DoesNotContain(shown.CanonKey, nodeKeys);
      if (shown.VisualKey is not null) Assert.Contains(shown.VisualKey, fileKeys);
    }

    Assert.All(_world.DeviceDiagrams, diagram =>
    {
      Assert.Contains(diagram.CameraKey, cameraKeys);
      Assert.Contains(diagram.TypeKey, typeKeys);
      Assert.Contains(diagram.VisualKey, fileKeys);
    });

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
