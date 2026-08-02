using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using web_vms.Authentication;

namespace web_vms.Authorization;

/// <summary>
/// Applies per-camera permissions to every MVC action, and refuses anything that has not declared
/// what it needs. Fail-closed by construction: a controller added later without a
/// <see cref="CameraAccessAttribute"/> or <see cref="NoCameraAccessAttribute"/> answers 403 instead
/// of silently exposing video.
/// </summary>
public sealed class CameraAccessFilter(
  ICameraAccessService access,
  ILogger<CameraAccessFilter> logger) : IAsyncActionFilter
{
  public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
  {
    if (context.ActionDescriptor is not ControllerActionDescriptor descriptor)
    {
      await next();
      return;
    }

    // Anonymous endpoints (login, frontend config, the dev-only WHEP test mirror) have no identity
    // to scope anything to.
    if (Find<AllowAnonymousAttribute>(descriptor) is not null)
    {
      await next();
      return;
    }

    // Admin-only endpoints are already restricted to the role that bypasses every camera check.
    if (HasAdminOnlyPolicy(descriptor))
    {
      await next();
      return;
    }

    if (Find<NoCameraAccessAttribute>(descriptor) is not null)
    {
      await next();
      return;
    }

    var required = Find<CameraAccessAttribute>(descriptor);
    if (required is null)
    {
      logger.LogError(
        "{Controller}.{Action} declares no camera access requirement — refusing. Add [CameraAccess] "
        + "or [NoCameraAccess(reason)].",
        descriptor.ControllerName, descriptor.ActionName);
      context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
      return;
    }

    if (required.Source == CameraIdSource.Action)
    {
      // The action owns the check — it either filters a list or has to resolve the camera from a
      // clip/event/detection id first.
      await next();
      return;
    }

    var cameraId = ResolveCameraId(context, required);
    if (string.IsNullOrWhiteSpace(cameraId))
    {
      logger.LogWarning(
        "{Controller}.{Action} expects a camera id from {Source} but none was bound",
        descriptor.ControllerName, descriptor.ActionName, required.Source);
      context.Result = new BadRequestObjectResult("cameraId is required");
      return;
    }

    if (!await access.IsAllowedAsync(context.HttpContext.User, cameraId, required.Action, context.HttpContext.RequestAborted))
    {
      context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
      return;
    }

    await next();
  }

  private static string? ResolveCameraId(ActionExecutingContext context, CameraAccessAttribute required)
  {
    if (required.Source == CameraIdSource.Body)
    {
      var argument = required.Name is null
        ? context.ActionArguments.Values.FirstOrDefault(v => v is not null && HasCameraIdProperty(v))
        : (context.ActionArguments.TryGetValue(required.Name, out var named) ? named : null);

      return argument is null ? null : CameraIdOf(argument);
    }

    var key = required.Name ?? "cameraId";
    if (context.ActionArguments.TryGetValue(key, out var bound) && bound is string fromArgument)
      return fromArgument;
    if (context.RouteData.Values.TryGetValue(key, out var fromRoute))
      return fromRoute?.ToString();

    return context.HttpContext.Request.Query[key].FirstOrDefault();
  }

  private static bool HasCameraIdProperty(object value) =>
    value.GetType().GetProperty("CameraId")?.PropertyType == typeof(string);

  private static string? CameraIdOf(object value) =>
    value.GetType().GetProperty("CameraId")?.GetValue(value) as string;

  private static T? Find<T>(ControllerActionDescriptor descriptor) where T : Attribute =>
    descriptor.MethodInfo.GetCustomAttributes(typeof(T), inherit: true).FirstOrDefault() as T
    ?? descriptor.ControllerTypeInfo.GetCustomAttributes(typeof(T), inherit: true).FirstOrDefault() as T;

  private static bool HasAdminOnlyPolicy(ControllerActionDescriptor descriptor) =>
    descriptor.MethodInfo.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
      .Concat(descriptor.ControllerTypeInfo.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true))
      .Cast<AuthorizeAttribute>()
      .Any(a => a.Policy == AuthPolicies.AdminOnly);
}
