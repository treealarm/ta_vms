using Microsoft.EntityFrameworkCore;
using Vms.Cfg;

namespace VmsCfg;

// The definitions an alarm is raised under. They are seeded by the migration and read-only here:
// M2.4 has no editor for them, and the set is exactly what the system can raise, so a caller that
// names an unknown definition is wrong rather than the table being incomplete.
public class AlarmDefinitionStorage(IDbContextFactory<VmsCfgDbContext> dbFactory)
{
  public async Task<List<AlarmDefinition>> ListAsync(CancellationToken ct)
  {
    await using var db = await dbFactory.CreateDbContextAsync(ct);
    return await db.AlarmDefinitions
      .AsNoTracking()
      .OrderBy(x => x.Name)
      .Select(x => new AlarmDefinition
      {
        Id = x.Id.ToString(),
        Name = x.Name,
        Severity = x.Severity,
        AckPolicy = x.AckPolicy,
      })
      .ToListAsync(ct);
  }
}
