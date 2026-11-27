using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using web_srv.Authentication;

namespace web_srv.Tests;

// The list of places a type is in use exists to explain a refused delete, and the delete is an
// administrator's: the list travels on the admin surface and nowhere else. Its rows name diagrams
// and the spaces they stand in, so an operator must not reach them through the ordinary map reads,
// whatever the type dictionary itself is.
public class MapDiagramTypeUsageAccessTests
{
  private const string UsageRoute = "api/map/admin/diagram-types/usage";

  [Fact]
  public void TheUsageListIsServedOnTheAdminSurfaceOnly()
  {
    var serving = typeof(MapAdminController).Assembly.GetTypes()
      .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
      .SelectMany(ServedRoutes)
      .Where(served => served.Route == UsageRoute)
      .Select(served => served.Controller)
      .ToList();

    var controller = Assert.Single(serving);
    Assert.Equal(typeof(MapAdminController), controller);
    Assert.Equal(AuthPolicies.AdminOnly, controller.GetCustomAttribute<AuthorizeAttribute>()?.Policy);
  }

  /// Every route a controller serves: its own route with each action's template under it, which is
  /// how the framework builds them. One action may carry several, for a second path to the same read.
  private static IEnumerable<(Type Controller, string Route)> ServedRoutes(Type controller)
  {
    var prefix = controller.GetCustomAttribute<RouteAttribute>()?.Template.Trim('/') ?? string.Empty;
    return controller
      .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
      .SelectMany(action => action.GetCustomAttributes<HttpMethodAttribute>(inherit: true))
      .Select(attribute => (controller, Join(prefix, attribute.Template)));
  }

  private static string Join(string prefix, string? template)
  {
    var own = template?.Trim('/') ?? string.Empty;
    return own.Length == 0 ? prefix : $"{prefix}/{own}";
  }
}
