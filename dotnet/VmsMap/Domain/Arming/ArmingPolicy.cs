using System;
using System.Collections.Generic;

namespace VmsMap.Domain.Arming
{
  /// <summary>The three modes a node is written with. They are the wire and UI vocabulary, not the
  /// stored value: a container keeps `armed` as "true"/"false" and `inherit` as no property.</summary>
  public static class ArmMode
  {
    public const string Inherit = "inherit";
    public const string Armed = "armed";
    public const string Disarmed = "disarmed";

    /// <summary>The property a container carries. `Inherit` is its absence.</summary>
    public const string ArmedProperty = "armed";

    public static bool IsKnown(string? mode) =>
      string.Equals(mode, Inherit, StringComparison.Ordinal)
      || string.Equals(mode, Armed, StringComparison.Ordinal)
      || string.Equals(mode, Disarmed, StringComparison.Ordinal);

    /// <summary>The property value for a mode, or null when the property has to be removed.</summary>
    public static string? PropertyValue(string mode) => mode switch
    {
      Armed => "true",
      Disarmed => "false",
      _ => null,
    };
  }

  /// <summary>Whether a node takes part in arming, and what may be written to it.</summary>
  public sealed record Armability(bool Armable, bool Configured, IReadOnlyList<string> Modes)
  {
    public static readonly IReadOnlyList<string> ContainerModes =
      [ArmMode.Inherit, ArmMode.Armed, ArmMode.Disarmed];

    public static readonly IReadOnlyList<string> OperatorModes =
      [ArmMode.Armed, ArmMode.Disarmed];

    public static readonly Armability NotArmable = new(false, true, []);

    public static Armability Container() => new(true, true, ContainerModes);

    public static Armability Device(bool configured) => new(true, configured, OperatorModes);

    public bool Accepts(string mode) =>
      Armable && Modes.Contains(mode, StringComparer.Ordinal);
  }

  /// <summary>
  /// Who may be put on guard, and with which modes. One place, because three surfaces ask it — the
  /// node read, the write and the UI — and they must agree.
  /// <para>
  /// A device part takes part through its own scheme: a digital input has an arm_mode, a camera has
  /// its flag, a relay is driven rather than watched and has neither. A container takes part when
  /// devices actually resolve through it: the resolver walks up from a camera's <b>reference</b>
  /// replica, and that replica always hangs under a space (SpatialDb refuses another canvas), so a
  /// container nothing stands through would hold a property nobody reads.
  /// </para>
  /// <para>
  /// `Configured` is a second question: a digital input exists in the tree from the device's first
  /// snapshot, but its mode lives in a VmsCfg settings row an administrator writes. Without one
  /// there is nowhere to keep the mode and no alarm to raise.
  /// </para>
  /// </summary>
  public static class ArmingPolicy
  {
    /// <summary>A part's arming, or null when the part kind has none.</summary>
    public static Armability? ForPart(string partKind, bool configured) =>
      partKind switch
      {
        // The camera's flag is a column of camera_states, so there is no "configured" question.
        DeviceObjectId.DevicePart => Armability.Device(true),
        DeviceObjectId.DigitalInputPart => Armability.Device(configured),
        DeviceObjectId.RelayPart => Armability.NotArmable,
        _ => null,
      };

    /// <summary>A container's arming: a space always, anything else only when devices stand through
    /// it.</summary>
    public static Armability ForContainer(NodeKind kind, bool hasDevicesInSubtree) => kind switch
    {
      NodeKind.Space => Armability.Container(),
      NodeKind.Figure or NodeKind.Object or NodeKind.Diagram when hasDevicesInSubtree =>
        Armability.Container(),
      _ => Armability.NotArmable,
    };
  }
}
