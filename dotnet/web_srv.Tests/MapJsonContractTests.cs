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
  public void ADiagramLeavesItsContentFieldsOutWhenTheyAreNotSet()
  {
    using var doc = JsonDocument.Parse(
      JsonFormatter.Default.Format(new MapProto.Diagram { Id = "diagram" }));

    Assert.False(doc.RootElement.TryGetProperty("dgrType", out _));
    Assert.False(doc.RootElement.TryGetProperty("fileId", out _));
  }

  [Fact]
  public void ADiagramKeepsAnEmptyContentFieldOnTheWire()
  {
    using var doc = JsonDocument.Parse(
      JsonFormatter.Default.Format(new MapProto.Diagram { Id = "diagram", DgrType = "", FileId = "" }));

    Assert.True(doc.RootElement.TryGetProperty("dgrType", out var type));
    Assert.Equal(string.Empty, type.GetString());
    Assert.True(doc.RootElement.TryGetProperty("fileId", out var file));
    Assert.Equal(string.Empty, file.GetString());
  }

  [Fact]
  public void AParsedDiagramTellsAnAbsentContentFieldFromAnEmptyOne()
  {
    var absent = JsonParser.Default.Parse<MapProto.Diagram>("{\"id\":\"diagram\"}");
    Assert.False(absent.HasDgrType);
    Assert.False(absent.HasFileId);

    // A copy's place leaves the diagram's type alone; an empty value is a request to clear it.
    var empty = JsonParser.Default.Parse<MapProto.Diagram>(
      "{\"id\":\"diagram\",\"dgrType\":\"\",\"fileId\":\"\"}");
    Assert.True(empty.HasDgrType);
    Assert.True(empty.HasFileId);
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
