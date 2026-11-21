using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VmsMap.Db;
using VmsMap.Domain;
using VmsMap.Domain.Arming;

namespace VmsMap.Adapter
{
  /// <summary>
  /// Restores nodes whose bypass deadline has passed: the own mode they had before the exclusion,
  /// a journal entry by `system` and, for a container, the same outbox row an operator's arming
  /// writes so the sensors are re-checked. The claim and the write share one transaction, so a
  /// crash leaves the deadline for the next tick; <c>SKIP LOCKED</c> lets replicas split the batch
  /// instead of racing for it.
  /// </summary>
  internal sealed class ArmingBypassSweep : BackgroundService
  {
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private const int BatchSize = 200;

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<ArmingBypassSweep> _logger;

    public ArmingBypassSweep(
      IServiceScopeFactory scopes,
      TimeProvider clock,
      ILogger<ArmingBypassSweep> logger)
    {
      _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
      _clock = clock ?? throw new ArgumentNullException(nameof(clock));
      _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
      while (!stoppingToken.IsCancellationRequested)
      {
        try
        {
          await SweepAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
          break;
        }
        catch (Exception ex)
        {
          _logger.LogWarning(ex, "Expiring arming bypasses failed");
        }

        try
        {
          await Task.Delay(Interval, stoppingToken);
        }
        catch (OperationCanceledException)
        {
          break;
        }
      }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
      using var scope = _scopes.CreateScope();
      var db = scope.ServiceProvider.GetRequiredService<VmsMapDbContext>();
      var arming = scope.ServiceProvider.GetRequiredService<NodeArmingService>();

      var expired = await SweepOnceAsync(db, arming, _clock.GetUtcNow().UtcDateTime, ct);
      if (expired > 0)
        _logger.LogInformation("Expired {Count} arming bypasses", expired);
    }

    /// <summary>
    /// One pass: claim up to <see cref="BatchSize"/> due rows per table and restore each. The
    /// deadline is compared against the caller's clock, so a test can move time without touching
    /// the database's.
    /// </summary>
    internal static async Task<int> SweepOnceAsync(
      VmsMapDbContext db, NodeArmingService arming, DateTime now, CancellationToken ct)
    {
      if (!db.Database.IsRelational())
        return 0;

      await using var tx = await db.Database.BeginTransactionAsync(ct);

      var devices = await db.DeviceObjectSettings
        .FromSqlInterpolated($"""
          SELECT * FROM device_object_settings
          WHERE bypass_until IS NOT NULL AND bypass_until <= {now}
          ORDER BY bypass_until
          LIMIT {BatchSize}
          FOR UPDATE SKIP LOCKED
          """)
        .AsTracking()
        .ToListAsync(ct);

      var containers = await db.ObjectArming
        .FromSqlInterpolated($"""
          SELECT * FROM object_arming
          WHERE bypass_until IS NOT NULL AND bypass_until <= {now}
          ORDER BY bypass_until
          LIMIT {BatchSize}
          FOR UPDATE SKIP LOCKED
          """)
        .AsTracking()
        .ToListAsync(ct);

      var expired = 0;
      foreach (var row in devices)
      {
        if (await arming.ExpireBypassAsync(Utils.ConvertGuidToObjectId(row.object_id), ct))
          expired++;
      }

      foreach (var row in containers)
      {
        if (await arming.ExpireBypassAsync(Utils.ConvertGuidToObjectId(row.object_id), ct))
          expired++;
      }

      await tx.CommitAsync(ct);
      return expired;
    }
  }
}
