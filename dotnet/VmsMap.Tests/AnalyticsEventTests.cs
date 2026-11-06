using System;
using System.Linq;
using VmsMap.Adapter;
using VmsMap.Domain;

namespace VmsMap.Tests;

// A detection becomes one journal row, named by the analytics row's own id. The id is what makes
// the publisher's at-least-once delivery harmless, and the props are what a subscription can
// filter on without a device_node.
public class AnalyticsEventTests
{
  private const string CameraId = "0e9d6f4a-3c1b-4a5f-9e2d-1b7c8a6f5e4d";

  private static AnalyticsEventMessage Message(long detectionId, string? objectId = "obj-7") =>
    new(detectionId, CameraId, "person", 0.87f, objectId,
      new DateTimeOffset(2026, 11, 6, 12, 0, 0, TimeSpan.Zero));

  [Fact]
  public void TheEventIdIsTheDetectionsOwnId()
  {
    Assert.Equal(EventIds.ForAnalytics(42), EventIds.ForAnalytics(42));
    Assert.NotEqual(EventIds.ForAnalytics(42), EventIds.ForAnalytics(43));
    Assert.True(Guid.TryParse(EventIds.ForAnalytics(42), out _));
  }

  [Fact]
  public void TheEventIsAddressedToTheCameraNode()
  {
    var evt = AnalyticsEventSubscriber.ToEvent(CameraObjectId.For(CameraId), Message(42));

    Assert.Equal(CameraObjectId.For(CameraId), evt.object_id);
    Assert.Equal("analytics", evt.event_name);
    Assert.Equal("person", evt.param0);
    Assert.Equal(EventIds.ForAnalytics(42), evt.id);
    Assert.Equal(new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc), evt.timestamp);
  }

  [Fact]
  public void ThePropsCarryWhatASelectorCanFilterOn()
  {
    var props = AnalyticsEventSubscriber.ToEvent(CameraObjectId.For(CameraId), Message(42))
      .extra_props!
      .ToDictionary(p => p.prop_name!, p => p.str_val);

    Assert.Equal(CameraId, props["camera_id"]);
    Assert.Equal("person", props["kind"]);
    Assert.Equal("0.87", props["confidence"]);
    Assert.Equal("obj-7", props["reid_object_id"]);
    Assert.Equal("42", props["detection_id"]);
  }

  [Fact]
  public void AnUnresolvedObjectCarriesNoReidProp()
  {
    var props = AnalyticsEventSubscriber.ToEvent(CameraObjectId.For(CameraId), Message(42, null))
      .extra_props!
      .Select(p => p.prop_name)
      .ToList();

    Assert.DoesNotContain("reid_object_id", props);
  }

  [Fact]
  public void ConfidenceIsNotWrittenInTheHostsCulture()
  {
    var original = System.Globalization.CultureInfo.CurrentCulture;
    try
    {
      System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

      var props = AnalyticsEventSubscriber.ToEvent(CameraObjectId.For(CameraId), Message(42))
        .extra_props!
        .ToDictionary(p => p.prop_name!, p => p.str_val);

      // The router parses this with the invariant culture; a comma here would be an event no
      // threshold selector ever matches.
      Assert.Equal("0.87", props["confidence"]);
    }
    finally
    {
      System.Globalization.CultureInfo.CurrentCulture = original;
    }
  }
}
