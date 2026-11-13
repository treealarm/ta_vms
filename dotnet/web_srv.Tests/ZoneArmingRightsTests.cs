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

// The guard switch and the zone's acknowledgement: what each action requires, and that the filter
// can resolve a node id — a space has no camera id at all, so `cameraId` would never bind.
public class ZoneArmingRightsTests
{
  [Fact]
  public void ReadingArmingIsView()
  {
    var required = Action(nameof(ControlController.GetArming));

    Assert.NotNull(required);
    Assert.Equal(RightActions.View, required.Action);
    Assert.Equal("objectId", required.Name);
  }

  [Fact]
  public void WritingArmingIsControl()
  {
    var required = Action(nameof(ControlController.SetArming));

    Assert.NotNull(required);
    Assert.Equal(RightActions.Control, required.Action);
    Assert.Equal("objectId", required.Name);
  }

  [Fact]
  public void AcknowledgingAZoneIsAcknowledge()
  {
    var required = Action(nameof(ControlController.AcknowledgeAlarms));

    Assert.NotNull(required);
    Assert.Equal(RightActions.Acknowledge, required.Action);
    Assert.Equal("objectId", required.Name);
  }

  [Fact]
  public void EveryControlActionDeclaresWhatItNeeds()
  {
    var actions = typeof(ControlController)
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

  [Fact]
  public async Task TheFilterResolvesANodeIdNamedByTheAction()
  {
    var (result, executed) = await RunAsync(allowed: true, nameof(Probe.NodeGuarded));

    Assert.True(executed);
    Assert.Null(result);
  }

  [Fact]
  public async Task ANodeIdWithoutTheRightIsForbidden()
  {
    var (result, executed) = await RunAsync(allowed: false, nameof(Probe.NodeGuarded));

    Assert.False(executed);
    var code = Assert.IsType<StatusCodeResult>(result);
    Assert.Equal(StatusCodes.Status403Forbidden, code.StatusCode);
  }

  private static RequiresRightAttribute? Action(string method) =>
    typeof(ControlController).GetMethod(method)?.GetCustomAttribute<RequiresRightAttribute>();

  private sealed class Probe : ControllerBase
  {
    [RequiresRight(RightActions.Control, Name = "objectId")]
    public void NodeGuarded() { }
  }

  private sealed class FakeRights(bool allowed) : IRightsService
  {
    public Task<bool> IsAllowedAsync(
      ClaimsPrincipal user, string id, string action, CancellationToken ct) =>
      Task.FromResult(allowed);

    public Task<RightsScope> ScopeAsync(ClaimsPrincipal user, string action, CancellationToken ct) =>
      throw new NotSupportedException();

    public void Invalidate(string username) { }
  }

  private static async Task<(IActionResult? Result, bool Executed)> RunAsync(
    bool allowed, string method)
  {
    var http = new DefaultHttpContext
    {
      User = new ClaimsPrincipal(new ClaimsIdentity("test")),
    };
    var route = new RouteData();
    route.Values["objectId"] = "f0b9c5d0-0000-4000-8000-000000000001";

    var descriptor = new ControllerActionDescriptor
    {
      ControllerName = nameof(Probe),
      ActionName = method,
      ControllerTypeInfo = typeof(Probe).GetTypeInfo(),
      MethodInfo = typeof(Probe).GetMethod(method)!,
    };

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
}
