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
  // The fixtures these tests share.
  private const string Manufacturer = "OnvifSim";
  private const string Model = "Simulated fixed camera";
  private const string DigitalInputPart = "digital_input";
  private const string InputToken = "input";
  private const string LampToken = "lamp";
  private const string RelayPart = "relay";
  private const string CaptionRegion = "caption";
  private const string PlaceholderValue = "x";
  private const string CountsFromOne = "counts from 1";

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
    device_manufacturer = Manufacturer,
    device_model = Model,
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
      Bound(InputToken, DeviceObjectId.DigitalInputPart, DiagramPartMatch.Token, InputToken),
      Bound(LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken),
      Region(CaptionRegion)));

    Assert.Equal(DeviceObjectId.CameraSource, saved.source_kind);
    Assert.Equal(Manufacturer, saved.device_manufacturer);
    Assert.Equal(Model, saved.device_model);
    Assert.True(saved.auto_attach);

    var input = saved.regions!.Single(r => r.region_key == InputToken);
    Assert.Equal(DeviceObjectId.DigitalInputPart, input.part!.part_kind);
    Assert.Equal(DiagramPartMatch.Token, input.part.match_by);
    Assert.Equal(InputToken, input.part.match_value);

    // A region that is only a drawing or a caption binds nothing, and that is normal.
    Assert.Null(saved.regions!.Single(r => r.region_key == CaptionRegion).part);
  }

  [Fact]
  public async Task AMatchValueIsTrimmedAndTheWayIsLowercased()
  {
    var db = NewDb();
    var service = new DiagramTypeService(db);

    var saved = await RoundTripAsync(service, Type(
      Bound(LampToken, " Relay ", "TOKEN ", "  lamp  ")));

    var part = saved.regions!.Single().part!;
    Assert.Equal(DeviceObjectId.RelayPart, part.part_kind);
    Assert.Equal(DiagramPartMatch.Token, part.match_by);
    Assert.Equal(LampToken, part.match_value);
  }

  [Theory]
  [InlineData(null, DiagramPartMatch.Token, PlaceholderValue, "part kind")]
  [InlineData(DigitalInputPart, "colour", PlaceholderValue, "matches by")]
  [InlineData(DigitalInputPart, DiagramPartMatch.Token, "  ", "says nothing")]
  [InlineData(DigitalInputPart, DiagramPartMatch.Index, "0", CountsFromOne)]
  [InlineData(DigitalInputPart, DiagramPartMatch.Index, "first", CountsFromOne)]
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
        Bound("a", DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken),
        Bound("b", DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken))]));

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
      Bound(LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken),
      Region(CaptionRegion)));

    // The key is what a binding names, so a row left behind would bind whatever region takes the
    // key next.
    saved.regions = saved.regions!.Where(r => r.region_key != LampToken).ToList();
    await service.UpdateListAsync([saved]);

    Assert.Empty(db.DiagramTypeParts);
  }

  [Fact]
  public async Task DroppingTheBindingLeavesTheRegionAlone()
  {
    var db = NewDb();
    var service = new DiagramTypeService(db);

    var saved = await RoundTripAsync(service, Type(
      Bound(LampToken, DeviceObjectId.RelayPart, DiagramPartMatch.Token, LampToken)));

    var region = Assert.Single(saved.regions!);
    region.part = null;
    await service.UpdateListAsync([saved]);

    var reloaded = await RoundTripAsync(service, saved);
    Assert.Null(Assert.Single(reloaded.regions!).part);
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
  // The fixtures these tests share.
  private const string Manufacturer = "OnvifSim";
  private const string Model = "Simulated fixed camera";
  private const string CameraSource = "camera";
  private const string AnyTypeId = "any";
  private const string ExactTypeId = "exact";
  private const string RearTypeId = "rear";
  private const string FrontTypeId = "front";
  private const string SwitchSource = "switch";
  private const string UnknownModel = "X";

  private static DiagramTypeCandidate Type(
    string id, string manufacturer = "", string model = "", bool autoAttach = true,
    string sourceKind = CameraSource) => new(id, sourceKind, manufacturer, model, autoAttach);

  [Fact]
  public void AModelMatchWinsOverAWildcard()
  {
    var chosen = DiagramModelMatch.Select(
      [Type(AnyTypeId), Type("byMaker", manufacturer: Manufacturer),
       Type(ExactTypeId, manufacturer: Manufacturer, model: Model)],
      CameraSource, Manufacturer, Model);

    Assert.Equal(ExactTypeId, chosen.TypeId);
    Assert.Null(chosen.Ambiguity);
  }

  [Fact]
  public void TheComparisonIgnoresCaseAndSurroundingSpace()
  {
    var chosen = DiagramModelMatch.Select(
      [Type(ExactTypeId, manufacturer: " onvifsim ", model: "SIMULATED FIXED CAMERA")],
      CameraSource, Manufacturer, Model);

    Assert.Equal(ExactTypeId, chosen.TypeId);
  }

  [Fact]
  public void AnEmptyFieldIsAnyDeviceOfThatKind()
  {
    var chosen = DiagramModelMatch.Select([Type(AnyTypeId)], CameraSource, "Whatever", UnknownModel);

    Assert.Equal(AnyTypeId, chosen.TypeId);
  }

  [Fact]
  public void ATieCreatesNothingAndSaysWhy()
  {
    var chosen = DiagramModelMatch.Select(
      [Type(RearTypeId), Type(FrontTypeId, manufacturer: Manufacturer)],
      CameraSource, Manufacturer, Model);

    // "front" is the only one with a manufacturer — it wins, it does not tie.
    Assert.Equal(FrontTypeId, chosen.TypeId);

    var tie = DiagramModelMatch.Select(
      [Type(RearTypeId, manufacturer: Manufacturer, model: UnknownModel),
       Type(FrontTypeId, manufacturer: Manufacturer, model: UnknownModel)],
      CameraSource, Manufacturer, UnknownModel);

    Assert.Null(tie.TypeId);
    Assert.NotNull(tie.Ambiguity);
  }

  [Fact]
  public void ATemplateThatDoesNotAttachItselfIsNotACandidate()
  {
    var chosen = DiagramModelMatch.Select(
      [Type("manual", manufacturer: Manufacturer, model: UnknownModel, autoAttach: false)],
      CameraSource, Manufacturer, UnknownModel);

    Assert.Null(chosen.TypeId);
    Assert.Null(chosen.Ambiguity);
  }

  [Fact]
  public void AnotherSourceKindIsAnotherDeviceType()
  {
    var chosen = DiagramModelMatch.Select(
      [Type(SwitchSource, sourceKind: SwitchSource)], CameraSource, Manufacturer, UnknownModel);

    Assert.Null(chosen.TypeId);
  }
}

