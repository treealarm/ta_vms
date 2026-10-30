using VmsMap.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;

namespace VmsMap.Db
{
  internal class MapDb : IMapService
  {
    private readonly VmsMapDbContext _db;
    public MapDb(
        VmsMapDbContext db)
    {
      _db = db;
    }

    public async Task<Dictionary<string, BaseMarkerDTO>> GetAsync(List<string> ids)
    {
      var guids = ids
          .Select(Utils.ConvertObjectIdToGuid)
          .Where(g => g != null)
          .Select(g => g.Value)
          .ToList();

      if (guids.Count == 0)
        return new Dictionary<string, BaseMarkerDTO>();

      var entities = await _db.Markers
          .Where(m => guids.Contains(m.id))
          .ToListAsync();

      var result = new Dictionary<string, BaseMarkerDTO>();

      foreach (var entity in entities)
      {
        var dto = new MarkerDTO();
        entity.CopyAllTo(dto);

        // the dictionary key stays the ObjectId string, as callers expect
        var key = Utils.ConvertGuidToObjectId(entity.id);
        result[key] = dto;
      }

      return result;
    }
    public async Task<BaseMarkerDTO> GetAsync(string id)
    {
      var guid = Utils.ConvertObjectIdToGuid(id);
      if (guid == null)
        return null;

      var entity = await _db.Markers.FirstOrDefaultAsync(m => m.id == guid.Value);
      if (entity == null)
        return null;

      var dto = new MarkerDTO();
      entity.CopyAllTo(dto); // PropertyCopy's extension
      return dto;
    }
    public async Task<BaseMarkerDTO> GetParent(string id)
    {
      var guid = Utils.ConvertObjectIdToGuid(id);
      if (guid == null)
        return null;

      var entity = await _db.Markers
          .Where(m => m.id == guid.Value)
          .Select(m => m.parent_id)
          .FirstOrDefaultAsync();

      if (entity == Guid.Empty)
        return null;

      var parent = await _db.Markers.FirstOrDefaultAsync(m => m.id == entity);
      if (parent == null)
        return null;

      var dto = new MarkerDTO();
      parent.CopyAllTo(dto);
      return dto;
    }
    public async Task<Dictionary<string, BaseMarkerDTO>> GetByParentIdsAsync(
        List<string> parent_ids,
        string start_id,
        string end_id,
        int count)
    {
      // parent_ids to Guid first, skipping empty strings
      var parentGuids = parent_ids
          .Where(id => !string.IsNullOrEmpty(id))
          .Select(id => Utils.ConvertObjectIdToGuid(id))
          .Where(g => g.HasValue)
          .Select(g => g.Value)
          .ToList();

      // whether any parent_id was empty or null
      bool includeNull = parent_ids.Any(string.IsNullOrEmpty);


      if (!parentGuids.Any() && !includeNull)
        return new Dictionary<string, BaseMarkerDTO>();

      IQueryable<DBMarker> query = _db.Markers
          .Where(m => (m.parent_id.HasValue && parentGuids.Contains(m.parent_id.Value))
                      || (!m.parent_id.HasValue && includeNull))
          .OrderBy(m => m.id);


      // filter by start_id / end_id
      if (!string.IsNullOrEmpty(start_id))
      {
        var startGuid = Utils.ConvertObjectIdToGuid(start_id);
        if (startGuid != null)
        {
          query = query.Where(m => m.id.CompareTo(startGuid.Value) >= 0);            
        }
      }
      else if (!string.IsNullOrEmpty(end_id))
      {
        var endGuid = Utils.ConvertObjectIdToGuid(end_id);
        if (endGuid != null)
        {
          query = query.Where(m => m.id.CompareTo(endGuid.Value) <= 0)
                       .OrderByDescending(m => m.id);
        }
      }

      var list = await query.Take(count).ToListAsync();

      // paged backwards, so return in ascending order
      if (!string.IsNullOrEmpty(end_id))
        list.Sort((x, y) => x.id.CompareTo(y.id));

      return list.ToDictionary(m => Utils.ConvertGuidToObjectId(m.id), m =>
      {
        var dto = new BaseMarkerDTO();
        m.CopyAllTo(dto);
        return dto;
      });
    }


    public async Task<Dictionary<string, BaseMarkerDTO>> GetByParentIdAsync(
      string parent_id,
      string start_id,
      string end_id,
      int count
    )
    {
      return await GetByParentIdsAsync(new List<string>() { parent_id }, start_id, end_id, count);
    }

