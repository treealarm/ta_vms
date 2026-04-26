using Grpc.Core;
using Vms.Fs;

namespace VmsFs;

public class FsServiceImpl(ClipStorage storage) : FsService.FsServiceBase
{
  private readonly ClipStorage _storage = storage;

  public override async Task<OperationStatus> IngestClip(
    IngestClipRequest request,
    ServerCallContext context)
  {
    if (request.Clip is null
      || string.IsNullOrWhiteSpace(request.Clip.CameraId)
      || string.IsNullOrWhiteSpace(request.Clip.FilePath)
      || request.Clip.StartTs is null)
    {
      return new OperationStatus
      {
        Status = OperationStatus.Types.Status.PermanentFailure,
        Message = "camera_id, file_path and start_ts are required"
      };
    }

    try
    {
      var clipUid = await _storage.IngestAsync(request.Clip, context.CancellationToken);
      return new OperationStatus
      {
        Status = OperationStatus.Types.Status.Success,
        ClipUid = clipUid
      };
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"IngestClip failed for '{request.Clip.FilePath}': {ex}");
      return new OperationStatus
      {
        Status = OperationStatus.Types.Status.RetryableFailure,
        Message = ex.Message
      };
    }
  }

  public override async Task<GetRecordingsResponse> GetRecordings(
    GetRecordingsRequest request,
    ServerCallContext context)
  {
    try
    {
      var response = new GetRecordingsResponse();
      response.Recordings.AddRange(
        await _storage.GetRecordingsAsync(request, context.CancellationToken));
      return response;
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"GetRecordings failed for '{request.CameraId}': {ex}");
      throw new RpcException(new Status(StatusCode.Internal, ex.Message));
    }
  }
}
