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
      MapProto.WatchRequest request,
      IServerStreamWriter<MapProto.WatchMessage> responseStream,
      ServerCallContext context)
    {
      var subscription = _watchPoller.Subscribe();
      var ct = context.CancellationToken;

      try
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
            subscription.SetCaller(caller);
            break;
        }

        await foreach (var message in subscription.Messages.Reader.ReadAllAsync(ct))
          await responseStream.WriteAsync(message, ct);
      }
      finally
      {
        _watchPoller.Unsubscribe(subscription);
      }
    }
  }
}
