using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using web_srv;
using web_srv.Authorization;

namespace web_srv.Tests;

// The gateway's one authorization boundary, and the row the operator's guard switch sits in it.
// Inheriting `control` down the tree is VmsMap's answer and is tested there; this checks the
// fail-closed contract of the filter, that every DeviceIo action declares what it needs, and that
// the control list is a self-filtering read rather than a per-camera check.
public class DeviceIoRightsTests
{
  [Fact]
  public void TheControlListFiltersItselfByViewLive()
  {
    var required = typeof(ControlController)
      .GetMethod(nameof(ControlController.ListObjects))!
      .GetCustomAttribute<RequiresRightAttribute>();

    Assert.NotNull(required);
    Assert.Equal(RightActions.ViewLive, required.Action);
    // VmsMap filters the rows; without Action the filter would demand a cameraId the list has none of.
    Assert.Equal(RightSource.Action, required.Source);
  }

  [Fact]
  public async Task ASelfFilteringActionRunsWithoutACameraId()
  {
    var (result, executed) = await RunAsync(
      allowed: false, Descriptor(nameof(Probe.Filtered)), cameraId: null);

    Assert.True(executed);
    Assert.Null(result);
  }

  [Fact]
  public void ArmingASensorIsControl()
  {
    var required = typeof(DeviceIoController)
      .GetMethod(nameof(DeviceIoController.SetDigitalInputArmMode))!
      .GetCustomAttribute<RequiresRightAttribute>();

    Assert.NotNull(required);
    Assert.Equal(RightActions.Control, required.Action);
  }

  [Theory]
  [InlineData(nameof(DeviceIoController.GetDigitalInputs))]
  [InlineData(nameof(DeviceIoController.GetRelayOutputs))]
  public void ReadingPartsIsViewLive(string action)
  {
    var required = typeof(DeviceIoController).GetMethod(action)!
      .GetCustomAttribute<RequiresRightAttribute>();

    Assert.NotNull(required);
    Assert.Equal(RightActions.ViewLive, required.Action);
  }

  [Fact]
  public void EveryDeviceIoActionDeclaresWhatItNeeds()
  {
    var actions = typeof(DeviceIoController)
      .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
      .Where(m => m.GetCustomAttribute<HttpMethodAttribute>(inherit: true) is not null)
      .ToList();

    Assert.NotEmpty(actions);
    foreach (var action in actions)
    {
      var declared = action.GetCustomAttribute<RequiresRightAttribute>() is not null
        || action.GetCustomAttribute<NoRightsAttribute>() is not null;
      Assert.True(declared, $"{action.Name} declares no right, so RightsFilter would refuse it");
    }
  }

  // ── The filter's fail-closed rule ─────────────────────────────────────────

  private sealed class Probe : ControllerBase
  {
    public void Bare() { }

    [RequiresRight(RightActions.Control)]
    public void Guided() { }

    [RequiresRight(RightActions.ViewLive, Source = RightSource.Action)]
    public void Filtered() { }
  }

  private sealed class FakeRights(bool allowed) : IRightsService
  {
    public Task<bool> IsAllowedAsync(
      ClaimsPrincipal user, string cameraId, string action, CancellationToken ct) =>
      Task.FromResult(allowed);

    public Task<RightsScope> ScopeAsync(ClaimsPrincipal user, string action, CancellationToken ct) =>
      throw new NotSupportedException();

    public void Invalidate(string username) { }
  }

  private static ControllerActionDescriptor Descriptor(string method) => new()
  {
    ControllerName = nameof(Probe),
    ActionName = method,
    ControllerTypeInfo = typeof(Probe).GetTypeInfo(),
    MethodInfo = typeof(Probe).GetMethod(method)!,
  };

  private static async Task<(IActionResult? Result, bool Executed)> RunAsync(
    bool allowed, ControllerActionDescriptor descriptor, string? cameraId = "cam1")
  {
    var http = new DefaultHttpContext
    {
      User = new ClaimsPrincipal(new ClaimsIdentity("test")),
    };
    var route = new RouteData();
    if (cameraId is not null)
      route.Values["cameraId"] = cameraId;

    var actionContext = new ActionContext(http, route, descriptor);
    var executing = new ActionExecutingContext(
      actionContext, [], new Dictionary<string, object?>(), new Probe());

    var filter = new RightsFilter(new FakeRights(allowed), NullLogger<RightsFilter>.Instance);
    var executed = false;
    await filter.OnActionExecutionAsync(executing, () =>
    {
      executed = true;
      return Task.FromResult(new ActionExecutedContext(actionContext, [], executing.Controller));
    });

    return (executing.Result as IActionResult, executed);
  }

  [Fact]
  public async Task AnActionThatDeclaresNothingIsRefused()
  {
    var (result, executed) = await RunAsync(allowed: true, Descriptor(nameof(Probe.Bare)));

    Assert.False(executed);
    var code = Assert.IsType<StatusCodeResult>(result);
    Assert.Equal(StatusCodes.Status403Forbidden, code.StatusCode);
  }

  [Fact]
  public async Task AnActionWithItsRightRuns()
  {
    var (result, executed) = await RunAsync(allowed: true, Descriptor(nameof(Probe.Guided)));

    Assert.True(executed);
    Assert.Null(result);
  }

  [Fact]
  public async Task AnActionWithoutTheRightIsForbidden()
  {
    var (result, executed) = await RunAsync(allowed: false, Descriptor(nameof(Probe.Guided)));

    Assert.False(executed);
    var code = Assert.IsType<StatusCodeResult>(result);
    Assert.Equal(StatusCodes.Status403Forbidden, code.StatusCode);
  }

  [Fact]
  public async Task AnActionWithNoCameraIdCannotDecide()
  {
    var (result, executed) = await RunAsync(
      allowed: true, Descriptor(nameof(Probe.Guided)), cameraId: null);

    Assert.False(executed);
    Assert.IsType<BadRequestObjectResult>(result);
  }
}
