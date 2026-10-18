using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Vms.Square;

namespace VmsSquareSingleton.Grpc;

/// <summary>
/// Answers whether this deployment is integrated with Square.
/// </summary>
/// <remarks>
/// Mapped whether or not the integration is switched on. A deployment without Square still runs
/// this service, and "configured = false" is the answer its monitor needs; refusing the call would
/// be indistinguishable from the service being down.
/// </remarks>
public sealed class SquareStatusServiceImpl(SquareLinkState state, SquareIntegrationEnabled enabled)
  : SquareStatusService.SquareStatusServiceBase
{
  public override Task<SquareStatus> GetStatus(GetSquareStatusRequest request, ServerCallContext context)
  {
    var (synced, lastOkAt, lastErrorAt, lastError) = state.Snapshot();
    var status = new SquareStatus
    {
      Configured = enabled.Value,
      Synced = enabled.Value && synced,
      LastError = lastError,
    };
    if (lastOkAt is not null)
      status.LastOkAt = Timestamp.FromDateTime(DateTime.SpecifyKind(lastOkAt.Value, DateTimeKind.Utc));
    if (lastErrorAt is not null)
      status.LastErrorAt = Timestamp.FromDateTime(DateTime.SpecifyKind(lastErrorAt.Value, DateTimeKind.Utc));
    return Task.FromResult(status);
  }
}
