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

      // The production path is one recursive CTE; the in-memory provider the tests use has no raw
      // SQL, so walk the same chain one query per level there.
      var rows = _db.Database.IsRelational()
        ? await QueryNodeChainAsync(rootGuid.Value)
        : await QueryNodeChainInMemoryAsync(rootGuid.Value);

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
      QueryNodeChainInMemoryAsync(Guid id)
    {
      var result = new List<(Guid, Guid?, Guid?, string)>();
      var visited = new HashSet<Guid>();
      Guid? current = id;

      while (current.HasValue && visited.Add(current.Value))
      {
        var row = await _db.Markers.AsNoTracking()
          .Where(m => m.id == current.Value)
          .Select(m => new { m.id, m.parent_id, m.owner_id, m.name })
          .FirstOrDefaultAsync();
        if (row is null)
          break;

        result.Add((row.id, row.parent_id, row.owner_id, row.name));
        current = row.parent_id;
      }

      return result;
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

      // A node under itself is a one-row cycle: the alarm aggregation would walk it forever, so
      // the write is refused where it is made rather than turned into a 500 further down.
      var selfParented = updatedList
        .FirstOrDefault(item => !string.IsNullOrEmpty(item.id) && item.id == item.parent_id);
      if (selfParented is not null)
        throw new ArgumentException($"Node '{selfParented.id}' cannot be its own parent");

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

      // A picture is a picture of an original, never of another picture: every read that answers
      // through an owner -- states, values, camera id, node kind, rights -- resolves it in one hop,
      // so the end of a chain would answer nothing. A replica dropped from another replica's
      // canvas is stored as a replica of the node both show, and the DTO carries that back.
      foreach (var kvp in dbUpdated.Where(kvp => kvp.Value.owner_id.HasValue))
      {
        var ownerId = kvp.Value.owner_id!.Value;

        // A stored owner is already an original: only a write that sets a new one can name a
        // picture, and the walk below is one read per level.
        if (existingEntities.TryGetValue(kvp.Value.id, out var before) && before.owner_id == ownerId)
          continue;

        var original = await ResolveOwnerAsync(ownerId, kvp.Value.id);
        if (original == ownerId)
          continue;

        kvp.Value.owner_id = original;
        kvp.Key.owner_id = Utils.ConvertGuidToObjectId(original);
      }

      // A move under the node's own descendant would close a loop the alarm and rights walks cannot
      // leave; whatever rearranges the tree -- the editor's "move to" among them -- is refused here,
      // before any tracked row is touched.
      var moves = dbUpdated
          .Where(kvp => existingEntities.TryGetValue(kvp.Value.id, out var before)
                        && before.parent_id != kvp.Value.parent_id)
          .ToDictionary(kvp => kvp.Value.id, kvp => kvp.Value.parent_id);
      if (moves.Count > 0)
        await EnsureNoCycleAsync(moves);

      // Nodes whose parent changes also change the canvas their figure is drawn on.
      var moved = new List<Guid>();

      // save through EF Core
      foreach (var kvp in dbUpdated)
      {
        if (existingEntities.TryGetValue(kvp.Value.id, out var entity))
        {
          // Copy the incoming values onto the tracked row. Square called Update() on the
          // unchanged entity, so a rename or a move to another parent was silently dropped.
          if (entity.parent_id != kvp.Value.parent_id)
            moved.Add(entity.id);
          entity.parent_id = kvp.Value.parent_id;
          entity.owner_id = kvp.Value.owner_id;
          entity.name = kvp.Value.name;
        }
        else if (_db.Database.IsRelational())
        {
          // Created-or-updated in one statement. Nodes are created from several paths that run at
          // the same time -- the resync's batches, a config event, the demo seeder -- and all of
          // them derive the same id, so "read, then insert" leaves a window where two writers both
          // insert and one dies on the primary key. The row did not exist when this call read the
          // tree, so an existing one now was written by such a writer; taking this call's values is
          // the same answer a normal update would give.
          await _db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO objects (id, parent_id, owner_id, name)
            VALUES ({kvp.Value.id}, {kvp.Value.parent_id}, {kvp.Value.owner_id}, {kvp.Value.name})
            ON CONFLICT (id) DO UPDATE SET
              parent_id = EXCLUDED.parent_id,
              owner_id  = EXCLUDED.owner_id,
              name      = EXCLUDED.name;
            """);
        }
        else
        {
          _db.Markers.Add(kvp.Value);
        }
      }

      await _db.SaveChangesAsync();

      if (moved.Count > 0)
      {
        // A move takes the whole subtree with it, so the canvas of every figure below a moved
        // node is recomputed, not only the moved node's own: a container moved to another canvas
        // brings its children there.
        var subtree = await GetSubtreeIdsAsync(
            moved.Select(id => Utils.ConvertGuidToObjectId(id)).ToList());
        var subtreeIds = subtree
            .Select(Utils.ConvertObjectIdToGuid)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .ToList();
        var figures = await _db.GeoObjects
            .Where(g => subtreeIds.Contains(g.id))
            .ToListAsync();
        if (figures.Count > 0)
        {
          var canvasIds = await CanvasResolver.ResolveForAsync(
              _db, figures.Select(f => f.id).ToList());
          foreach (var figure in figures)
            figure.canvas_id = canvasIds[figure.id];
          await _db.SaveChangesAsync();
        }
      }

      // write the assigned id back into the DTO
      foreach (var pair in dbUpdated)
      {
        pair.Key.id = Utils.ConvertGuidToObjectId(pair.Value.id);
      }
    }


    /// The node an owner must name: the one that keeps its own rows. A replica's owner is followed
    /// to the end of the chain, and a node that would own itself, or a chain that returns to where
    /// it started, is refused rather than written.
    private async Task<Guid> ResolveOwnerAsync(Guid ownerId, Guid nodeId)
    {
      var visited = new HashSet<Guid>();
      var current = ownerId;

      while (true)
      {
        if (current == nodeId)
          throw new ArgumentException(
              $"Node '{Utils.ConvertGuidToObjectId(nodeId)}' cannot be its own owner");

        if (!visited.Add(current))
          throw new ArgumentException(
              $"Node '{Utils.ConvertGuidToObjectId(ownerId)}' belongs to an owner cycle");

        var next = await _db.Markers.AsNoTracking()
            .Where(m => m.id == current)
            .Select(m => m.owner_id)
            .FirstOrDefaultAsync();

        if (next is null)
          return current;

        current = next.Value;
      }
    }

    /// Walks up from each new parent: reaching the moved node itself means the move closes a loop.
    /// A node moved in the same request is followed where it will go, not where it is now.
    private async Task EnsureNoCycleAsync(IReadOnlyDictionary<Guid, Guid?> moves)
    {
      foreach (var (id, parent) in moves)
      {
        var visited = new HashSet<Guid>();
        var at = parent;
        while (at is not null)
        {
          if (at.Value == id)
            throw new ArgumentException(
                $"Node '{Utils.ConvertGuidToObjectId(id)}' cannot be moved under its own descendant");
          if (!visited.Add(at.Value))
            break;

          at = moves.TryGetValue(at.Value, out var reparented)
              ? reparented
              : await _db.Markers.AsNoTracking()
                  .Where(m => m.id == at.Value)
                  .Select(m => m.parent_id)
                  .FirstOrDefaultAsync();
        }
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
      // all of those behind. The in-memory provider has no ExecuteDelete and no cascades, so the
      // tests take the explicit path; production always takes the single statement.
      if (!_db.Database.IsRelational())
      {
        var rows = await _db.Markers
          .Where(m => guids.Contains(m.id))
          .ToListAsync();
        _db.Markers.RemoveRange(rows);
        await _db.SaveChangesAsync();
        return rows.Count;
      }

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

      // The in-memory provider the tests use has no raw SQL; walk the same subtree one level at a
      // time there. Production always takes the recursive CTE below.
      if (!_db.Database.IsRelational())
      {
        var frontier = guids.ToHashSet();
        var visited = new HashSet<Guid>();

        while (frontier.Count > 0)
        {
          var level = frontier.ToList();
          frontier.Clear();

          var ids = await _db.Markers.AsNoTracking()
            .Where(m => level.Contains(m.id))
            .Select(m => m.id)
            .ToListAsync();
          foreach (var id in ids)
          {
            if (visited.Add(id))
              result.Add(Utils.ConvertGuidToObjectId(id));
          }

          // A replica is not a child: it stands on its own canvas and names the node it shows.
          // Without the owner link here, deleting a node left its pictures behind.
          var children = await _db.Markers.AsNoTracking()
            .Where(m => (m.parent_id.HasValue && level.Contains(m.parent_id.Value))
              || (m.owner_id.HasValue && level.Contains(m.owner_id.Value)))
            .Select(m => m.id)
            .ToListAsync();
          foreach (var child in children)
          {
            if (!visited.Contains(child))
              frontier.Add(child);
          }
        }

        return result;
      }

      // A recursive CTE rather than a query per node (Square's GetAllChildren) or per level
      // (GetByChildIdAsync): the tree is one table, and Postgres walks it in one statement.
      await using var cmd = CreateCommand("""
        WITH RECURSIVE sub AS (
          SELECT id FROM objects WHERE id = ANY(@ids)
          UNION
          -- Children, and replicas, which are not children: a replica stands on its own canvas
          -- and names the node it shows.
          SELECT o.id FROM objects o JOIN sub s ON o.parent_id = s.id OR o.owner_id = s.id
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
