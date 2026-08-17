using Grpc.Core;
using Vms.Fs;

namespace VmsFs;

/// <summary>
/// Analytics geometry recorded against the archive timeline. Write comes from
/// VmsAnalyticsSingleton (the one place both detection sources converge); reads come from
/// web_vms.Server on behalf of an archive player.
/// </summary>
public sealed partial class FsServiceImpl
{
  public override async Task<OperationStatus> PushMetadataSamples(
    PushMetadataSamplesRequest request, ServerCallContext context)
  {
    try
    {
      await metadataStore.PushAsync(request, context.CancellationToken);
      return new OperationStatus { Status = OperationStatus.Types.Status.Success };
    }
    catch (ArgumentException ex)
    {
      // Permanent, not retryable: a batch the caller built wrong will be built wrong again.
      return new OperationStatus
      {
        Status = OperationStatus.Types.Status.PermanentFailure,
        Message = ex.Message,
      };
    }
  }

  public override async Task<GetMetadataSamplesResponse> GetMetadataSamples(
    GetMetadataSamplesRequest request, ServerCallContext context)
  {
    var response = new GetMetadataSamplesResponse();
    if (request.FromTs is null || request.ToTs is null)
      return response;

    var sources = await metadataStore.GetAsync(
      request.CameraId,
      request.HasStreamName ? request.StreamName : null,
      request.FromTs.ToDateTime().ToUniversalTime(),
      request.ToTs.ToDateTime().ToUniversalTime(),
      context.CancellationToken);

    response.Sources.AddRange(sources);
    return response;
  }

  public override async Task<GetMetadataCoverageResponse> GetMetadataCoverage(
    GetMetadataCoverageRequest request, ServerCallContext context)
  {
    if (request.FromTs is null || request.ToTs is null)
      return new GetMetadataCoverageResponse();

    var (hasCv, hasOnvif) = await metadataStore.HasAnyAsync(
      request.CameraId,
      request.HasStreamName ? request.StreamName : null,
      request.FromTs.ToDateTime().ToUniversalTime(),
      request.ToTs.ToDateTime().ToUniversalTime(),
      context.CancellationToken);

    return new GetMetadataCoverageResponse { HasCv = hasCv, HasOnvif = hasOnvif };
  }
}