// What a template's regions resolve to on one device: one way per region, no cascade, and a
// conflict that nobody wins.
public class DiagramTemplateBindingTests
{
  // The fixtures these tests share.
  private const string LampToken = "lamp";
  private const string RelayPart = "relay";
  private const string RelayOneToken = "relay1";
  private const string TokenA = "a";
  private const string TokenB = "b";

  private static DevicePartRef Part(string kind, string token, string name = "") =>
    new(kind, token, name);

  private static RegionPartRule Rule(string region, string kind, string by, string value) =>
    new(region, kind, by, value);

  [Fact]
  public void ATokenFindsItsPart()
  {
    var result = DiagramTemplateBinding.Bind(
      [Rule(LampToken, RelayPart, DiagramPartMatch.Token, LampToken)],
      [Part(RelayPart, LampToken), Part(RelayPart, "peer")]);

    Assert.Equal(LampToken, Assert.Single(result.Bound).Part.Token);
    Assert.Empty(result.Warnings);
  }

  [Fact]
  public void ANameIsTheOperatorsAndIgnoresCase()
  {
    var result = DiagramTemplateBinding.Bind(
      [Rule(LampToken, RelayPart, DiagramPartMatch.Name, " Lamp ")],
      [Part(RelayPart, RelayOneToken, LampToken)]);

    Assert.Equal(RelayOneToken, Assert.Single(result.Bound).Part.Token);
  }

  [Fact]
  public void AnEmptyNameMatchesNothing()
  {
    var result = DiagramTemplateBinding.Bind(
      [Rule(LampToken, RelayPart, DiagramPartMatch.Name, "")],
      [Part(RelayPart, RelayOneToken, "")]);

    Assert.Empty(result.Bound);
  }

  [Fact]
  public void TwoPartsWithOneNameBindNeither()
  {
    var result = DiagramTemplateBinding.Bind(
      [Rule(LampToken, RelayPart, DiagramPartMatch.Name, LampToken)],
      [Part(RelayPart, TokenA, LampToken), Part(RelayPart, TokenB, LampToken)]);

    Assert.Empty(result.Bound);
    Assert.NotEmpty(result.Warnings);
  }

  [Fact]
  public void AnIndexCountsFromOneInTokenOrder()
  {
    var result = DiagramTemplateBinding.Bind(
      [Rule("second", RelayPart, DiagramPartMatch.Index, "2")],
      [Part(RelayPart, "c"), Part(RelayPart, TokenA), Part(RelayPart, TokenB)]);

    // The manifest's order is not a contract; the order of the tokens is.
    Assert.Equal(TokenB, Assert.Single(result.Bound).Part.Token);
  }

  [Fact]
  public void TwoRegionsOnOnePartBindNeither()
  {
    var result = DiagramTemplateBinding.Bind(
      [
        Rule(TokenA, RelayPart, DiagramPartMatch.Token, LampToken),
        Rule(TokenB, RelayPart, DiagramPartMatch.Name, LampToken),
      ],
      [Part(RelayPart, LampToken, LampToken)]);

    Assert.Empty(result.Bound);
    Assert.NotEmpty(result.Warnings);
  }

  [Fact]
  public void APartThatIsNotThereBindsNothing()
  {
    var result = DiagramTemplateBinding.Bind(
      [Rule(LampToken, RelayPart, DiagramPartMatch.Token, LampToken)],
      [Part("digital_input", LampToken)]);

    // Same token, other kind: a relay region cannot stand for an input.
    Assert.Empty(result.Bound);
  }
}
