using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace web_vms;

// Maps gRPC errors from downstream services (VmsCfg, VmsOnvif, VmsFs, media_server, ...)
// to a single HTTP status/body convention, so controllers don't repeat this per action.
public sealed class RpcExceptionFilter : IExceptionFilter
{
  public void OnException(ExceptionContext context)
  {
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
