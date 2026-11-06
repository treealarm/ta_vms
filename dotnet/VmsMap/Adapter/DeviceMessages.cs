using System;
using System.Collections.Generic;
using System.Text.Json;
using VmsMap.Domain;

namespace VmsMap.Adapter
{
  /// <summary>
  /// One <c>device-event</c> as VmsOnvif publishes it. The payload carries addressing only; what a
  /// contact means is the map's business and is looked up here.
  /// </summary>
  internal sealed record DeviceEventMessage(
    string? SourceKind,
    string? SourceId,
    string? PartKind,
    string? Token,
    JsonElement? Contact,
    JsonElement? Active,
    JsonElement? Previous,
    bool Initialized,
    DateTimeOffset ObservedAt,
    string? Epoch,
    long Seq,
    bool PolarityKnown);

  internal sealed record DeviceSnapshotMessage(
    string? SourceKind,
    string? SourceId,
    bool Reachable,
    DateTimeOffset ObservedAt,
    string? Epoch,
    IReadOnlyList<DeviceSnapshotPartMessage>? Parts);

  internal sealed record DeviceSnapshotPartMessage(
    string? Kind,
    string? Token,
    string? IdleState,
    JsonElement? Contact,
    JsonElement? Active,
    bool Present);

  internal static class DevicePayload
  {
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
      PropertyNameCaseInsensitive = true,
      PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static bool IsDigitalInput(string? partKind) =>
      string.Equals(partKind, DeviceObjectId.DigitalInputPart, StringComparison.Ordinal);

    public static bool IsRelay(string? partKind) =>
      string.Equals(partKind, DeviceObjectId.RelayPart, StringComparison.Ordinal);

    public static string? Text(JsonElement? value) =>
      value is { ValueKind: JsonValueKind.String } ? value.Value.GetString() : null;

    public static bool? Bool(JsonElement? value) => value?.ValueKind switch
    {
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      _ => null,
    };
  }

  /// <summary>
  /// The monotonicity cursor shared by <c>device-event</c> and <c>device-snapshot</c>. A snapshot
  /// has no sequence number, so within one epoch it is always accepted; across epochs the only
  /// comparable fact is the sender's own <c>observed_at</c>, which is why it is stored when it is
  /// accepted rather than compared with the map's clock.
  /// </summary>
  internal static class DeviceCursor
  {
    public static bool Accept(
      Guid? lastEpoch, long lastSeq, DateTime? lastObservedAt,
      string? epoch, long? seq, DateTimeOffset observedAt)
    {
      if (!Guid.TryParse(epoch, out var incoming))
        return false; // without an epoch there is nothing to order by

      if (lastEpoch is null)
        return true;

      if (lastEpoch.Value == incoming)
        return seq is null || seq.Value > lastSeq;

      return lastObservedAt is null || observedAt.UtcDateTime >= lastObservedAt.Value;
    }

    public static void Advance(
      Action<Guid?> setEpoch, Action<long> setSeq, Action<DateTime?> setObservedAt,
      string epoch, long? seq, DateTimeOffset observedAt)
    {
      setEpoch(Guid.Parse(epoch));
      if (seq is not null)
        setSeq(seq.Value);
      setObservedAt(observedAt.UtcDateTime);
    }
  }
}
