using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using VmsMap.Domain;
using VmsMap.Outbox;

namespace VmsMap.Db
{
  /// <summary>
  /// The one way an event enters the journal. It writes the event and, in the same transaction, the
  /// outbox row that announces it — so a crash between the commit and the publish cannot lose the
  /// reaction. The row is written only for an event that was actually inserted: re-publishing an
  /// old event would start a subscription created after it.
  /// </summary>
  public sealed class EventsWriter
  {
    private readonly VmsMapDbContext _db;
    private readonly IEventsService _events;

    public EventsWriter(VmsMapDbContext db, IEventsService events)
    {
      _db = db ?? throw new ArgumentNullException(nameof(db));
      _events = events ?? throw new ArgumentNullException(nameof(events));
    }

    public async Task<IReadOnlyList<string>> WriteAsync(
      List<EventDTO> events, CancellationToken ct = default)
    {
      if (events.Count == 0)
        return [];

      var ownsTransaction = _db.Database.IsRelational()
        && _db.Database.CurrentTransaction is null;
      await using var tx = ownsTransaction
        ? await _db.Database.BeginTransactionAsync(ct)
        : null;

      var inserted = await _events.InsertManyAsync(events);

      if (_db.Database.IsRelational() && inserted.Count > 0)
      {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)_db.Database.CurrentTransaction!.GetDbTransaction();

        foreach (var id in inserted)
        {
          await using var command = new NpgsqlCommand("""
            INSERT INTO map_outbox (topic, payload)
            VALUES ($1, jsonb_build_object('event_id', $2::uuid));
            """, connection, transaction);
          command.Parameters.AddWithValue(MapTopics.EventCreated);
          command.Parameters.AddWithValue(Guid.Parse(id));
          await command.ExecuteNonQueryAsync(ct);
        }
      }

      if (tx is not null)
        await tx.CommitAsync(ct);

      return inserted;
    }
  }
}
