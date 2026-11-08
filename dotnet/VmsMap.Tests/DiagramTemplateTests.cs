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

// Which template a device gets: only the ones that ask for it, the most specific of those, and
// nothing at all when two are equally specific.
public class DiagramModelMatchTests
{
  private static DiagramTypeCandidate Type(
    string id, string manufacturer = "", string model = "", bool autoAttach = true,
    string sourceKind = "camera") => new(id, sourceKind, manufacturer, model, autoAttach);

  [Fact]
  public void AModelMatchWinsOverAWildcard()
  {
    var chosen = DiagramModelMatch.Select(
      [Type("any"), Type("byMaker", manufacturer: "OnvifSim"),
       Type("exact", manufacturer: "OnvifSim", model: "Simulated fixed camera")],
      "camera", "OnvifSim", "Simulated fixed camera");

    Assert.Equal("exact", chosen.TypeId);
    Assert.Null(chosen.Ambiguity);
  }

  [Fact]
  public void TheComparisonIgnoresCaseAndSurroundingSpace()
  {
    var chosen = DiagramModelMatch.Select(
      [Type("exact", manufacturer: " onvifsim ", model: "SIMULATED FIXED CAMERA")],
      "camera", "OnvifSim", "Simulated fixed camera");

    Assert.Equal("exact", chosen.TypeId);
  }

  [Fact]
  public void AnEmptyFieldIsAnyDeviceOfThatKind()
  {
    var chosen = DiagramModelMatch.Select([Type("any")], "camera", "Whatever", "X");

    Assert.Equal("any", chosen.TypeId);
  }

  [Fact]
  public void ATieCreatesNothingAndSaysWhy()
  {
    var chosen = DiagramModelMatch.Select(
      [Type("rear"), Type("front", manufacturer: "OnvifSim")],
      "camera", "OnvifSim", "Simulated fixed camera");

    // "front" is the only one with a manufacturer — it wins, it does not tie.
    Assert.Equal("front", chosen.TypeId);

    var tie = DiagramModelMatch.Select(
      [Type("rear", manufacturer: "OnvifSim", model: "X"),
       Type("front", manufacturer: "OnvifSim", model: "X")],
      "camera", "OnvifSim", "X");

    Assert.Null(tie.TypeId);
    Assert.NotNull(tie.Ambiguity);
  }

  [Fact]
  public void ATemplateThatDoesNotAttachItselfIsNotACandidate()
  {
    var chosen = DiagramModelMatch.Select(
      [Type("manual", manufacturer: "OnvifSim", model: "X", autoAttach: false)],
      "camera", "OnvifSim", "X");

    Assert.Null(chosen.TypeId);
    Assert.Null(chosen.Ambiguity);
  }

  [Fact]
  public void AnotherSourceKindIsAnotherDeviceType()
  {
    var chosen = DiagramModelMatch.Select(
      [Type("switch", sourceKind: "switch")], "camera", "OnvifSim", "X");

    Assert.Null(chosen.TypeId);
  }
}

// What a template's regions resolve to on one device: one way per region, no cascade, and a
// conflict that nobody wins.
public class DiagramTemplateBindingTests
{
  private static DevicePartRef Part(string kind, string token, string name = "") =>
    new(kind, token, name);

  private static RegionPartRule Rule(string region, string kind, string by, string value) =>
    new(region, kind, by, value);

  [Fact]
  public void ATokenFindsItsPart()
  {
    var result = DiagramTemplateBinding.Bind(
      [Rule("lamp", "relay", DiagramPartMatch.Token, "lamp")],
      [Part("relay", "lamp"), Part("relay", "peer")]);

    Assert.Equal("lamp", Assert.Single(result.Bound).Part.Token);
    Assert.Empty(result.Warnings);
  }

  [Fact]
  public void ANameIsTheOperatorsAndIgnoresCase()
  {
    var result = DiagramTemplateBinding.Bind(
      [Rule("lamp", "relay", DiagramPartMatch.Name, " Lamp ")],
      [Part("relay", "relay1", "lamp")]);

    Assert.Equal("relay1", Assert.Single(result.Bound).Part.Token);
  }

  [Fact]
  public void AnEmptyNameMatchesNothing()
  {
    var result = DiagramTemplateBinding.Bind(
      [Rule("lamp", "relay", DiagramPartMatch.Name, "")],
      [Part("relay", "relay1", "")]);

    Assert.Empty(result.Bound);
  }

  [Fact]
  public void TwoPartsWithOneNameBindNeither()
  {
    var result = DiagramTemplateBinding.Bind(
      [Rule("lamp", "relay", DiagramPartMatch.Name, "lamp")],
      [Part("relay", "a", "lamp"), Part("relay", "b", "lamp")]);

    Assert.Empty(result.Bound);
    Assert.NotEmpty(result.Warnings);
  }

  [Fact]
  public void AnIndexCountsFromOneInTokenOrder()
  {
    var result = DiagramTemplateBinding.Bind(
      [Rule("second", "relay", DiagramPartMatch.Index, "2")],
      [Part("relay", "c"), Part("relay", "a"), Part("relay", "b")]);

    // The manifest's order is not a contract; the order of the tokens is.
    Assert.Equal("b", Assert.Single(result.Bound).Part.Token);
  }

  [Fact]
  public void TwoRegionsOnOnePartBindNeither()
  {
    var result = DiagramTemplateBinding.Bind(
      [
        Rule("a", "relay", DiagramPartMatch.Token, "lamp"),
        Rule("b", "relay", DiagramPartMatch.Name, "lamp"),
      ],
      [Part("relay", "lamp", "lamp")]);

    Assert.Empty(result.Bound);
    Assert.NotEmpty(result.Warnings);
  }

  [Fact]
  public void APartThatIsNotThereBindsNothing()
  {
    var result = DiagramTemplateBinding.Bind(
      [Rule("lamp", "relay", DiagramPartMatch.Token, "lamp")],
      [Part("digital_input", "lamp")]);

    // Same token, other kind: a relay region cannot stand for an input.
    Assert.Empty(result.Bound);
  }
}
