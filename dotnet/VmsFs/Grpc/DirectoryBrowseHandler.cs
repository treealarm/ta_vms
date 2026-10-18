using Grpc.Core;
using Vms.Fs;

namespace VmsFs;

// Storage-root picker for the admin UI. Part of FsServiceImpl — see FsServiceImpl.cs.
public sealed partial class FsServiceImpl
{
  public override Task<ListDirectoriesResponse> ListDirectories(
    ListDirectoriesRequest request, ServerCallContext context)
  {
    // Probing directories is blocking I/O against a possibly slow (network) volume, so it is kept
    // off the gRPC dispatch thread.
    return Task.Run(() => WritableDirectoryBrowser.List(request.Path, request.IncludeFiles), context.CancellationToken);
  }
}
