using Microsoft.EntityFrameworkCore;
using Vms.Cfg;

namespace VmsCfg;

/// <summary>
/// The stored reactions to journal events. VmsCfg owns them — the router in VmsSingleton reads
/// them through gRPC and keeps its own copy — and saving one is the only moment anything about it
/// is checked: the selector and the action parameters are validated here, cameras are looked up,
/// and the version the run will carry is assigned.
/// </summary>
public class EventSubscriptionStorage(IDbContextFactory<VmsCfgDbContext> dbFactory)
{
  public async Task<List<EventSubscription>> ListAsync(CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var rows = await db.EventSubscriptions
      .AsNoTracking()
      .Include(x => x.Actions)
      .OrderBy(x => x.Name)
      .ThenBy(x => x.Id)
      .ToListAsync(ct);

    return rows.Select(ToProto).ToList();
  }

  public async Task<EventSubscription?> GetAsync(string id, CancellationToken ct)
  {
    if (!Guid.TryParse(id, out var guid))
      return null;

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var row = await db.EventSubscriptions
      .AsNoTracking()
      .Include(x => x.Actions)
      .FirstOrDefaultAsync(x => x.Id == guid, ct);

    return row is null ? null : ToProto(row);
  }

  /// <summary>
  /// Saves a subscription and answers it with the id and version it was stored under. The version
  /// is the service's: a caller cannot rewind it, and it is what a started run names as its
  /// snapshot. An empty id assigns one, which is how a demo install stays idempotent.
  /// </summary>
  public async Task<(EventSubscription? Saved, string? Error)> UpsertAsync(
    EventSubscription input, CancellationToken ct)
  {
    var check = EventSubscriptionRules.Check(input);
    if (check.Error is not null)
      return (null, check.Error);

    Guid id;
    if (string.IsNullOrWhiteSpace(input.Id))
      id = Guid.NewGuid();
    else if (!Guid.TryParse(input.Id, out id))
      return (null, $"id must be a UUID, got '{input.Id}'");

    await using var db = await dbFactory.CreateDbContextAsync(ct);

    if (check.CameraIds.Count > 0)
    {
      var known = await db.CameraSetups
        .AsNoTracking()
        .Where(x => check.CameraIds.Contains(x.CameraId))
        .Select(x => x.CameraId)
        .ToListAsync(ct);
      var missing = check.CameraIds.Except(known, StringComparer.Ordinal).ToList();
      if (missing.Count > 0)
        return (null, $"no such camera: {string.Join(", ", missing)}");
    }

    await using var tx = await db.Database.BeginTransactionAsync(ct);

    var existing = await db.EventSubscriptions.FirstOrDefaultAsync(x => x.Id == id, ct);

    var saved = existing ?? new DbEventSubscription { Id = id };
    saved.Name = input.Name;
    saved.Enabled = input.Enabled;
    saved.EventName = input.EventName;
    saved.SourceFilter = Normalize(input.SourceFilter);
    saved.ArmedCondition = input.ArmedCondition;
    saved.CooldownMs = input.CooldownMs;
    saved.Version = existing is null ? 1 : existing.Version + 1;

    if (existing is null)
      db.EventSubscriptions.Add(saved);
    else
      // Bulk, not tracked: the replacement actions carry the same (subscription_id, position)
      // keys, and a tracked delete beside a tracked insert of the same key is a conflict.
      await db.SubscriptionActions
        .Where(a => a.SubscriptionId == id)
        .ExecuteDeleteAsync(ct);

    foreach (var action in input.Actions)
    {
      db.SubscriptionActions.Add(new DbSubscriptionAction
      {
        SubscriptionId = id,
        Position = action.Position,
        Kind = action.Kind,
        Params = Normalize(action.Params),
      });
    }

    await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);

    // The caller's own message, with the service's id and version: the stored actions are the
    // caller's actions, and re-reading them would only cost a round trip.
    var result = input.Clone();
    result.Id = id.ToString();
    result.Version = saved.Version;
    return (result, null);
  }

  public async Task<(bool Removed, string? Error)> RemoveAsync(string id, CancellationToken ct)
  {
    if (!Guid.TryParse(id, out var guid))
      return (false, $"id must be a UUID, got '{id}'");

    await using var db = await dbFactory.CreateDbContextAsync(ct);
    var row = await db.EventSubscriptions.FirstOrDefaultAsync(x => x.Id == guid, ct);
    if (row is null)
      return (false, null);

    // The actions cascade in the database; removing the parent is the whole delete.
    db.EventSubscriptions.Remove(row);
    await db.SaveChangesAsync(ct);
    return (true, null);
  }

  // An empty selector is the empty object, so the column never holds NULL and the router's
  // reader has one shape to parse.
  private static string Normalize(string? json) =>
    string.IsNullOrWhiteSpace(json) ? "{}" : json;

  private static EventSubscription ToProto(DbEventSubscription row)
  {
    var subscription = new EventSubscription
    {
      Id = row.Id.ToString(),
      Name = row.Name,
      Enabled = row.Enabled,
      EventName = row.EventName,
      SourceFilter = row.SourceFilter,
      ArmedCondition = row.ArmedCondition,
      CooldownMs = row.CooldownMs,
      Version = row.Version,
    };

    subscription.Actions.AddRange(row.Actions
      .OrderBy(x => x.Position)
      .Select(x => new SubscriptionAction
      {
        Position = x.Position,
        Kind = x.Kind,
        Params = x.Params,
      }));

    return subscription;
  }
}
