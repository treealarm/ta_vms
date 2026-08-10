using AnalyticsServiceReference;
using System.ServiceModel;
using System.ServiceModel.Channels;

namespace OnvifLib
{
  /// <summary>One name/value parameter of an analytics module or rule.</summary>
  public record OnvifSimpleItem(string Name, string Value);

  /// <summary>
  /// A configured analytics module or rule. <paramref name="ElementItemsXml"/> carries the
  /// structured parameters (polygons, line segments, schedules) as raw XML: they have no fixed
  /// schema across vendors, and round-tripping them untouched is the only way a Modify call can
  /// avoid silently dropping whatever it did not understand.
  /// </summary>
  public record OnvifAnalyticsModule(
    string Name,
    string Type,
    IReadOnlyList<OnvifSimpleItem> SimpleItems,
    IReadOnlyList<string> ElementItemsXml);

  /// <summary>One parameter a module/rule type accepts, as advertised by the camera.</summary>
  public record OnvifParameterDescription(string Name, string Type);

  /// <summary>A module or rule type the camera says it supports, with its parameter list.</summary>
  public record OnvifModuleDescription(
    string Type,
    string? MaxInstances,
    IReadOnlyList<OnvifParameterDescription> Parameters);

  /// <summary>
  /// ONVIF ver20 Analytics: the modules a camera runs over a VideoAnalyticsConfiguration and the
  /// rules layered on top of them. Both live at the same XAddr but on two different ports
  /// (AnalyticsEnginePort for modules, RuleEnginePort for rules), so this wraps both.
  ///
  /// Everything here is keyed by a VideoAnalyticsConfiguration token, which the media service
  /// hands out — see MediaService.GetAnalyticsConfigurationsAsync.
  /// </summary>
  public class AnalyticsService : OnvifServiceBase, IOnvifServiceFactory<AnalyticsService>
  {
    public const string WSDL_V20 = "http://www.onvif.org/ver20/analytics/wsdl";

    private AnalyticsEnginePortClient? _modulesClient;
    private RuleEnginePortClient? _rulesClient;

    protected AnalyticsService(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null) :
      base(url, binding, username, password, profile, tokenFactory, logger)
    {
    }

    public static string[] GetSupportedWsdls()
    {
      return new[] { WSDL_V20 };
    }

    public static async Task<AnalyticsService?> CreateAsync(string url, CustomBinding binding, string username, string password, string profile, Func<SecurityToken>? tokenFactory = null, IOnvifLogger? logger = null)
    {
      var instance = new AnalyticsService(url, binding, username, password, profile, tokenFactory, logger);
      await instance.InitializeAsync();
      return instance;
    }

    protected async override Task InitializeAsync()
    {
      await base.InitializeAsync();

      _modulesClient = _onvifClientFactory.CreateClient<AnalyticsEnginePortClient, AnalyticsEnginePort>(
        new EndpointAddress(_url), _binding, _username, _password);
      await _modulesClient.OpenAsync();

      _rulesClient = _onvifClientFactory.CreateClient<RuleEnginePortClient, RuleEnginePort>(
        new EndpointAddress(_url), _binding, _username, _password);
      await _rulesClient.OpenAsync();
    }

    // ---- Analytics modules -------------------------------------------------------------------

    public async Task<IReadOnlyList<OnvifModuleDescription>> GetSupportedAnalyticsModulesAsync(string configurationToken)
    {
      if (_modulesClient == null) return [];
      var resp = await _modulesClient.GetSupportedAnalyticsModulesAsync(
        new GetSupportedAnalyticsModulesRequest { ConfigurationToken = configurationToken });
      return ToDescriptions(resp.SupportedAnalyticsModules?.AnalyticsModuleDescription);
    }

    public async Task<IReadOnlyList<OnvifAnalyticsModule>> GetAnalyticsModulesAsync(string configurationToken)
    {
      if (_modulesClient == null) return [];
      var resp = await _modulesClient.GetAnalyticsModulesAsync(
        new GetAnalyticsModulesRequest { ConfigurationToken = configurationToken });
      return ToModules(resp.AnalyticsModule);
    }

    public async Task CreateAnalyticsModuleAsync(string configurationToken, OnvifAnalyticsModule module)
    {
      if (_modulesClient == null) return;
      await _modulesClient.CreateAnalyticsModulesAsync(new CreateAnalyticsModulesRequest
      {
        ConfigurationToken = configurationToken,
        AnalyticsModule = [ToConfig(module)],
      });
    }

    public async Task ModifyAnalyticsModuleAsync(string configurationToken, OnvifAnalyticsModule module)
    {
      if (_modulesClient == null) return;
      await _modulesClient.ModifyAnalyticsModulesAsync(new ModifyAnalyticsModulesRequest
      {
        ConfigurationToken = configurationToken,
        AnalyticsModule = [ToConfig(module)],
      });
    }

