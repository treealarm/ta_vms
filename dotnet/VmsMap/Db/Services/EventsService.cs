using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using VmsMap.Domain;

namespace VmsMap.Db
{
  // The event journal. Reads are paginated by keyset — (timestamp, id) of the last row the caller
  // saw — rather than by an offset, so an insert between two pages cannot make a row disappear.
  internal class EventsService : IEventsService
  {
    private readonly VmsMapDbContext _dbContext;
    private readonly IMapService _mapService;

    public EventsService(VmsMapDbContext context, IMapService mapService)
    {
      _dbContext = context;
      _mapService = mapService;
    }

    public static List<ObjExtraPropertyDTO> ConvertDBExtraProps2DTO(List<EventProp>? props)
    {
      var retVal = new List<ObjExtraPropertyDTO>();

      if (props == null)
      {
        return retVal;
      }

      foreach (var prop in props)
      {
        retVal.Add(new ObjExtraPropertyDTO
        {
          prop_name = prop.prop_name,
          str_val = prop.str_val,
          visual_type = prop.visual_type
        });
      }

      return retVal;
    }

    private static EventDTO ConvertDB2DTO(DBEvent dbEvent)
    {
      return new EventDTO
      {
        timestamp = dbEvent.timestamp,
        id = Utils.ConvertGuidToObjectId(dbEvent.id),
        object_id = Utils.ConvertGuidToObjectId(dbEvent.object_id),
        event_name = dbEvent.event_name,
        event_priority = dbEvent.event_priority,
        param0 = dbEvent.param0,
        param1 = dbEvent.param1,
        extra_props = ConvertDBExtraProps2DTO(dbEvent.extra_props)
      };
    }

    public static List<EventProp> ConvertExtraPropsToDB(
        List<ObjExtraPropertyDTO>? extra_props,
        Guid owner_id)
    {
      var ep_db = new List<EventProp>();

      if (extra_props == null)
        return ep_db;

      foreach (var prop in extra_props)
      {
        if (string.IsNullOrEmpty(prop.prop_name))
          continue;

        var newProp = new EventProp
        {
          id = Utils.NewGuid(),
          prop_name = prop.prop_name,
          visual_type = prop.visual_type,
          owner_id = owner_id
        };

        // Numbers and instants are normalised so that filters and sorting can compare strings.
        if (prop.visual_type == VisualTypes.Double)
        {
          if (double.TryParse(prop.str_val, NumberStyles.Any, CultureInfo.InvariantCulture, out var dbl))
            newProp.str_val = dbl.ToString(CultureInfo.InvariantCulture);
          else
            newProp.str_val = prop.str_val;
        }
        else if (prop.visual_type == VisualTypes.DateTime)
        {
          if (DateTime.TryParse(prop.str_val, out var dt))
            newProp.str_val = dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
          else
            newProp.str_val = prop.str_val;
        }
        else
        {
          newProp.str_val = prop.str_val;
        }

        ep_db.Add(newProp);
      }

      return ep_db;
    }

    // An event enters the journal at most once. The id is the caller's (a deterministic one for
    // anything replayable), and an unrecognised id is an error rather than a silent random
    // replacement: a NewGuid() here would quietly make every redelivery a new event.
    public async Task<IReadOnlyList<string>> InsertManyAsync(List<EventDTO> events)
    {
      if (events.Count == 0)
        return [];

      return _dbContext.Database.IsRelational()
        ? await InsertRelationalAsync(events)
        : await InsertInMemoryAsync(events);
    }

    private async Task<IReadOnlyList<string>> InsertRelationalAsync(List<EventDTO> events)
    {
      var connection = (NpgsqlConnection)_dbContext.Database.GetDbConnection();
      var transaction = (NpgsqlTransaction?)_dbContext.Database.CurrentTransaction?.GetDbTransaction();

      var openedHere = connection.State != System.Data.ConnectionState.Open;
      if (openedHere)
        await _dbContext.Database.OpenConnectionAsync();

      try
      {
        var inserted = new List<string>(events.Count);

        foreach (var ev in events)
        {
          var id = Utils.ConvertObjectIdToGuid(ev.id)
            ?? throw new ArgumentException(
                $"Event '{ev.id}' has no valid UUID id — an event must be identifiable to be deduplicated",
                nameof(events));

          var objectId = Utils.ConvertObjectIdToGuid(ev.object_id)
            ?? throw new ArgumentException("An event needs an object_id", nameof(events));

          await using (var command = new NpgsqlCommand("""
              INSERT INTO events (id, object_id, event_name, event_priority, "timestamp", param0, param1)
              VALUES ($1, $2, $3, $4, $5, $6, $7)
              ON CONFLICT (id) DO NOTHING
              RETURNING id;
              """, connection, transaction))
          {
            command.Parameters.AddWithValue(id);
            command.Parameters.AddWithValue(objectId);
            command.Parameters.AddWithValue(ev.event_name ?? string.Empty);
            command.Parameters.AddWithValue(ev.event_priority);
            command.Parameters.AddWithValue(ev.timestamp == default ? DateTime.UtcNow : ev.timestamp);
            command.Parameters.AddWithValue((object?)ev.param0 ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)ev.param1 ?? DBNull.Value);

            var returned = await command.ExecuteScalarAsync();
            if (returned is null)
              continue; // already in the journal: a redelivery, not a second event

            inserted.Add(id.ToString());
          }

          foreach (var prop in ConvertExtraPropsToDB(ev.extra_props, id))
          {
            await using var command = new NpgsqlCommand("""
              INSERT INTO event_props (id, prop_name, str_val, visual_type, owner_id)
              VALUES ($1, $2, $3, $4, $5);
              """, connection, transaction);
            command.Parameters.AddWithValue(prop.id);
            command.Parameters.AddWithValue((object?)prop.prop_name ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)prop.str_val ?? DBNull.Value);
            command.Parameters.AddWithValue((object?)prop.visual_type ?? DBNull.Value);
            command.Parameters.AddWithValue(prop.owner_id);
            await command.ExecuteNonQueryAsync();
          }
        }

