using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace web_vms;

// Maps gRPC errors from downstream services (VmsCfg, VmsOnvif, VmsFs, video_srv, ...)
// to a single HTTP status/body convention, so controllers don't repeat this per action.
public sealed class RpcExceptionFilter : IExceptionFilter
{
  public void OnException(ExceptionContext context)
  {
    // A dependency that never came up: answer 503 with the reason instead of a bare 500, so the
    // client can say what is wrong rather than just "request failed".
    if (context.Exception is ServiceNotReadyException notReady)
    {
      context.Result = new ObjectResult(new { error = notReady.Message, detail = notReady.Detail })
      {
        StatusCode = StatusCodes.Status503ServiceUnavailable,
      };
      context.ExceptionHandled = true;
      return;
    }

    if (context.Exception is not RpcException ex)
      return;

    var status = ex.StatusCode switch
    {
      StatusCode.DeadlineExceeded => StatusCodes.Status504GatewayTimeout,
      StatusCode.InvalidArgument => StatusCodes.Status400BadRequest,
      StatusCode.NotFound => StatusCodes.Status404NotFound,
      StatusCode.Unavailable => StatusCodes.Status503ServiceUnavailable,
      _ => StatusCodes.Status502BadGateway
    };

    context.Result = new ObjectResult(new { error = ex.Status.Detail }) { StatusCode = status };
    context.ExceptionHandled = true;
  }
}
