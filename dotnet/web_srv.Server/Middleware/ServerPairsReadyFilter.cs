using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace web_srv;

/// <summary>
/// Marks an action that must answer before VmsCfg has been read — health and signing in.
/// </summary>
/// <remarks>
/// Deliberately an opt-out rather than an opt-in. Almost every endpoint here resolves a camera to
/// the node that serves it, so "needs the configuration" is the rule and these are the exceptions;
/// an opt-in attribute would have to be remembered on every new controller, and forgetting it fails
/// in the direction of a confusing 503.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowNotReadyAttribute : Attribute;

/// <summary>
/// Answers 503 while the server↔node map has not been read from VmsCfg yet.
/// </summary>
/// <remarks>
/// <para>
/// This exists so that the ordinary startup race stops being an exception. The browser starts
/// polling as soon as the page loads, which is routinely before VmsCfg has answered
/// <c>ListMediaServers</c> — and the registry's own guard threw <see cref="ServiceNotReadyException"/>
/// from deep inside the call, to be turned into a 503 by <see cref="RpcExceptionFilter"/> several
/// frames later. The response was right, but the route to it was an exception thrown on a path
/// that is expected and frequent, which stops a debugger on every page load and buries real faults
/// among the noise.
/// </para>
/// <para>
/// Asking the question once, before the action runs, gives the same answer without any of that. The
/// guards inside <see cref="ServerPairRegistry"/> stay where they are: they now describe an
/// invariant this filter maintains, rather than being the thing that enforces it, and they still
/// catch anything reaching the registry by another road.
/// </para>
/// </remarks>
public sealed class ServerPairsReadyFilter(ServerPairRegistry registry) : IAsyncActionFilter
{
  public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
  {
    if (registry.IsInitialized || Exempt(context))
    {
      await next();
      return;
    }

    context.Result = new ObjectResult(new
    {
      error = "Configuration service (VmsCfg) has not answered yet — see GET /api/health",
      detail = registry.LastInitError,
    })
    {
      StatusCode = StatusCodes.Status503ServiceUnavailable,
    };
  }

  private static bool Exempt(ActionExecutingContext context) =>
    context.ActionDescriptor.EndpointMetadata.Any(m => m is AllowNotReadyAttribute);
}
