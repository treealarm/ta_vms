using Microsoft.EntityFrameworkCore;
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

    public async Task<long> InsertManyAsync(List<EventDTO> events)
    {
      var list = new List<DBEvent>();

      foreach (var ev in events)
      {
        var objectId = Utils.ConvertObjectIdToGuid(ev.object_id)
          ?? throw new ArgumentException("An event needs an object_id", nameof(events));

        var dbEvent = new DBEvent
        {
          id = Utils.ConvertObjectIdToGuid(ev.id) ?? Utils.NewGuid(),
          timestamp = ev.timestamp == default ? DateTime.UtcNow : ev.timestamp,
          object_id = objectId,
          event_name = ev.event_name ?? string.Empty,
          event_priority = ev.event_priority,
          param0 = ev.param0,
          param1 = ev.param1
        };

        dbEvent.extra_props = ConvertExtraPropsToDB(ev.extra_props, dbEvent.id);
        list.Add(dbEvent);
      }

      if (list.Count > 0)
      {
        await _dbContext.Events.AddRangeAsync(list);
        await _dbContext.SaveChangesAsync();
      }

      return list.Count;
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
