using Grpc.Core;
using OnvifLib;
using Vms.Onvif;
using VmsOnvif.Onvif;

namespace VmsOnvif;

/// <summary>
/// Profile M handlers: the camera's own metadata track and the analytics engine feeding it.
/// </summary>
/// <remarks>
/// Reads answer with an empty list rather than throwing when a camera has no analytics service —
/// most cameras in a deployment do not, and the UI must tell that apart from a fault. Writes act on
/// the device and report failure in <c>error</c>, so the frontend can show the camera's own wording
/// instead of a transport error.
/// </remarks>
public partial class OnvifServiceImpl
{
  // ── Metadata configurations ───────────────────────────────────────────────

  public override async Task<MetadataConfigsResponse> GetMetadataConfigs(
    CameraRequest request, ServerCallContext context)
  {
    var response = new MetadataConfigsResponse();
    try
    {
      var configs = await settingsService.GetMetadataConfigsAsync(
        request.SourceId, context.CancellationToken);
      foreach (var c in configs)
      {
        var proto = new MetadataConfig
        {
          Token           = c.Token,
          Name            = c.Name,
          Analytics       = c.Analytics,
          PtzStatus       = c.PtzStatus,
          Events          = c.Events,
          GeoLocation     = c.GeoLocation,
          ShapePolygon    = c.ShapePolygon,
          SessionTimeout  = c.SessionTimeout ?? string.Empty,
          CompressionType = c.CompressionType ?? string.Empty,
        };
        proto.AttachedProfileTokens.AddRange(c.AttachedProfileTokens);
        response.Configs.Add(proto);
      }
    }
    catch (Exception ex)
    {
      response.Error = ex.Message;
    }
    return response;
  }

  public override async Task<MetadataConfigOptionsResponse> GetMetadataConfigOptions(
    MetadataConfigOptionsRequest request, ServerCallContext context)
  {
    var response = new MetadataConfigOptionsResponse();
    try
    {
      var options = await settingsService.GetMetadataConfigOptionsAsync(
        request.SourceId, request.ConfigurationToken, context.CancellationToken);
      if (options != null)
      {
        response.SupportsPtzStatus = options.SupportsPtzStatus;
        response.CompressionTypes.AddRange(options.CompressionTypes);
      }
    }
    catch (Exception ex)
    {
      response.Error = ex.Message;
    }
    return response;
  }

  public override Task<OnvifMutationResponse> SetMetadataConfig(
    SetMetadataConfigRequest request, ServerCallContext context)
    => Apply(() => settingsService.SetMetadataConfigAsync(
      request.SourceId,
      new OnvifMetadataConfigUpdate(
        request.ConfigurationToken,
        request.HasAnalytics ? request.Analytics : null,
        request.HasPtzStatus ? request.PtzStatus : null,
        request.HasSessionTimeout ? request.SessionTimeout : null,
        request.HasCompressionType ? request.CompressionType : null),
      context.CancellationToken));

  public override Task<OnvifMutationResponse> AttachMetadataConfig(
    AttachConfigRequest request, ServerCallContext context)
    => Apply(() => settingsService.AttachMetadataConfigAsync(
      request.SourceId, request.ProfileToken, request.ConfigurationToken, context.CancellationToken));

  public override Task<OnvifMutationResponse> DetachMetadataConfig(
    AttachConfigRequest request, ServerCallContext context)
    => Apply(() => settingsService.DetachMetadataConfigAsync(
      request.SourceId, request.ProfileToken, request.ConfigurationToken, context.CancellationToken));

  public override async Task<AnalyticsConfigsResponse> GetAnalyticsConfigs(
    CameraRequest request, ServerCallContext context)
  {
    var response = new AnalyticsConfigsResponse();
    try
    {
      var configs = await settingsService.GetAnalyticsConfigsAsync(
        request.SourceId, context.CancellationToken);
      foreach (var c in configs)
      {
        var proto = new AnalyticsConfig { Token = c.Token, Name = c.Name };
        proto.AttachedProfileTokens.AddRange(c.AttachedProfileTokens);
        response.Configs.Add(proto);
      }
    }
    catch (Exception ex)
    {
      response.Error = ex.Message;
    }
    return response;
  }

  public override Task<OnvifMutationResponse> AttachAnalyticsConfig(
    AttachConfigRequest request, ServerCallContext context)
    => Apply(() => settingsService.AttachAnalyticsConfigAsync(
      request.SourceId, request.ProfileToken, request.ConfigurationToken, context.CancellationToken));

  public override Task<OnvifMutationResponse> DetachAnalyticsConfig(
    AttachConfigRequest request, ServerCallContext context)
    => Apply(() => settingsService.DetachAnalyticsConfigAsync(
      request.SourceId, request.ProfileToken, request.ConfigurationToken, context.CancellationToken));

  // ── Analytics modules and rules ───────────────────────────────────────────

  public override Task<AnalyticsModuleDescriptionsResponse> GetSupportedAnalyticsModules(
    AnalyticsConfigRequest request, ServerCallContext context)
    => Describe(() => settingsService.GetSupportedAnalyticsModulesAsync(
      request.SourceId, request.ConfigurationToken, context.CancellationToken));

  public override Task<AnalyticsModulesResponse> GetAnalyticsModules(
    AnalyticsConfigRequest request, ServerCallContext context)
    => ListModules(() => settingsService.GetAnalyticsModulesAsync(
      request.SourceId, request.ConfigurationToken, context.CancellationToken));

  public override Task<OnvifMutationResponse> CreateAnalyticsModule(
    AnalyticsModuleRequest request, ServerCallContext context)
    => Apply(() => settingsService.CreateAnalyticsModuleAsync(
      request.SourceId, request.ConfigurationToken, ToLib(request.Module), context.CancellationToken));