    public async Task<Dictionary<string, BaseMarkerDTO>> GetByNameAsync(string name)
    {
      var list = await _db.Markers
          .Where(m => EF.Functions.ILike(m.name, $"%{name}%"))
          .OrderBy(m => m.name)
          .Take(20)
          .ToListAsync();

      return list.ToDictionary(
          m => Utils.ConvertGuidToObjectId(m.id), // key: the ObjectId string
          m =>
          {
            var dto = new BaseMarkerDTO();
            m.CopyAllTo(dto);
            return dto;
          });
    }
    public async Task<List<BaseMarkerDTO>> GetByChildIdAsync(string object_id)
    {
      var rootGuid = Utils.ConvertObjectIdToGuid(object_id);
      if (rootGuid == null)
      {
        return new List<BaseMarkerDTO>();
      }

      var rows = await QueryNodeChainAsync(rootGuid.Value);

      var byId = rows.ToDictionary(r => r.Id);
      var result = new List<BaseMarkerDTO>();

      // Walk up from the object to the root in memory; the CTE returned the whole chain.
      Guid? current = rootGuid;
      var visited = new HashSet<Guid>();
      while (current.HasValue
             && byId.TryGetValue(current.Value, out var row)
             && visited.Add(current.Value))
      {
        result.Add(new BaseMarkerDTO
        {
          id = Utils.ConvertGuidToObjectId(row.Id),
          parent_id = row.ParentId.HasValue
            ? Utils.ConvertGuidToObjectId(row.ParentId.Value)
            : null,
          owner_id = row.OwnerId.HasValue
            ? Utils.ConvertGuidToObjectId(row.OwnerId.Value)
            : null,
          name = row.Name
        });

        current = row.ParentId;
      }

      return result;
    }

    public async Task<List<BaseMarkerDTO>> GetAllChildren(string parent_id)
    {
      var subtree = await GetSubtreeIdsAsync(new[] { parent_id });
      var root = Utils.ConvertObjectIdToGuid(parent_id);

      var guids = subtree
          .Select(Utils.ConvertObjectIdToGuid)
          .Where(g => g.HasValue)
          .Select(g => g!.Value)
          .Where(g => g != root)
          .ToList();

      if (guids.Count == 0)
      {
        return new List<BaseMarkerDTO>();
      }

      var markers = await _db.Markers
          .Where(m => guids.Contains(m.id))
          .ToListAsync();

      return markers.Select(ConvertMarkerDB2DTO).ToList();
    }

    private async Task<List<(Guid Id, Guid? ParentId, Guid? OwnerId, string Name)>>
      QueryNodeChainAsync(Guid id)
    {
      var result = new List<(Guid, Guid?, Guid?, string)>();

      // UNION, not UNION ALL: a cycle in parent_id must not spin the query (or the walk above).
      await using var cmd = CreateCommand("""
        WITH RECURSIVE up AS (
          SELECT id, parent_id, owner_id, name FROM objects WHERE id = @id
          UNION
          SELECT o.id, o.parent_id, o.owner_id, o.name
            FROM objects o JOIN up ON o.id = up.parent_id
        )
        SELECT id, parent_id, owner_id, name FROM up;
        """);
      cmd.Parameters.Add(new NpgsqlParameter("id", id));

      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync())
      {
        result.Add((
          reader.GetGuid(0),
          reader.IsDBNull(1) ? null : reader.GetGuid(1),
          reader.IsDBNull(2) ? null : reader.GetGuid(2),
          reader.GetString(3)));
      }

      return result;
    }

    private DbCommand CreateCommand(string sql)
    {
      var connection = _db.Database.GetDbConnection();

      if (connection.State != ConnectionState.Open)
      {
        connection.Open();
      }

      var cmd = connection.CreateCommand();
      cmd.CommandText = sql;
      cmd.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
      return cmd;
    }

