using System.Text.Json;
using Dapr.Client;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vms.Cfg;
using Vms.Utils;

namespace VmsUtils;

public class UtilsServiceImpl(
  IHttpClientFactory httpClientFactory,
  string cfgAppId,
  EntityStateRegistry entityStateRegistry,
  ILogger<UtilsServiceImpl> logger)
  : UtilsService.UtilsServiceBase
{
  /// <summary>Carries the vms_read Basic credentials MediaMTX's Control API requires.</summary>
  public const string MediaMtxHttpClientName = "mediamtx-api";

  private readonly HttpClient _http = httpClientFactory.CreateClient(MediaMtxHttpClientName);

  public override Task<ListEntityStatesResponse> ListEntityStates(Empty request, ServerCallContext context)
  {
    var response = new ListEntityStatesResponse();
    foreach (var (key, entry) in entityStateRegistry.GetAll())
    {
      response.Entities.Add(new EntityState
      {
        Service = key.Service,
        EntityType = key.EntityType,
        EntityId = key.EntityId,
        StateJson = entry.StateJson,
        LastSeen = Timestamp.FromDateTime(DateTime.SpecifyKind(entry.LastSeenUtc, DateTimeKind.Utc)),
        Stale = entry.Stale,
      });
    }
    return Task.FromResult(response);
  }

  public override async Task<GetMediaMtxPathInfoResponse> GetMediaMtxPathInfo(
    GetMediaMtxPathInfoRequest request,
    ServerCallContext context)
  {
    try
    {
      // Ask VmsCfg which MediaMTX server is assigned to this camera
      var invoker = DaprClient.CreateInvocationInvoker(cfgAppId);
      var cfgClient = new CfgService.CfgServiceClient(invoker);
      var serverReply = await cfgClient.GetOrAssignMediaMtxServerAsync(
        new SourceRequest { SourceId = request.CameraId },
        cancellationToken: context.CancellationToken);

      if (!serverReply.Found || string.IsNullOrWhiteSpace(serverReply.Server.Host))
        return new GetMediaMtxPathInfoResponse { Success = false };

      var apiUrl = $"http://{serverReply.Server.Host}:{serverReply.Server.ApiPort}";
      var path   = $"{request.CameraId}/{request.StreamName}";
      var url    = $"{apiUrl}/v3/paths/get/{path}";

      using var response = await _http.GetAsync(url, context.CancellationToken);
      if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        return new GetMediaMtxPathInfoResponse { Success = true, ReadersCount = 0, HasSource = false };
      if (!response.IsSuccessStatusCode)
        return new GetMediaMtxPathInfoResponse { Success = false };

      using var doc = JsonDocument.Parse(
        await response.Content.ReadAsStreamAsync(context.CancellationToken));

      var root = doc.RootElement;
      var readersCount = root.TryGetProperty("readers", out var readers)
        ? readers.GetArrayLength()
        : 0;
      var hasSource = root.TryGetProperty("source", out var source)
        && source.ValueKind != JsonValueKind.Null;

      return new GetMediaMtxPathInfoResponse
      {
        Success      = true,
        ReadersCount = readersCount,
        HasSource    = hasSource,
      };
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "GetMediaMtxPathInfo failed for camera '{CameraId}' stream '{StreamName}'", request.CameraId, request.StreamName);
      return new GetMediaMtxPathInfoResponse { Success = false };
    }
  }
}
