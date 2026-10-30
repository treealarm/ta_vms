using System;
using System.Collections.Generic;
using System.Linq;

namespace VmsMap.Domain
{
  // Who a read is for. VmsMap trusts web_srv to fill it in: the web is the only caller with a
  // user behind the request, and it is the service that already authenticated them.
  public sealed record CallerContext(
    string? Username,
    IReadOnlyCollection<string> Roles,
    bool Unrestricted)
  {
    public static readonly CallerContext Anonymous = new(null, [], false);

    // `admin` bypasses every right, exactly as the old camera ACL did. `unrestricted` is a
    // deployment without Keycloak at all, where there is nobody to scope anything to.
    public bool IsAdmin =>
      Unrestricted || Roles.Contains("admin", StringComparer.OrdinalIgnoreCase);

    // Every subject a right row may name: the user themselves and each of their roles.
    public IReadOnlyList<string> Subjects =>
      string.IsNullOrWhiteSpace(Username)
        ? Roles.Distinct(StringComparer.Ordinal).ToList()
        : Roles.Append(Username!).Distinct(StringComparer.Ordinal).ToList();
  }
}