        return inserted;
      }
      finally
      {
        if (openedHere)
          await _dbContext.Database.CloseConnectionAsync();
      }
    }

    private async Task<IReadOnlyList<string>> InsertInMemoryAsync(List<EventDTO> events)
    {
      var parsed = new List<(DBEvent Event, string Id)>();

      foreach (var ev in events)
      {
        var id = Utils.ConvertObjectIdToGuid(ev.id)
          ?? throw new ArgumentException(
              $"Event '{ev.id}' has no valid UUID id — an event must be identifiable to be deduplicated",
              nameof(events));

        var objectId = Utils.ConvertObjectIdToGuid(ev.object_id)
          ?? throw new ArgumentException("An event needs an object_id", nameof(events));

        parsed.Add((new DBEvent
        {
          id = id,
          timestamp = ev.timestamp == default ? DateTime.UtcNow : ev.timestamp,
          object_id = objectId,
          event_name = ev.event_name ?? string.Empty,
          event_priority = ev.event_priority,
          param0 = ev.param0,
          param1 = ev.param1,
          extra_props = ConvertExtraPropsToDB(ev.extra_props, id),
        }, id.ToString()));
      }

      var ids = parsed.Select(p => p.Event.id).ToList();
      var existing = await _dbContext.Events
        .Where(e => ids.Contains(e.id))
        .Select(e => e.id)
        .ToListAsync();

      var fresh = parsed.Where(p => !existing.Contains(p.Event.id)).ToList();
      if (fresh.Count > 0)
      {
        await _dbContext.Events.AddRangeAsync(fresh.Select(p => p.Event));
        await _dbContext.SaveChangesAsync();
      }

      return fresh.Select(p => p.Id).ToList();
    }

    // The event router's read: one row, by the id the outbox announced. It carries no caller and
    // applies no rights — the router is the system's own reaction engine, not a viewer, and a
    // subscription configured on a node must fire whether or not some user may see that node.
    public async Task<EventDTO?> GetByIdAsync(Guid id)
    {
      var row = await _dbContext.Events
        .AsNoTracking()
        .Include(e => e.extra_props)
        .FirstOrDefaultAsync(e => e.id == id);

      return row is null ? null : ConvertDB2DTO(row);
    }

    public async Task<List<EventDTO>> GetEventsByFilter(SearchEventFilterDTO filter)
    {
      var query = _dbContext.Events.AsQueryable();

      if (filter.time_start != null)
      {
        query = query.Where(e => e.timestamp >= filter.time_start);
      }

      if (filter.time_end != null)
      {
        query = query.Where(e => e.timestamp <= filter.time_end);
      }

      // A group used to be a separate table; in one tree it is a node, so the filter is the
      // subtree of the named objects.
      if (filter.objects.Count > 0)
      {
        var subtree = await _mapService.GetSubtreeIdsAsync(filter.objects);
        var ids = subtree
          .Select(Utils.ConvertObjectIdToGuid)
          .Where(g => g.HasValue)
          .Select(g => g!.Value)
          .ToList();

        query = query.Where(e => ids.Contains(e.object_id));
      }

      if (filter.images_only)
      {
        query = query.Where(e => e.extra_props.Any(p => p.visual_type == "image_fs"));
      }

      if (filter.param0 != null)
      {
        query = query.Where(e => e.param0 == filter.param0);
      }

      if (filter.param1 != null)
      {
        query = query.Where(e => e.param1 == filter.param1);
      }

      var ascending = IsAscending(filter.sort);

      // Keyset: the cursor is the event id of the last row the caller has; the next page starts
      // strictly after it in the chosen direction.
      if (!string.IsNullOrEmpty(filter.start_id))
      {
        var cursorId = Utils.ConvertObjectIdToGuid(filter.start_id);
        if (cursorId == null)
        {
          throw new ArgumentException($"Invalid event cursor '{filter.start_id}'", nameof(filter));
        }

        var cursor = await _dbContext.Events.AsNoTracking()
          .Where(e => e.id == cursorId)
          .Select(e => new { e.timestamp, e.id })
          .FirstOrDefaultAsync()
          ?? throw new ArgumentException($"Event '{filter.start_id}' does not exist", nameof(filter));

        query = ascending
          ? query.Where(e => e.timestamp > cursor.timestamp
                          || (e.timestamp == cursor.timestamp && e.id.CompareTo(cursor.id) > 0))
          : query.Where(e => e.timestamp < cursor.timestamp
                          || (e.timestamp == cursor.timestamp && e.id.CompareTo(cursor.id) < 0));
      }

      query = ascending
        ? query.OrderBy(e => e.timestamp).ThenBy(e => e.id)
        : query.OrderByDescending(e => e.timestamp).ThenByDescending(e => e.id);

      var limit = filter.count > 0 ? filter.count : 100;
      query = query.Take(limit);

      var events = await query.Include(e => e.extra_props).ToListAsync();

      return events.Select(ConvertDB2DTO).ToList();
    }

    // Only the timestamp is sortable: it is the key the pagination uses, and silently ignoring
    // any other requested key is how Square's filter came to lie about what it returned.
    private static bool IsAscending(List<SortDTO>? sort)
    {
      if (sort == null || sort.Count == 0)
      {
        return false;
      }

      if (sort.Count > 1 || sort[0].key != "timestamp")
      {
        throw new ArgumentException(
          "Events can only be sorted by timestamp", nameof(sort));
      }

      return sort[0].order == "asc";
    }
  }
}
