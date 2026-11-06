using System.Text.Json;
using OnvifLib;

namespace VmsOnvif;

/// <summary>
/// The camera's configuration, as discovery reads it: the relays and inputs it says it has. Separate
/// from the settings service's public surface so discovery can be tested without an ONVIF client.
/// </summary>
public interface IDeviceIoReader
{
  Task<List<OnvifRelayOutput>> GetRelayOutputsAsync(string cameraId, CancellationToken ct);
  Task<List<OnvifDigitalInput>> GetDigitalInputsAsync(string cameraId, CancellationToken ct);
}

/// <summary>One part of a device snapshot. <c>contact</c> is an input's, <c>active</c> a relay's.</summary>
public sealed record DeviceSnapshotPart(
  string Kind,
  string Token,
  string? IdleState,
  object? Contact,
  object? Active,
  bool Present);

/// <summary>
/// A camera's whole I/O manifest and current state in one message. One per camera, not one per
/// part: at a hundred thousand cameras that difference is three messages a second against thirteen
/// hundred. The parts list is what the map creates canonical nodes from, so a part that never
/// sends an event still gets a node.
/// </summary>
public sealed record DeviceSnapshotPayload(
  string SourceKind,
  string SourceId,
  bool Reachable,
  DateTimeOffset ObservedAt,
  IReadOnlyList<DeviceSnapshotPart> Parts);

/// <summary>Publishes a device snapshot. Discovery owns the content; the publisher owns the wire.</summary>
public interface IDeviceSnapshotPublisher
{
  Task PublishAsync(DeviceSnapshotPayload snapshot);
}

internal static class DeviceJson
{
  // DaprClient serializes with web defaults (camelCase) while the subscribers read snake_case, and
  // the mismatch does not fail — it deserializes into nulls. Payloads go out as pre-built
  // JsonElements with the right names instead of relying on the client's serializer.
  public static readonly JsonSerializerOptions Options =
    new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

  public static JsonElement Element(object value) =>
    JsonSerializer.SerializeToElement(value, Options);

  public static string KindName(DeviceIoKind kind) =>
    kind == DeviceIoKind.Relay ? "relay" : "digital_input";
}