    BaseMarkerDTO ConvertMarkerDB2DTO(DBMarker dbMarker)
    {
      if (dbMarker == null)
      {
        return null;
      }

      BaseMarkerDTO result = new BaseMarkerDTO();
      dbMarker.CopyAllTo(result);
      return result;
    }
    Dictionary<string, BaseMarkerDTO> ConvertMarkerListDB2DTO(List<DBMarker> dbMarkers)
    {
      var result = new Dictionary<string, BaseMarkerDTO>();

      foreach (var dbItem in dbMarkers)
      {
        result.Add(Utils.ConvertGuidToObjectId(dbItem.id), ConvertMarkerDB2DTO(dbItem));
      }

      return result;
    }
    public async Task<Dictionary<string, BaseMarkerDTO>> GetTopChildren(List<string> parentIds)
    {
      // parentIds to Guid, skipping empty or malformed ones
      var parentGuids = parentIds
          .Select(Utils.ConvertObjectIdToGuid)
          .Where(g => g.HasValue)
          .Select(g => g.Value)
          .ToList();

      if (!parentGuids.Any())
        return new Dictionary<string, BaseMarkerDTO>();

      // one child per parent_id, the lowest id
      var topChildren = await _db.Markers
          .Where(m => m.parent_id.HasValue && parentGuids.Contains(m.parent_id.Value))
          .GroupBy(m => m.parent_id.Value)
          .Select(g => g.OrderBy(m => m.id).FirstOrDefault())
          .ToListAsync();

      var dict = new Dictionary<string, BaseMarkerDTO>();
      foreach (var entity in topChildren)
      {
        if (entity != null)
        {
          var dto = new BaseMarkerDTO
          {
            id = Utils.ConvertGuidToObjectId(entity.id),
          };
          dict[Utils.ConvertGuidToObjectId(entity.parent_id??Guid.Empty)] = dto;
        }
      }

      return dict;
    }

    public async Task UpdateHierarchyAsync(IEnumerable<BaseMarkerDTO> updatedList)
    {
      if (!updatedList.Any())
        return;

      var ids = updatedList
        .Select(u => Utils.ConvertObjectIdToGuid(u.id))
        .Where(g => g.HasValue)
        .Select(g => g.Value)
        .ToList();

      var existingEntities = await _db.Markers
          .Where(m => ids.Contains(m.id))
          .ToDictionaryAsync(m => m.id);

      var dbUpdated = new Dictionary<BaseMarkerDTO, DBMarker>();

      foreach (var item in updatedList)
      {
        var dbObj = new DBMarker();
        item.CopyAllTo(dbObj);

        // parent_id and owner_id are nullable Guids
        dbObj.parent_id = string.IsNullOrEmpty(item.parent_id)
            ? null
            : Utils.ConvertObjectIdToGuid(item.parent_id);

        dbObj.owner_id = string.IsNullOrEmpty(item.owner_id)
            ? null
            : Utils.ConvertObjectIdToGuid(item.owner_id);

        // id
        if (string.IsNullOrEmpty(item.id))
        {
          var newGuid = Guid.NewGuid();
          dbObj.id = Utils.ConvertObjectIdToGuid(
              Utils.ConvertGuidToObjectId(newGuid)) ?? newGuid;
        }

        dbUpdated.Add(item, dbObj);
      }

      // save through EF Core
      foreach (var kvp in dbUpdated)
      {
        if (existingEntities.TryGetValue(kvp.Value.id, out var entity))
        {
          // Copy the incoming values onto the tracked row. Square called Update() on the
          // unchanged entity, so a rename or a move to another parent was silently dropped.
          entity.parent_id = kvp.Value.parent_id;
          entity.owner_id = kvp.Value.owner_id;
          entity.name = kvp.Value.name;
        }
        else
        {
          _db.Markers.Add(kvp.Value);
        }
      }

      await _db.SaveChangesAsync();

      // write the assigned id back into the DTO
      foreach (var pair in dbUpdated)
      {
        pair.Key.id = Utils.ConvertGuidToObjectId(pair.Value.id);
      }
    }


    public async Task<long> RemoveAsync(List<string> ids)
    {
      var subtree = await GetSubtreeIdsAsync(ids);

      var guids = subtree
          .Select(Utils.ConvertObjectIdToGuid)
          .Where(g => g.HasValue)
          .Select(g => g.Value)
          .ToList();

      if (!guids.Any())
        return 0;

      // The named nodes and everything under them. Their states, alarms, events, tracks, rights,
      // geometry, values and diagrams follow through ON DELETE CASCADE in the schema; Square left
      // all of those behind.
      return await _db.Markers
        .Where(m => guids.Contains(m.id))
        .ExecuteDeleteAsync();
    }

