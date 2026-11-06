using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapr.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VmsMap.Db;

namespace VmsMap.Outbox
{
  /// <summary>
  /// Publishes what <see cref="EventsWriter"/> left in <c>map_outbox</c>. At-least-once: the row is
  /// marked published after the bus accepted it, and the receiver is idempotent, so a repeat is
  /// harmless. Two VmsMap replicas do not fight over a row — <c>SKIP LOCKED</c> gives each its own
  /// batch.
  /// </summary>
  internal sealed class OutboxPublisher : BackgroundService
  {
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetainPublished = TimeSpan.FromHours(24);
    private const int BatchSize = 100;

    private readonly IServiceScopeFactory _scopes;
    private readonly DaprClient _dapr;
    private readonly string _pubSubName;
    private readonly ILogger<OutboxPublisher> _logger;
    private DateTime _lastCleanup = DateTime.MinValue;

    public OutboxPublisher(
      IServiceScopeFactory scopes,
      DaprClient dapr,
      string pubSubName,
      ILogger<OutboxPublisher> logger)
    {
      _scopes = scopes;
      _dapr = dapr;
      _pubSubName = pubSubName;
      _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
      using var timer = new PeriodicTimer(Interval);
      while (!stoppingToken.IsCancellationRequested)
      {
        try
        {
          await PublishBatchAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
          break;
        }
        catch (Exception ex)
        {
          _logger.LogWarning(ex, "Publishing the map outbox failed; the rows stay for the next pass");
        }

        if (DateTime.UtcNow - _lastCleanup >= CleanupInterval)
        {
          try
          {
            await CleanupAsync(stoppingToken);
            _lastCleanup = DateTime.UtcNow;
          }
          catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
          {
            break;
          }
          catch (Exception ex)
          {
            _logger.LogWarning(ex, "Cleaning the published map outbox rows failed");
          }
        }

        try
        {
          await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
          break;
        }
      }
    }

    private async Task PublishBatchAsync(CancellationToken ct)
    {
      using var scope = _scopes.CreateScope();
      var db = scope.ServiceProvider.GetRequiredService<VmsMapDbContext>();

      if (!db.Database.IsRelational())
        return;

      await using var tx = await db.Database.BeginTransactionAsync(ct);

      var batch = await db.MapOutbox
        .FromSqlRaw("""
          SELECT * FROM map_outbox
          WHERE published_at IS NULL
          ORDER BY id
          LIMIT 100
          FOR UPDATE SKIP LOCKED
          """)
        .AsTracking()
        .ToListAsync(ct);

      if (batch.Count == 0)
      {
        await tx.CommitAsync(ct);
        return;
      }

      foreach (var row in batch)
      {
        try
        {
          using var document = JsonDocument.Parse(row.payload);
          await _dapr.PublishEventAsync(_pubSubName, row.topic, document.RootElement, ct);
          row.published_at = DateTime.UtcNow;
          row.last_error = null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
          throw;
        }
        catch (Exception ex)
        {
          row.attempts += 1;
          row.last_error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
        }
      }

      await db.SaveChangesAsync(ct);
      await tx.CommitAsync(ct);
    }

    private async Task CleanupAsync(CancellationToken ct)
    {
      using var scope = _scopes.CreateScope();
      var db = scope.ServiceProvider.GetRequiredService<VmsMapDbContext>();
      if (!db.Database.IsRelational())
        return;

      var cutoff = DateTime.UtcNow - RetainPublished;
      await db.MapOutbox
        .Where(r => r.published_at != null && r.published_at < cutoff)
        .ExecuteDeleteAsync(ct);
    }
  }
}