  public override Task<OnvifMutationResponse> ModifyAnalyticsModule(
    AnalyticsModuleRequest request, ServerCallContext context)
    => Apply(() => settingsService.ModifyAnalyticsModuleAsync(
      request.SourceId, request.ConfigurationToken, ToLib(request.Module), context.CancellationToken));

  public override Task<OnvifMutationResponse> DeleteAnalyticsModule(
    AnalyticsModuleNameRequest request, ServerCallContext context)
    => Apply(() => settingsService.DeleteAnalyticsModuleAsync(
      request.SourceId, request.ConfigurationToken, request.ModuleName, context.CancellationToken));

  public override Task<AnalyticsModuleDescriptionsResponse> GetSupportedRules(
    AnalyticsConfigRequest request, ServerCallContext context)
    => Describe(() => settingsService.GetSupportedRulesAsync(
      request.SourceId, request.ConfigurationToken, context.CancellationToken));

  public override Task<AnalyticsModulesResponse> GetRules(
    AnalyticsConfigRequest request, ServerCallContext context)
    => ListModules(() => settingsService.GetRulesAsync(
      request.SourceId, request.ConfigurationToken, context.CancellationToken));

  public override Task<OnvifMutationResponse> CreateRule(
    AnalyticsModuleRequest request, ServerCallContext context)
    => Apply(() => settingsService.CreateRuleAsync(
      request.SourceId, request.ConfigurationToken, ToLib(request.Module), context.CancellationToken));

  public override Task<OnvifMutationResponse> ModifyRule(
    AnalyticsModuleRequest request, ServerCallContext context)
    => Apply(() => settingsService.ModifyRuleAsync(
      request.SourceId, request.ConfigurationToken, ToLib(request.Module), context.CancellationToken));

  public override Task<OnvifMutationResponse> DeleteRule(
    AnalyticsModuleNameRequest request, ServerCallContext context)
    => Apply(() => settingsService.DeleteRuleAsync(
      request.SourceId, request.ConfigurationToken, request.ModuleName, context.CancellationToken));

  // ── Scene Description from media_server ───────────────────────────────────

  // Raw metadata documents off the camera's RTSP metadata track. Parsed here and forwarded to the
  // analytics service as normalized boxes. The reply carries nothing: media_server treats this as
  // fire-and-forget and never retries, so there is nothing useful to tell it.
  public override async Task<PushMetadataDocumentsResponse> PushMetadataDocuments(
    MetadataDocumentBatch request, ServerCallContext context)
  {
    await metadataIngestor.IngestAsync(request, context.CancellationToken);
    return new PushMetadataDocumentsResponse();
  }

  // ── Overlay-button probe ──────────────────────────────────────────────────

  public override Task<MetadataStatesResponse> GetMetadataStates(
    MetadataStatesRequest request, ServerCallContext context)
  {
    var response = new MetadataStatesResponse();
    foreach (var cameraId in request.CameraIds.Distinct())
    {
      var cached = metadataStateCache.TryGet(cameraId);
      if (cached is { } value)
      {
        response.BboxCapable[cameraId] = value;
        continue;
      }

      // Left out of the response entirely rather than answered "false": the caller re-asks, and a
      // false here would make the button flicker off on every cold cache.
      metadataStateCache.ProbeInBackground(
        cameraId, ct => settingsService.ProbeBboxCapableAsync(cameraId, ct));
    }
    return Task.FromResult(response);
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  // Named apart from the Profile G Mutate helper next door: same shape, different response type,
  // and C# will not overload on return type alone.
  private static async Task<OnvifMutationResponse> Apply(Func<Task> action)
  {
    try
    {
      await action();
      return new OnvifMutationResponse { Success = true };
    }
    catch (Exception ex)
    {
      return new OnvifMutationResponse { Success = false, Error = ex.Message };
    }
  }

  private static async Task<AnalyticsModulesResponse> ListModules(
    Func<Task<IReadOnlyList<OnvifAnalyticsModule>>> action)
  {
    var response = new AnalyticsModulesResponse();
    try
    {
      foreach (var m in await action())
      {
        var proto = new Vms.Onvif.AnalyticsModule { Name = m.Name, Type = m.Type };
        proto.SimpleItems.AddRange(
          m.SimpleItems.Select(i => new AnalyticsSimpleItem { Name = i.Name, Value = i.Value }));
        proto.ElementItemsXml.AddRange(m.ElementItemsXml);
        response.Modules.Add(proto);
      }
    }
    catch (Exception ex)
    {
      response.Error = ex.Message;
    }
    return response;
  }

  private static async Task<AnalyticsModuleDescriptionsResponse> Describe(
    Func<Task<IReadOnlyList<OnvifModuleDescription>>> action)
  {
    var response = new AnalyticsModuleDescriptionsResponse();
    try
    {
      foreach (var d in await action())
      {
        var proto = new Vms.Onvif.AnalyticsModuleDescription
        {
          Type = d.Type,
          MaxInstances = d.MaxInstances ?? string.Empty,
        };
        proto.Parameters.AddRange(d.Parameters.Select(
          p => new AnalyticsParameterDescription { Name = p.Name, Type = p.Type }));
        response.Descriptions.Add(proto);
      }
    }
    catch (Exception ex)
    {
      response.Error = ex.Message;
    }
    return response;
  }

  private static OnvifAnalyticsModule ToLib(Vms.Onvif.AnalyticsModule? module) =>
    new(module?.Name ?? string.Empty,
        module?.Type ?? string.Empty,
        module?.SimpleItems.Select(i => new OnvifSimpleItem(i.Name, i.Value)).ToList() ?? [],
        module?.ElementItemsXml.ToList() ?? []);
}