    public async Task<HashSet<string>> GetSubtreeIdsAsync(IReadOnlyCollection<string> rootIds)
    {
      var result = new HashSet<string>();

      var guids = rootIds
          .Select(Utils.ConvertObjectIdToGuid)
          .Where(g => g.HasValue)
          .Select(g => g!.Value)
          .ToArray();

      if (guids.Length == 0)
      {
        return result;
      }

      // A recursive CTE rather than a query per node (Square's GetAllChildren) or per level
      // (GetByChildIdAsync): the tree is one table, and Postgres walks it in one statement.
      await using var cmd = CreateCommand("""
        WITH RECURSIVE sub AS (
          SELECT id FROM objects WHERE id = ANY(@ids)
          UNION
          SELECT o.id FROM objects o JOIN sub s ON o.parent_id = s.id
        )
        SELECT id FROM sub;
        """);
      cmd.Parameters.Add(new NpgsqlParameter("ids", guids));

      await using var reader = await cmd.ExecuteReaderAsync();
      while (await reader.ReadAsync())
      {
        result.Add(Utils.ConvertGuidToObjectId(reader.GetGuid(0)));
      }

      return result;
    }

    private static List<DBMarkerProp> ConvertDTO2Property(IObjectProps propsIn)
    {
      var props = propsIn as IObjectProps;
      var extra_props = new List<DBMarkerProp>();

      if (props.extra_props == null)
      {
        return extra_props;
      }

      extra_props = props.extra_props
        .Select(p => new DBMarkerProp
        {
          prop_name = p.prop_name,
          str_val = p.str_val,
          visual_type = p.visual_type,
          object_id = Utils.ConvertObjectIdToGuid(props.id)??Guid.Empty,
        })
        .ToList();


      return extra_props;
    }

    async Task IMapService.UpdatePropAsync(IEnumerable<ObjPropsDTO> updatedObjs)
    {
      if (!updatedObjs.Any())
        return;

      // the objects' Guids
      var objIds = updatedObjs
          .Select(x => Utils.ConvertObjectIdToGuid(x.id))
          .Where(g => g.HasValue)
          .Select(g => g.Value)
          .ToList();

      // load their properties
      var existingProps = await _db.Properties
          .Where(p => objIds.Contains(p.object_id))
          .ToListAsync();

      // group by object
      var existingByObj = existingProps
          .GroupBy(p => p.object_id)
          .ToDictionary(g => g.Key, g => g.ToList());

      var toAdd = new List<DBMarkerProp>();
      var toUpdate = new List<DBMarkerProp>();
      var toRemove = new List<DBMarkerProp>();

      foreach (var dto in updatedObjs)
      {
        var objId = Utils.ConvertObjectIdToGuid(dto.id) ?? Guid.Empty;
        if (objId == Guid.Empty)
          continue;

        var incoming = dto.extra_props
           .GroupBy(p => p.prop_name)
           .ToDictionary(g => g.Key, g => g.First());


        // the object's current properties
        existingByObj.TryGetValue(objId, out var existingForObj);
        existingForObj ??= new List<DBMarkerProp>();

        // 1. update or add
        foreach (var kvp in incoming)
        {
          var propName = kvp.Key;
          var dtoProp = kvp.Value;

          var existingProp = existingForObj.FirstOrDefault(p => p.prop_name == propName);
          if (existingProp != null)
          {
            // update
            existingProp.str_val = dtoProp.str_val;
            existingProp.visual_type = dtoProp.visual_type;
            toUpdate.Add(existingProp);
          }
          else
          {
            // add
            toAdd.Add(new DBMarkerProp
            {
              object_id = objId,
              prop_name = dtoProp.prop_name,
              str_val = dtoProp.str_val,
              visual_type = dtoProp.visual_type
            });
          }
        }

        // 2. remove the ones the input no longer has
        var toDelete = existingForObj
            .Where(p => !incoming.ContainsKey(p.prop_name))
            .ToList();
        toRemove.AddRange(toDelete);
      }

      // apply
      if (toRemove.Any())
        _db.Properties.RemoveRange(toRemove);

      if (toUpdate.Any())
        _db.Properties.UpdateRange(toUpdate);

      if (toAdd.Any())
        await _db.Properties.AddRangeAsync(toAdd);

      await _db.SaveChangesAsync();
    }

