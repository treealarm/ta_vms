using System;
using System.Collections.Generic;
using System.Linq;

namespace web_srv.Authorization;

// The action names the map's rights model uses, in the order the editor offers them. They are
// strings rather than an enum because the set is open: a new node kind may add an action without
// a migration, and the dictionary VmsMap returns is what the UI reads.
public static class RightActions
{
  public const string View = "view";
  public const string Edit = "edit";
  public const string ViewLive = "view_live";
  public const string ViewArchive = "view_archive";
  public const string Ptz = "ptz";
  public const string Control = "control";
  public const string Export = "export";
  /// <summary>Acknowledging and closing an alarm: the same right for both, it is one handling.</summary>
  public const string Acknowledge = "acknowledge";
}

/// <summary>
/// The set of objects an action is permitted on. <see cref="All"/> models a blanket grant without
/// ever materialising the list, so a permission check never needs to know how many objects exist.
/// </summary>
public sealed class RightsScope
{
  private static readonly IReadOnlySet<string> EmptyIds = new HashSet<string>(StringComparer.Ordinal);

  public static readonly RightsScope Everything = new(all: true, EmptyIds);
  public static readonly RightsScope Nothing = new(all: false, EmptyIds);

  private RightsScope(bool all, IReadOnlySet<string> ids)
  {
    All = all;
    Ids = ids;
  }

  public static RightsScope Of(IReadOnlySet<string> ids) => ids.Count == 0 ? Nothing : new(false, ids);

  public bool All { get; }
  /// <summary>Meaningless when <see cref="All"/> is true — ask <see cref="Contains"/> instead.</summary>
  public IReadOnlySet<string> Ids { get; }

  public bool IsEmpty => !All && Ids.Count == 0;

  public bool Contains(string cameraId) => All || Ids.Contains(cameraId);

  public IEnumerable<T> Filter<T>(IEnumerable<T> items, Func<T, string> cameraIdOf) =>
    All ? items : items.Where(i => Ids.Contains(cameraIdOf(i)));
}
