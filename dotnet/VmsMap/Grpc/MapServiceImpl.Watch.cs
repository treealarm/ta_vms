using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using VmsMap.Watch;
using MapProto = Vms.Map;

namespace VmsMap.Grpc
{
  // The watch stream: commands in, diffs out. The poller is one per process and outlives the
  // stream; the subscription is the stream's own view of what it follows.
  public partial class MapServiceImpl
  {
    public override async Task Watch(
      IAsyncStreamReader<MapProto.WatchRequest> requestStream,
      IServerStreamWriter<MapProto.WatchMessage> responseStream,
      ServerCallContext context)
    {
      var subscription = _watchPoller.Subscribe();
      using var linked = CancellationTokenSource.CreateLinkedTokenSource(
        context.CancellationToken);

      try
      {
        var reading = ReadWatchRequestsAsync(requestStream, subscription, linked.Token);
        var writing = WriteWatchMessagesAsync(responseStream, subscription, linked.Token);

        await Task.WhenAny(reading, writing);
        linked.Cancel();

        try
        {
          await Task.WhenAll(reading, writing);
        }
        catch (OperationCanceledException)
        {
          // One side ended; the other was cancelled on purpose.
        }
      }
      finally
      {
        _watchPoller.Unsubscribe(subscription);
      }
    }

    private static async Task ReadWatchRequestsAsync(
      IAsyncStreamReader<MapProto.WatchRequest> stream,
      WatchSubscription subscription,
      CancellationToken ct)
    {
      await foreach (var request in stream.ReadAllAsync(ct))
      {
        var caller = ToCaller(request.Caller);

        switch (request.CommandCase)
        {
          case MapProto.WatchRequest.CommandOneofCase.SetBox when request.SetBox.Box is not null:
            subscription.SetBox(ToBoxDto(request.SetBox.Box), caller);
            break;

          case MapProto.WatchRequest.CommandOneofCase.SetIds:
            subscription.SetIds(request.SetIds.Ids.ToList(), caller);
            break;

          case MapProto.WatchRequest.CommandOneofCase.UpdateValues:
            subscription.SetValues(request.UpdateValues.Enabled, caller);
            break;

          default:
            // A message that only refreshes the caller applies to the current subscription.
            subscription.SetCaller(caller);
            break;
        }
      }
    }

    private static async Task WriteWatchMessagesAsync(
      IServerStreamWriter<MapProto.WatchMessage> stream,
      WatchSubscription subscription,
      CancellationToken ct)
    {
      await foreach (var message in subscription.Messages.Reader.ReadAllAsync(ct))
        await stream.WriteAsync(message, ct);
    }
  }
}
