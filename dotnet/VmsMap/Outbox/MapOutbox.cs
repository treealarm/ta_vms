using System;

namespace VmsMap.Outbox
{
  /// <summary>
  /// One notification waiting to be published. The row is written in the same transaction as the
  /// event it announces, which is what closes the gap between "the event is committed" and "the
  /// notification went out": a crash in between leaves the row for the publisher to find.
  /// </summary>
  public record DBMapOutbox
  {
    public long id { get; set; }
    public string topic { get; set; } = null!;
    public string payload { get; set; } = null!;
    public DateTime created_at { get; set; }
    public DateTime? published_at { get; set; }
    public int attempts { get; set; }
    public string? last_error { get; set; }
  }

  public static class MapTopics
  {
    /// <summary>An event entered the journal and may start a subscription.</summary>
    public const string EventCreated = "map-event-created";

    /// <summary>A container's arming changed and the sensors already in their trigger position
    /// must be re-checked. Written in the same transaction as the property, so a crash between the
    /// commit and the re-check loses nothing.</summary>
    public const string ArmingChanged = "map-arming-changed";
  }
}
