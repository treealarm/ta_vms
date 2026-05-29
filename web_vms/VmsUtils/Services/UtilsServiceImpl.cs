using System.Text.Json;
using Dapr.Client;
using Grpc.Core;
using Vms.Cfg;
using Vms.Utils;

namespace VmsUtils;

public class UtilsServiceImpl(IHttpClientFactory httpClientFactory, string cfgAppId)
  : UtilsService.UtilsServiceBase
{
  private readonly HttpClient _http = httpClientFactory.CreateClient();

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

      if (!serverReply.Found || string.IsNullOrWhiteSpace(serverReply.Server.ApiUrl))
        return new GetMediaMtxPathInfoResponse { Success = false };

      var apiUrl = serverReply.Server.ApiUrl.TrimEnd('/');
      var path   = $"{request.CameraId}/{request.StreamName}";
      var url    = $"{apiUrl}/v3/paths/get/{path}";

      using var response = await _http.GetAsync(url, context.CancellationToken);
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
    catch
    {
      return new GetMediaMtxPathInfoResponse { Success = false };
    }
  }
}
