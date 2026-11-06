using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VmsDb;
using VmsMap.Db;
using VmsMap.Domain;

namespace VmsMap.Tests;

// The event and its notification are one transaction. What these tests prove is the property that
// matters when the process dies between them: the outbox row is already committed, so the reaction
// is not lost — and a redelivered event does not produce a second row.
[Collection("postgis")]
public class OutboxTests
{
  private static readonly string? Dsn =
    Environment.GetEnvironmentVariable("VMS_MAP_TEST_DSN");

  private static async Task<(NpgsqlDataSource Source, VmsMapDbContext Db)?> OpenAsync()
  {
    if (string.IsNullOrWhiteSpace(Dsn))
      return null;

    await using (var migrationSource = new NpgsqlDataSourceBuilder(Dsn).Build())
    {
      await new VmsDbMigrator(migrationSource, "VmsMapTest", AppContext.BaseDirectory, "7743512881")
        .MigrateAsync();
    }

    var builder = new NpgsqlDataSourceBuilder(Dsn);
    builder.EnableDynamicJson();
    builder.UseNetTopologySuite();
    var source = builder.Build();

    var options = new DbContextOptionsBuilder<VmsMapDbContext>()
      .UseNpgsql(source, o => o.UseNetTopologySuite())
      .Options;
    var db = new VmsMapDbContext(options);

    await db.Database.ExecuteSqlRawAsync("TRUNCATE objects CASCADE;");
    await db.Database.ExecuteSqlRawAsync("TRUNCATE map_outbox;");

    return (source, db);
  }

  [Fact]
  public async Task AnEventAndItsNotificationAreCommittedTogether()
  {
    var opened = await OpenAsync();
    if (opened is not { } o)
      return;

    await using var source = o.Source;
    await using var db = o.Db;

    var objectId = Guid.NewGuid();
    db.Markers.Add(new DBMarker { id = objectId, name = "Sensor" });
    await db.SaveChangesAsync();

    var eventId = Guid.NewGuid().ToString();
    var writer = new EventsWriter(db, new EventsService(db, new MapDb(db)));
    await writer.WriteAsync([
      new EventDTO
      {
        id = eventId,
        object_id = objectId.ToString(),
        event_name = "sensor",
        timestamp = DateTime.UtcNow,
        param0 = "open",
      }
    ]);

    // Nothing published yet — this is the "process died after the commit" state. The row is there,
    // so the publisher finds it on the next pass.
    var row = await db.MapOutbox.SingleAsync();
    Assert.Null(row.published_at);
    Assert.Equal("map-event-created", row.topic);
    Assert.Contains(eventId, row.payload);
  }

  [Fact]
  public async Task ThePendingBatchIsReadWithItsJsonPayload()
  {
    var opened = await OpenAsync();
    if (opened is not { } o)
      return;

    await using var source = o.Source;
    await using var db = o.Db;

    var objectId = Guid.NewGuid();
    db.Markers.Add(new DBMarker { id = objectId, name = "Sensor" });
    await db.SaveChangesAsync();

    var writer = new EventsWriter(db, new EventsService(db, new MapDb(db)));
    await writer.WriteAsync([
      new EventDTO
      {
        id = Guid.NewGuid().ToString(),
        object_id = objectId.ToString(),
        event_name = "sensor",
        timestamp = DateTime.UtcNow,
      }
    ]);

    // The same query the publisher runs: a jsonb payload must read into the entity's string, and
    // the pending index must find the row.
    await using var tx = await db.Database.BeginTransactionAsync();
    var batch = await db.MapOutbox
      .FromSqlRaw("""
        SELECT * FROM map_outbox
        WHERE published_at IS NULL
        ORDER BY id
        LIMIT 100
        FOR UPDATE SKIP LOCKED
        """)
      .AsTracking()
      .ToListAsync();

    var row = Assert.Single(batch);
    Assert.Contains("event_id", row.payload);
    await tx.RollbackAsync();
  }

  [Fact]
  public async Task ARedeliveredEventWritesOneJournalRowAndOneNotification()
  {
    var opened = await OpenAsync();
    if (opened is not { } o)
      return;

    await using var source = o.Source;
    await using var db = o.Db;

    var objectId = Guid.NewGuid();
    db.Markers.Add(new DBMarker { id = objectId, name = "Sensor" });
    await db.SaveChangesAsync();

    var eventId = Guid.NewGuid().ToString();
    var writer = new EventsWriter(db, new EventsService(db, new MapDb(db)));
    var dto = new EventDTO
    {
      id = eventId,
      object_id = objectId.ToString(),
      event_name = "sensor",
      timestamp = DateTime.UtcNow,
      param0 = "open",
    };

    var first = await writer.WriteAsync([dto]);
    var second = await writer.WriteAsync([dto]);

    Assert.Single(first);
    Assert.Empty(second);
    Assert.Equal(1, await db.Events.CountAsync());
    Assert.Equal(1, await db.MapOutbox.CountAsync());
  }
}
