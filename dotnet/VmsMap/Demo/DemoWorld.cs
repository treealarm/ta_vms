using System.Collections.Generic;
using Vms.Map;
using VmsMap.Domain;

namespace VmsMap.Demo
{
  /// A point in the coordinates of the canvas it is drawn on: a plan point on that plan, or a
  /// latitude/longitude on a map.
  public readonly record struct DemoPoint(double Lat, double Lon);

  /// A rectangle, in shares of a canvas for a region or in pixels for a diagram's own paper.
  public readonly record struct DemoRect(double Left, double Top, double Width, double Height, double Rotation = 0);

  /// A file the world brings: a picture of a plan, a diagram type's background or an icon. The
  /// bytes are either an embedded asset under Demo/Assets or drawn by `DemoArt`, so the world ships
  /// with the code and never depends on a file on the instance's disk.
  public sealed record DemoFile(string Key, byte[] Content, string Mime)
  {
    /// A picture that is an embedded raster: the plans, and the two icons that stand on the floor —
    /// the server room's and the readings panel's.
    public static DemoFile Asset(string key, string asset, string mime) =>
      new(key, DemoAssets.Read(asset), mime);
  }

  /// Where a plan image lies in its space: three corners in the parent canvas's coordinates.
  public sealed record DemoPlan(string FileKey, uint WidthPx, uint HeightPx,
    DemoPoint TopLeft, DemoPoint TopRight, DemoPoint BottomLeft);

  /// A space: a map, or a plan with a picture of its own. Its footprint is where it stands in its
  /// parent's canvas.
  public sealed record DemoSpace(
    string Key,
    string Name,
    string? ParentKey,
    SpaceKind Kind,
    double South,
    double West,
    double North,
    double East,
    double ElevationM = 0,
    int Ordinal = 0,
    bool RealCoordinates = false,
    IReadOnlyList<DemoPoint>? Footprint = null,
    DemoPlan? Plan = null);

  /// The device part a region of a template stands for: its kind, the way it is found, and what to
  /// look for.
  public sealed record DemoRegionPart(string PartKind, string MatchBy, string MatchValue);

  public sealed record DemoRegion(string Key, DemoRect Geometry,
    IReadOnlyDictionary<string, string>? Styles = null, DemoRegionPart? Part = null);

  /// A diagram type: the picture behind a diagram of it and the regions its children may take.
  /// The last four make it a template for one model of a real device as well; the demo uses them
  /// for a camera's rear panel, which is how a device's diagram appears on its own.
  public sealed record DemoDiagramType(string Key, string Name, string? BackgroundKey,
    IReadOnlyList<DemoRegion> Regions,
    string? SourceKind = null,
    string DeviceManufacturer = "",
    string DeviceModel = "",
    bool AutoAttach = false);

  /// A node of the tree. Under a space it carries a point (`At`) drawn on that space; under a
  /// diagram it takes a region (`RegionKey`) or a rectangle of its own. Every node that is a
  /// mnemoscheme has a `TypeKey` and a canvas (`Canvas`).
  public sealed record DemoNode(
    string Key,
    string Name,
    string? ParentKey,
    string? TypeKey = null,
    string? RegionKey = null,
    DemoRect? Canvas = null,
    DemoPoint? At = null,
    string? IconKey = null,
    string? ZoomLevel = null,
    // How the node is drawn where it is not its own canvas — a diagram standing on a plan is a
    // point, and without a picture it is a plain dot nobody would take for an entrance.
    string? VisualKey = null,
    int? VisualWidth = null,
    int? VisualHeight = null,
    // A replica of a camera: the node shows that camera's states and values (`objects.owner_id`),
    // and is skipped, and reported, while the deployment does not have the camera. It gets no
    // camera_placement — a diagram's canvas is not a space.
    string? OwnerCameraId = null);

  /// A scale layer: zoom levels at which the nodes that name it are drawn.
  public sealed record DemoLevel(string Name, int Min, int Max);

  /// A camera from the installation, placed where the world wants it. A camera the deployment
  /// does not have yet is skipped and reported; the world without cameras is still valid.
  public sealed record DemoPlacement(
    string CameraId,
    string SpaceKey,
    double Lat,
    double Lon,
    double HeightM = 3,
    double HeadingDeg = 0,
    double HfovDeg = 60,
    double RangeM = 30,
    bool IsReference = true);

  /// A landmark a calibration is made against: a named, coloured point of the world a camera can
  /// see. It is a node of the space it stands in, and the marks and observations are made on it.
  public sealed record DemoLandmark(
    string Key,
    string Name,
    string SpaceKey,
    double Lat,
    double Lon,
    double HeightM = 0);

  /// The foot of a person standing on a landmark, as the master's picture shows it: x and y in
  /// shares of the frame, from the top left — what the ground calibration stores for a fixed
  /// camera.
  public sealed record DemoGroundMark(string CameraKey, string LandmarkKey, double X, double Y);

  /// Where a PTZ camera's head points to centre a landmark, in the camera's own ONVIF generic
  /// units; `Zoom` is the framing zoom at that distance, for a pair that uses one.
  public sealed record DemoPtzObservation(
    string CameraKey, string LandmarkKey, double Pan, double Tilt, double? Zoom = null);

  /// A state row on a node. The demo writes no alarm: its one row is `CONNECTED` on a switch
  /// port, replacing the `DISCONNECTED` earlier worlds installed.
  public sealed record DemoState(string NodeKey, params string[] States);

  /// A right on a node. The world grants `view` to the deployment's `user` role at its root.
  public sealed record DemoRight(string NodeKey, string Subject, string Action);

  /// The whole demo world, described without a single id: the seeder derives and records them.
  /// It is code, not a dump, so a rack's ports and a switch's regions are loops in DemoWorlds.
  public sealed record DemoWorld(
    string Key,
    int Version,
    IReadOnlyList<DemoFile> Files,
    IReadOnlyList<DemoDiagramType> Types,
    IReadOnlyList<DemoSpace> Spaces,
    IReadOnlyList<DemoNode> Nodes,
    IReadOnlyList<DemoLevel> Levels,
    IReadOnlyList<DemoPlacement> Placements,
    IReadOnlyList<DemoLandmark> Landmarks,
    IReadOnlyList<DemoGroundMark> GroundMarks,
    IReadOnlyList<DemoPtzObservation> PtzObservations,
    IReadOnlyList<DemoState> States,
    IReadOnlyList<DemoRight> Rights);
}
