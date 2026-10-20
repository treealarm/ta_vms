using Grpc.Core;
using Vms.Fs;

namespace VmsFs;

/// <summary>
/// Serves mp4 files produced by camera-side imports. Mirrors the archive clip byte-range path
/// (<see cref="FsServiceImpl.ReadArchiveTs"/>) so the gateway can stream and range-request them the
/// same way, but these files are never clips — see <see cref="ExportFileStore"/>.
/// </summary>
public sealed partial class FsServiceImpl
{
  public override Task<ListExportFilesResponse> ListExportFiles(
    ListExportFilesRequest request, ServerCallContext context)
  {
    var response = new ListExportFilesResponse();
    response.Files.AddRange(exportStore.List(request.CameraId));
    return Task.FromResult(response);
  }

  public override Task<StatExportFileResponse> StatExportFile(
    ExportFileRequest request, ServerCallContext context)
  {
    var abs = exportStore.Resolve(request.RelPath);
    if (abs is null)
      return Task.FromResult(new StatExportFileResponse { Exists = false, Message = "invalid rel_path" });

    try
    {
      var info = new FileInfo(abs);
      return Task.FromResult(info.Exists
        ? new StatExportFileResponse { Exists = true, SizeBytes = (ulong)info.Length }
        : new StatExportFileResponse { Exists = false, Message = "not found" });
    }
    catch (Exception ex)
    {
      return Task.FromResult(new StatExportFileResponse { Exists = false, Message = ex.Message });
    }
  }

  public override async Task ReadExportFile(
    ReadExportFileRequest request,
    IServerStreamWriter<ReadArchiveTsChunk> responseStream,
    ServerCallContext context)
  {
    var abs = exportStore.Resolve(request.RelPath)
      ?? throw new RpcException(new Status(StatusCode.InvalidArgument, "invalid rel_path"));

    var info = new FileInfo(abs);
    if (!info.Exists)
      throw new RpcException(new Status(StatusCode.NotFound, "not found"));

    var fileSize = (ulong)info.Length;
    var offset = request.Offset;
    if (offset > fileSize)
      throw new RpcException(new Status(StatusCode.OutOfRange, "offset past end"));

    var remaining = fileSize - offset;
    if (request.Length > 0)
      remaining = Math.Min(remaining, request.Length);

    const int bufSize = 256 * 1024;
    var buf = new byte[bufSize];
    await using var fs = SharedFileRead.Open(abs, bufSize);
    fs.Seek((long)offset, SeekOrigin.Begin);

    while (remaining > 0)
    {
      var want = (int)Math.Min((ulong)bufSize, remaining);
      var got = await fs.ReadAsync(buf.AsMemory(0, want), context.CancellationToken);
      if (got <= 0)
        break;
      await responseStream.WriteAsync(
        new ReadArchiveTsChunk { Data = Google.Protobuf.ByteString.CopyFrom(buf, 0, got) },
        context.CancellationToken);
      remaining -= (ulong)got;
    }
  }

  public override Task<DeleteExportFileResponse> DeleteExportFile(
    ExportFileRequest request, ServerCallContext context)
  {
    var abs = exportStore.Resolve(request.RelPath);
    if (abs is null)
      return Task.FromResult(new DeleteExportFileResponse { Success = false, Message = "invalid rel_path" });

    try
    {
      if (File.Exists(abs))
        File.Delete(abs);
      return Task.FromResult(new DeleteExportFileResponse { Success = true });
    }
    catch (Exception ex)
    {
      return Task.FromResult(new DeleteExportFileResponse { Success = false, Message = ex.Message });
    }
  }
}
