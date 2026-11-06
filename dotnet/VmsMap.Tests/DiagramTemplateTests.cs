using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Tests;

// What a device template may say: which model it is for, and which part each region stands for.
// The service is the only place a template is written, so this is where a binding that nothing can
// resolve has to be refused rather than stored and puzzled over later.
public class DiagramTemplateTests
{
  private static VmsMapDbContext NewDb() => new(
    new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
      .Options);

  private static DiagramTypeRegionDTO Region(string key) => new()
  {
    region_key = key,
    geometry = new DiagramCoordDTO { width = 10, height = 10 },
  };

  private static DiagramTypeRegionDTO Bound(
    string key, string? kind, string? by, string? value) =>
    Region(key) with
    {
      part = new DiagramTypePartDTO { part_kind = kind, match_by = by, match_value = value },
    };

  private static DiagramTypeDTO Type(params DiagramTypeRegionDTO[] regions) => new()
  {
    name = "Camera rear panel",
    source_kind = DeviceObjectId.CameraSource,
    device_manufacturer = "OnvifSim",
    device_model = "Simulated fixed camera",
    auto_attach = true,
    regions = regions.ToList(),
  };

  private static async Task<DiagramTypeDTO> RoundTripAsync(
    DiagramTypeService service, DiagramTypeDTO type)
  {
    await service.UpdateListAsync([type]);
    var saved = await service.GetListByTypeIdsAsync([type.id!]);
    return saved[type.id!];
  }

  [Fact]
  public async Task ATemplateKeepsItsModelAndItsBindings()
  {
    var db = NewDb();
    var service = new DiagramTypeService(db);

    var saved = await RoundTripAsync(service, Type(
      Bound("input", DeviceObjectId.DigitalInputPart, DiagramPartMatch.Token, "input"),
      Bound("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"),
      Region("caption")));

    Assert.Equal(DeviceObjectId.CameraSource, saved.source_kind);
    Assert.Equal("OnvifSim", saved.device_manufacturer);
    Assert.Equal("Simulated fixed camera", saved.device_model);
    Assert.True(saved.auto_attach);

    var input = saved.regions!.Single(r => r.region_key == "input");
    Assert.Equal(DeviceObjectId.DigitalInputPart, input.part!.part_kind);
    Assert.Equal(DiagramPartMatch.Token, input.part.match_by);
    Assert.Equal("input", input.part.match_value);

    // A region that is only a drawing or a caption binds nothing, and that is normal.
    Assert.Null(saved.regions!.Single(r => r.region_key == "caption").part);
  }

  [Fact]
  public async Task AMatchValueIsTrimmedAndTheWayIsLowercased()
  {
    var db = NewDb();
    var service = new DiagramTypeService(db);

    var saved = await RoundTripAsync(service, Type(
      Bound("lamp", " Relay ", "TOKEN ", "  lamp  ")));

    var part = saved.regions!.Single().part!;
    Assert.Equal(DeviceObjectId.RelayPart, part.part_kind);
    Assert.Equal(DiagramPartMatch.Token, part.match_by);
    Assert.Equal("lamp", part.match_value);
  }

  [Theory]
  [InlineData(null, "token", "x", "part kind")]
  [InlineData("digital_input", "colour", "x", "matches by")]
  [InlineData("digital_input", "token", "  ", "says nothing")]
  [InlineData("digital_input", "index", "0", "counts from 1")]
  [InlineData("digital_input", "index", "first", "counts from 1")]
  public async Task ABindingNothingCouldResolveIsRefused(
    string? kind, string? by, string? value, string expected)
  {
    var service = new DiagramTypeService(NewDb());

    var error = await Assert.ThrowsAsync<DiagramTypeConfigException>(
      () => service.UpdateListAsync([Type(Bound("r1", kind, by, value))]));

    Assert.Contains(expected, error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task TwoRegionsCannotClaimTheSamePartByTheSameRule()
  {
    var service = new DiagramTypeService(NewDb());

    var error = await Assert.ThrowsAsync<DiagramTypeConfigException>(
      () => service.UpdateListAsync([Type(
        Bound("a", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"),
        Bound("b", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"))]));

    Assert.Contains("Two regions", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnotherSourceKindIsRefused()
  {
    var service = new DiagramTypeService(NewDb());
    var type = Type();
    type.source_kind = "switch";

    var error = await Assert.ThrowsAsync<DiagramTypeConfigException>(
      () => service.UpdateListAsync([type]));

    Assert.Contains("source kind", error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RemovingARegionTakesItsBindingWithIt()
  {
    var db = NewDb();
    var service = new DiagramTypeService(db);

    var saved = await RoundTripAsync(service, Type(
      Bound("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp"),
      Region("caption")));

    // The key is what a binding names, so a row left behind would bind whatever region takes the
    // key next.
    saved.regions = saved.regions!.Where(r => r.region_key != "lamp").ToList();
    await service.UpdateListAsync([saved]);

    Assert.Empty(db.DiagramTypeParts);
  }

  [Fact]
  public async Task DroppingTheBindingLeavesTheRegionAlone()
  {
    var db = NewDb();
    var service = new DiagramTypeService(db);

    var saved = await RoundTripAsync(service, Type(
      Bound("lamp", DeviceObjectId.RelayPart, DiagramPartMatch.Token, "lamp")));

    saved.regions![0].part = null;
    await service.UpdateListAsync([saved]);

    var reloaded = await RoundTripAsync(service, saved);
    Assert.Single(reloaded.regions!);
    Assert.Null(reloaded.regions![0].part);
    Assert.Empty(db.DiagramTypeParts);
  }

  [Fact]
  public async Task AnOrdinaryMnemoschemeHasNoTemplate()
  {
    var service = new DiagramTypeService(NewDb());
    var type = Type();
    type.source_kind = null;
    type.device_manufacturer = string.Empty;
    type.device_model = string.Empty;
    type.auto_attach = false;

    var saved = await RoundTripAsync(service, type);

    Assert.Null(saved.source_kind);
    Assert.False(saved.auto_attach);
  }
}