    public async Task UpdatePropNotDeleteAsync(IEnumerable<IObjectProps> listUpdate)
    {
      if (!listUpdate.Any())
        return;

      var idsToUpdate = listUpdate
          .Select(x => Utils.ConvertObjectIdToGuid(x.id) ?? Guid.Empty)
          .Where(g => g != Guid.Empty)
          .ToList();

      var existingProps = await _db.Properties
          .Where(p => idsToUpdate.Contains(p.object_id))
          .ToListAsync();

      var groupedExisting = existingProps
          .GroupBy(p => p.object_id)
          .ToDictionary(g => g.Key, g => g.ToList());

      var toAdd = new List<DBMarkerProp>();

      foreach (var dto in listUpdate)
      {
        if (dto?.extra_props?.Any() != true)
          continue;

        var objId = Utils.ConvertObjectIdToGuid(dto.id) ?? Guid.Empty;
        if (objId == Guid.Empty)
          continue;

        groupedExisting.TryGetValue(objId, out var existingForObj);
        existingForObj ??= new List<DBMarkerProp>();

        foreach (var prop in dto.extra_props)
        {
          var existing = existingForObj.FirstOrDefault(p => p.prop_name == prop.prop_name);
          if (existing != null)
          {
            // update the values only
            existing.str_val = prop.str_val;
            existing.visual_type = prop.visual_type;
            _db.Properties.Update(existing);
          }
          else
          {
            // add a new property
            toAdd.Add(new DBMarkerProp
            {
              object_id = objId,
              prop_name = prop.prop_name,
              str_val = prop.str_val,
              visual_type = prop.visual_type
            });
          }
        }
      }

      if (toAdd.Any())
        await _db.Properties.AddRangeAsync(toAdd);

      await _db.SaveChangesAsync();
    }

    public async Task<ObjPropsDTO> GetPropAsync(string id)
    {
      var guidId = Utils.ConvertObjectIdToGuid(id) ?? Guid.Empty;
      if (guidId == Guid.Empty)
        return null;

      // all properties of the object
      var props = await _db.Properties
          .Where(p => p.object_id == guidId)
          .ToListAsync();

      // build the DTO
      var dto = new ObjPropsDTO
      {
        id = id,
        extra_props = props.Select(p => new ObjExtraPropertyDTO
        {
          prop_name = p.prop_name,
          str_val = p.str_val,
          visual_type = p.visual_type
        }).ToList()
      };

      return dto;
    }
    public async Task<Dictionary<string, ObjPropsDTO>> GetPropsAsync(List<string> ids)
    {
      var guidIds = ids
          .Select(Utils.ConvertObjectIdToGuid)
          .Where(g => g.HasValue)
          .Select(g => g.Value)
          .ToList();

      if (!guidIds.Any())
        return new Dictionary<string, ObjPropsDTO>();

      // all properties of a list of objects
      var props = await _db.Properties
          .Where(p => guidIds.Contains(p.object_id))
          .ToListAsync();

      // group by object
      var grouped = props.GroupBy(p => p.object_id);

      var result = grouped.ToDictionary(
          g => Utils.ConvertGuidToObjectId(g.Key),
          g => new ObjPropsDTO
          {
            id = Utils.ConvertGuidToObjectId(g.Key),
            extra_props = g.Select(p => new ObjExtraPropertyDTO
            {
              prop_name = p.prop_name,
              str_val = p.str_val,
              visual_type = p.visual_type
            }).ToList()
          });

      return result;
    }

    public async Task<List<ObjPropsDTO>> GetPropByValuesAsync(
        ObjPropsSearchDTO propFilter,
        string start_id,
        int forward,
        int count)
    {
      var query = _db.Properties.AsQueryable();

      // paging by Guid (owner_id)
      if (!string.IsNullOrEmpty(start_id))
      {
        var startGuid = Utils.ConvertObjectIdToGuid(start_id) ?? Guid.Empty;

        if (forward > 0)
          query = query.Where(p => p.object_id.CompareTo(startGuid) > 0);
        else
          query = query.Where(p => p.object_id.CompareTo(startGuid) < 0);
      }

      // property filter
      if (propFilter?.props?.Any() == true)
      {
        foreach (var prop in propFilter.props)
        {
          var propName = prop.prop_name;
          var propValue = prop.str_val;

          query = query.Where(p =>
              p.prop_name == propName &&
              p.str_val == propValue
          );
        }
      }

      // order by owner_id
      query = forward > 0
          ? query.OrderBy(p => p.object_id)
          : query.OrderByDescending(p => p.object_id);

      query = query.Take(count * 10);
      // load the properties
      var props = await query.ToListAsync();
      // ×10, because several rows make one object

      // group by object
      var grouped = props
          .GroupBy(p => p.object_id)
          .Take(count) // keep only the requested number of objects
          .ToList();

      var result = grouped.Select(g => new ObjPropsDTO
      {
        id = Utils.ConvertGuidToObjectId(g.Key),
        extra_props = g.Select(p => new ObjExtraPropertyDTO
        {
          prop_name = p.prop_name,
          str_val = p.str_val,
          visual_type = p.visual_type
        }).ToList()
      }).ToList();

      // paged backwards, so return ascending
      if (forward <= 0)
        result.Reverse();

      return result;
    }