    public async Task DeleteAnalyticsModuleAsync(string configurationToken, string moduleName)
    {
      if (_modulesClient == null) return;
      await _modulesClient.DeleteAnalyticsModulesAsync(new DeleteAnalyticsModulesRequest
      {
        ConfigurationToken = configurationToken,
        AnalyticsModuleName = [moduleName],
      });
    }

    // ---- Rules -------------------------------------------------------------------------------

    public async Task<IReadOnlyList<OnvifModuleDescription>> GetSupportedRulesAsync(string configurationToken)
    {
      if (_rulesClient == null) return [];
      var resp = await _rulesClient.GetSupportedRulesAsync(
        new GetSupportedRulesRequest { ConfigurationToken = configurationToken });
      return ToDescriptions(resp.SupportedRules?.RuleDescription);
    }

    public async Task<IReadOnlyList<OnvifAnalyticsModule>> GetRulesAsync(string configurationToken)
    {
      if (_rulesClient == null) return [];
      var resp = await _rulesClient.GetRulesAsync(
        new GetRulesRequest { ConfigurationToken = configurationToken });
      return ToModules(resp.Rule);
    }

    public async Task CreateRuleAsync(string configurationToken, OnvifAnalyticsModule rule)
    {
      if (_rulesClient == null) return;
      await _rulesClient.CreateRulesAsync(new CreateRulesRequest
      {
        ConfigurationToken = configurationToken,
        Rule = [ToConfig(rule)],
      });
    }

    public async Task ModifyRuleAsync(string configurationToken, OnvifAnalyticsModule rule)
    {
      if (_rulesClient == null) return;
      await _rulesClient.ModifyRulesAsync(new ModifyRulesRequest
      {
        ConfigurationToken = configurationToken,
        Rule = [ToConfig(rule)],
      });
    }

    public async Task DeleteRuleAsync(string configurationToken, string ruleName)
    {
      if (_rulesClient == null) return;
      await _rulesClient.DeleteRulesAsync(new DeleteRulesRequest
      {
        ConfigurationToken = configurationToken,
        RuleName = [ruleName],
      });
    }

    // ---- Mapping -----------------------------------------------------------------------------

    private static IReadOnlyList<OnvifAnalyticsModule> ToModules(Config[]? configs)
    {
      if (configs == null) return [];
      var result = new List<OnvifAnalyticsModule>(configs.Length);
      foreach (var c in configs)
      {
        if (c == null) continue;
        result.Add(new OnvifAnalyticsModule(
          c.Name ?? string.Empty,
          // The type is a qualified name; only the local part identifies the module across
          // vendors, and it is what GetSupported* advertises too.
          c.Type?.Name ?? string.Empty,
          (c.Parameters?.SimpleItem ?? [])
            .Where(i => i?.Name != null)
            .Select(i => new OnvifSimpleItem(i.Name, i.Value ?? string.Empty))
            .ToList(),
          (c.Parameters?.ElementItem ?? [])
            .Where(i => i?.Any != null)
            .Select(i => i.Any.OuterXml)
            .ToList()));
      }
      return result;
    }

    private static IReadOnlyList<OnvifModuleDescription> ToDescriptions(ConfigDescription[]? descriptions)
    {
      if (descriptions == null) return [];
      var result = new List<OnvifModuleDescription>(descriptions.Length);
      foreach (var d in descriptions)
      {
        if (d == null) continue;
        result.Add(new OnvifModuleDescription(
          d.Name?.Name ?? string.Empty,
          d.maxInstances,
          (d.Parameters?.SimpleItemDescription ?? [])
            .Where(p => p?.Name != null)
            .Select(p => new OnvifParameterDescription(p.Name, p.Type?.Name ?? string.Empty))
            .ToList()));
      }
      return result;
    }

    private static Config ToConfig(OnvifAnalyticsModule module)
    {
      var elementItems = new List<ItemListElementItem>();
      foreach (var xml in module.ElementItemsXml)
      {
        if (string.IsNullOrWhiteSpace(xml)) continue;
        var doc = new System.Xml.XmlDocument();
        try
        {
          doc.LoadXml(xml);
        }
        catch (System.Xml.XmlException)
        {
          // Refusing the whole update because one structured parameter came back malformed would
          // make the module uneditable; dropping just that item at least keeps the rest writable.
          continue;
        }
        if (doc.DocumentElement == null) continue;
        elementItems.Add(new ItemListElementItem
        {
          Name = doc.DocumentElement.LocalName,
          Any = doc.DocumentElement,
        });
      }

      return new Config
      {
        Name = module.Name,
        // Namespace-less: cameras match on the local name, and the ver20 analytics namespace
        // varies between the ONVIF standard modules and vendor-specific ones.
        Type = new System.Xml.XmlQualifiedName(module.Type),
        Parameters = new ItemList
        {
          SimpleItem = module.SimpleItems
            .Select(i => new ItemListSimpleItem { Name = i.Name, Value = i.Value })
            .ToArray(),
          ElementItem = elementItems.ToArray(),
        },
      };
    }
  }
}
