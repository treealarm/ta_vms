using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Mvc;
using web_srv.Authentication;
using MapProto = Vms.Map;

namespace web_srv;

// The map endpoints speak the proto JSON their contract is written in, so the browser and the
// proto stay one shape instead of a third set of DTOs. The caller travels with every read:
// VmsMap decides what a node is worth to this user, not the gateway.
internal static class MapApi
{
  private static readonly JsonParser Parser =
    new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

  public static readonly JsonFormatter Formatter = JsonFormatter.Default;

  public static ContentResult Json(IMessage message) => new()
  {
    Content = Formatter.Format(message),
    ContentType = "application/json",
  };

  public static T Parse<T>(JsonElement body) where T : class, IMessage<T>, new() =>
    Parser.Parse<T>(body.GetRawText());

  public static CallOptions Options(CancellationToken ct) =>
    new(deadline: DateTime.UtcNow.AddSeconds(10), cancellationToken: ct);

  public static MapProto.Caller ToMapCaller(this ClaimsPrincipal user) => new()
  {
    Username = user.Identity?.Name ?? string.Empty,
    Roles = { user.Claims.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value) },
    // With no Keycloak in the deployment there is no identity to scope anything to; web_srv is the
    // only caller that knows that, and VmsMap trusts it.
    Unrestricted = !AuthGate.KeycloakEnabled,
  };
}