    public async Task<Dictionary<string, BaseMarkerDTO>> GetOwnersAsync(List<string> ids)
    {
      // the ObjectId strings to Guids
      var guids = ids
          .Select(Utils.ConvertObjectIdToGuid)
          .Where(g => g.HasValue)
          .Select(g => g.Value)
          .ToList();

      if (!guids.Any())
        return new Dictionary<string, BaseMarkerDTO>();

      // all objects with these ids
      var listObjects = await _db.Markers
          .Where(m => guids.Contains(m.id))
          .ToListAsync();

      // replicas: objects with owner_id != null
      var views = listObjects
          .Where(m => m.owner_id.HasValue)
          .ToList();

      if (!views.Any())
      {
        // no replicas among them
        return ConvertMarkerListDB2DTO(listObjects);
      }

      // owners: objects with owner_id == null
      var owners = listObjects
          .Where(m => !m.owner_id.HasValue)
          .ToList();

      var ownerGuids = views
          .Select(v => v.owner_id.Value)
          .ToList();

      // owners not among listObjects
      var ownersDb = await _db.Markers
          .Where(m => !m.owner_id.HasValue && ownerGuids.Contains(m.id))
          .ToListAsync();

      owners = owners.Union(ownersDb).ToList();

      return ConvertMarkerListDB2DTO(owners);
    }

    public async Task<Dictionary<string, BaseMarkerDTO>> GetOwnersAndViewsAsync(List<string> ids)
    {
      // the strings to Guids
      var guids = ids
          .Select(Utils.ConvertObjectIdToGuid)
          .Where(g => g.HasValue)
          .Select(g => g.Value)
          .ToList();

      if (!guids.Any())
        return new Dictionary<string, BaseMarkerDTO>();

      // all markers with these ids
      var listObjects = await _db.Markers
          .Where(m => guids.Contains(m.id))
          .ToListAsync();

      // replicas: objects with owner_id != null
      var views = listObjects
          .Where(m => m.owner_id.HasValue)
          .ToList();

      if (!views.Any())
      {
        return ConvertMarkerListDB2DTO(listObjects);
      }

      // owners already among them (owner_id == null)
      var owners = listObjects
          .Where(m => !m.owner_id.HasValue)
          .ToList();

      // the replicas' owner ids, for the ones still missing
      var ownerGuids = views
          .Select(v => v.owner_id.Value)
          .ToList();

      // owners not among those already loaded
      var ownersDb = ownerGuids.Any()
          ? await _db.Markers
              .Where(m => !m.owner_id.HasValue && ownerGuids.Contains(m.id))
              .ToListAsync()
          : new List<DBMarker>();

      // Replicas, the owners that were in the input, and the owners fetched for the replicas.
      // Square unioned only the first and the third, so a plain object in the input vanished.
      var allObjects = views
          .Union(owners)
          .Union(ownersDb)
          .ToList();

      return ConvertMarkerListDB2DTO(allObjects);
    }
    public async Task<FiguresDTO> GetFigures(Dictionary<string, GeoObjectDTO> geo)
    {
      var result = new FiguresDTO();

      if (geo == null)
      {
        return result;
      }

      var ids = geo.Keys.ToList();

      var tree = await GetAsync(ids);

      var props = await GetPropsAsync(ids);

      foreach (var item in tree.Values)
      {
        if (geo.TryGetValue(item.id, out var geoPart))
        {
          FigureZoomedDTO retItem = null;

          var figure = new FigureGeoDTO();
          figure.radius = geoPart.radius;
          figure.geometry = geoPart.location;
          result.figs.Add(figure);
          retItem = figure;

          if (retItem != null)
          {
            retItem.id = item.id;
            retItem.name = item.name;
            retItem.parent_id = item.parent_id;
            retItem.zoom_level = geoPart.zoom_level?.ToString();

            if (props.TryGetValue(retItem.id, out var objProp))
            {
              if (retItem.extra_props != null)
              {
                retItem.extra_props.AddRange(objProp.extra_props);
              }
              else
              {
                retItem.extra_props = objProp.extra_props;
              }
            }
          }

        }
      }
      return result;
    }
  }
}
