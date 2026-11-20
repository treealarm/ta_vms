using System.Text.Json;
using Google.Protobuf;
using MapProto = Vms.Map;

namespace web_srv.Tests;

// The map controllers serve the proto JSON. A part's presence must reach the browser as an
// optional field — present only where a device part is — and the fault's source and reset reason
// must keep their wire names.
public class MapJsonContractTests
{
  [Fact]
  public void AMarkerCarriesThePartsPresence()
  {
    using var doc = JsonDocument.Parse(
      JsonFormatter.Default.Format(new MapProto.Marker { Id = "part", Present = false }));

    Assert.True(doc.RootElement.TryGetProperty("present", out var present));
    Assert.False(present.GetBoolean());
  }

  [Fact]
  public void ANodeWithoutAPartLeavesPresenceOut()
  {
    using var doc = JsonDocument.Parse(
      JsonFormatter.Default.Format(new MapProto.Marker { Id = "space" }));

    Assert.False(doc.RootElement.TryGetProperty("present", out _));
  }

  [Fact]
  public void AFigureCarriesThePartsPresence()
  {
    using var doc = JsonDocument.Parse(
      JsonFormatter.Default.Format(new MapProto.Figure { Id = "part", Present = false }));

    Assert.True(doc.RootElement.TryGetProperty("present", out var present));
    Assert.False(present.GetBoolean());
  }

  [Fact]
  public void AFigureWithoutAPartLeavesPresenceOut()
  {
    using var doc = JsonDocument.Parse(
      JsonFormatter.Default.Format(new MapProto.Figure { Id = "box" }));

    Assert.False(doc.RootElement.TryGetProperty("present", out _));
  }

  [Fact]
  public void AFaultKeepsItsSourceAndResetReasonOnTheWire()
  {
    using var doc = JsonDocument.Parse(
      JsonFormatter.Default.Format(new MapProto.Alarm
      {
        Source = "fault",
        CloseReason = "reset",
      }));

    Assert.Equal("fault", doc.RootElement.GetProperty("source").GetString());
    Assert.Equal("reset", doc.RootElement.GetProperty("closeReason").GetString());
  }
}
