using System;
using System.Collections.Generic;
using System.Globalization;
using VmsMap.Domain;

namespace VmsMap.Adapter
{
  /// <summary>
  /// One state a part should be in, and the journal event that says why. Null when the message does
  /// not carry enough to say anything: an input whose polarity is unknown has a state, but it is
  /// neither open nor closed, and invariant 5 forbids guessing it.
  /// </summary>
  internal sealed record DeviceStateChange(
    string State,
    string EventName,
    string Param0,
    IReadOnlyList<ObjExtraPropertyDTO> ExtraProps);

  /// <summary>
  /// Turns the device's own words into the map's dictionary: a contact into SENSOR_OPEN/SENSOR_CLOSED
  /// and a relay coil into RELAY_ACTIVE/RELAY_INACTIVE. The addressing travels in the event's extra
  /// props because the Router has no device_node and could not otherwise tell two sensors apart.
  /// </summary>
  internal sealed class DeviceEventTranslator
  {
    public const string SensorOpen = "SENSOR_OPEN";
    public const string SensorClosed = "SENSOR_CLOSED";
    public const string RelayActive = "RELAY_ACTIVE";
    public const string RelayInactive = "RELAY_INACTIVE";

    public DeviceStateChange? Translate(DeviceEventMessage message)
    {
      if (DevicePayload.IsDigitalInput(message.PartKind))
      {
        var contact = DevicePayload.Text(message.Contact);
        if (contact is not ("open" or "closed"))
          return null;

        return new DeviceStateChange(
          contact == "closed" ? SensorClosed : SensorOpen,
          EventName: "sensor",
          Param0: contact,
          ExtraProps: Props(message, contact: contact, active: null));
      }

      if (DevicePayload.IsRelay(message.PartKind))
      {
        if (DevicePayload.Bool(message.Active) is not { } active)
          return null;

        return new DeviceStateChange(
          active ? RelayActive : RelayInactive,
          EventName: "relay",
          Param0: active ? "active" : "inactive",
          ExtraProps: Props(message, contact: null, active: active));
      }

      return null;
    }

    private static IReadOnlyList<ObjExtraPropertyDTO> Props(
      DeviceEventMessage message, string? contact, bool? active)
    {
      var props = new List<ObjExtraPropertyDTO>
      {
        Text("camera_id", message.SourceId),
        Text("part_kind", message.PartKind),
        Text("token", message.Token),
        Text("polarity_known", message.PolarityKnown ? "true" : "false"),
        new()
        {
          prop_name = "observed_at",
          str_val = message.ObservedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
          visual_type = VisualTypes.DateTime,
        },
      };

      if (contact is not null)
        props.Add(Text("contact", contact));
      if (active is not null)
        props.Add(Text("active", active.Value ? "true" : "false"));

      return props;
    }

    private static ObjExtraPropertyDTO Text(string name, string? value) => new()
    {
      prop_name = name,
      str_val = value ?? string.Empty,
    };
  }
}
