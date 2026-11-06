using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace web_srv;

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
      // A right the caller does not hold, and a transition the entity forbids — the two refusals
      // the alarm endpoints document as 403 and 409.
      StatusCode.PermissionDenied => StatusCodes.Status403Forbidden,
      StatusCode.FailedPrecondition => StatusCodes.Status409Conflict,
      StatusCode.Unavailable => StatusCodes.Status503ServiceUnavailable,
      _ => StatusCodes.Status502BadGateway
    };

    context.Result = new ObjectResult(new { error = ex.Status.Detail }) { StatusCode = status };
    context.ExceptionHandled = true;
  }
}
