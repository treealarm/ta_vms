using System;
using System.Collections.Generic;

namespace VmsMap.Domain
{
  // What kind of node a tree row is. The tree has no type column: a node is what its side row
  // says it is. The kinds exist for the rights UI, which offers a different action set for a
  // camera than for a building.
  public enum NodeKind
  {
    Object,
    Space,
    Landmark,
    Camera,
    /// One part of a device -- a digital input or a relay -- as a node of its own.
    Part,
    Diagram,
    Figure,
  }

  // The open action set, one table for the whole system. `view` is what puts a node on the map and
  // in the tree at all; `edit` is what changes it. A camera adds the five flags the old camera ACL
  // had, so moving an installation onto the tree keeps every permission it had. `acknowledge` is
  // the operator's handling of an alarm: seeing that one exists is `view`, dealing with it is this.
  public static class RightActions
  {
    public const string View = "view";
    public const string Edit = "edit";
    public const string ViewLive = "view_live";
    public const string ViewArchive = "view_archive";
    public const string Ptz = "ptz";
    public const string Control = "control";
    public const string Export = "export";
    // Taking responsibility for an alarm — acknowledging it and closing it afterwards. Closing is
    // the continuation of the same handling, so it carries the same right rather than a second one.
    public const string Acknowledge = "acknowledge";

    // The admin role has every action without a row; this is the set it is granted.
    public static readonly IReadOnlyList<string> All =
      [View, Edit, ViewLive, ViewArchive, Ptz, Control, Export, Acknowledge];

    private static readonly IReadOnlyList<string> Common = [View, Edit];

    private static readonly IReadOnlyList<string> CameraActions =
      [View, Edit, ViewLive, ViewArchive, Ptz, Control, Export, Acknowledge];

    // A part is watched and operated -- a relay switched, a sensor's alarm acknowledged -- but it
    // is not a camera: no archive, no PTZ.
    private static readonly IReadOnlyList<string> PartActions =
      [View, Edit, ViewLive, Control, Acknowledge];

    public static IReadOnlyList<string> For(NodeKind kind) => kind switch
    {
      NodeKind.Camera => CameraActions,
      NodeKind.Part => PartActions,
      _ => Common,
    };

    public static bool Applies(NodeKind kind, string action) =>
      For(kind).Contains(action, StringComparer.Ordinal);

    public static string Name(NodeKind kind) => kind switch
    {
      NodeKind.Space => "space",
      NodeKind.Landmark => "landmark",
      NodeKind.Camera => "camera",
      NodeKind.Part => "part",
      NodeKind.Diagram => "diagram",
      NodeKind.Figure => "figure",
      _ => "object",
    };
  }
}
