using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VmsMap.Domain;

namespace VmsMap.Db
{
  /// A diagram row the map cannot hold: the region it names is not one of the type its parent is
  /// drawn from, so nothing would draw it and the editor would have to guess where it stands.
  public sealed class DiagramConfigException(string message) : Exception(message);

  internal class DiagramService : IDiagramService, IDiagramServiceInternal
  {
    private readonly VmsMapDbContext _dbContext;

    public DiagramService(VmsMapDbContext context)
    {
      _dbContext = context;
    }

    public async Task<Dictionary<string, DiagramDTO>> GetListByIdsAsync(List<string> ids)
    {
      var guidIds = ids
          .Select(Utils.ConvertObjectIdToGuid)
          .Where(g => g.HasValue)
          .Select(g => g!.Value)
          .ToList();

      if (guidIds.Count == 0)
      {
        return new Dictionary<string, DiagramDTO>();
      }

      var diagrams = await _dbContext.Diagrams
          .Where(x => guidIds.Contains(x.id))
          .ToListAsync();

      return ConvertListDB2DTO(diagrams);
    }

    public async Task UpdateListAsync(List<DiagramDTO> newObjs)
    {
      if (newObjs.Count == 0)
        return;

      var dbUpdated = newObjs
          .Select(dto => (Dto: dto, Db: ConvertDTO2DB(dto)))
          .ToList();

      var ids = dbUpdated
          .Select(x => x.Db.id)
          .Where(id => id != Guid.Empty)
          .ToList();

      var existing = await _dbContext.Diagrams
          .Where(d => ids.Contains(d.id))
          .ToListAsync();

      // A region is a key of the parent's type: a row naming one that type does not have would be
      // drawn nowhere, and its node would sit in the tree with no place at all.
      await EnsureRegionsBelongToParentsAsync(dbUpdated.Select(x => x.Db).ToList());

      var existingDict = existing.ToDictionary(e => e.id);

      // The other side of the same rule: a type is the key its children stand in, so replacing it
      // is checked against what its children name.
      await EnsureChildrenKeepTheirRegionsAsync(dbUpdated
          .Select(x => x.Db)
          .Where(row => existingDict.TryGetValue(row.id, out var stored)
                        && stored.dgr_type != row.dgr_type)
          .Select(row => (row.id, row.dgr_type))
          .ToList());

      foreach (var (dto, dbObj) in dbUpdated)
      {
        if (dbObj.id == Guid.Empty || !existingDict.ContainsKey(dbObj.id))
        {
          if (dbObj.id == Guid.Empty)
            dbObj.id = Utils.NewGuid();

          _dbContext.Diagrams.Add(dbObj);
        }
        else
        {
          var existingObj = existingDict[dbObj.id];
          existingObj.dgr_type = dbObj.dgr_type;
          existingObj.geometry = dbObj.geometry;
          existingObj.region_id = dbObj.region_id;
          existingObj.file_id = dbObj.file_id;
        }

        // The response carries the id assigned to a new row.
        dto.id = Utils.ConvertGuidToObjectId(dbObj.id);
      }

      await _dbContext.SaveChangesAsync();
    }

    /// <summary>
    /// Writes only the content fields of the named rows. A copy of a diagram writes its type and
    /// picture through to the diagram itself, and must not touch the diagram's paper or the place
    /// it stands in: a null field means "not asked" and stays as it is, an empty one clears it.
    /// </summary>
    public async Task UpdateContentAsync(List<DiagramDTO> rows)
    {
      var ids = rows
          .Select(row => Utils.ConvertObjectIdToGuid(row.id))
          .Where(id => id.HasValue)
          .Select(id => id!.Value)
          .Distinct()
          .ToList();
      if (ids.Count == 0)
        return;

      var existing = await _dbContext.Diagrams
          .Where(d => ids.Contains(d.id))
          .ToDictionaryAsync(d => d.id);

      var changes = new List<(Guid Id, Guid? Type)>();
      foreach (var dto in rows)
      {
        if (Utils.ConvertObjectIdToGuid(dto.id) is not { } id
            || !existing.TryGetValue(id, out var row)
            || dto.dgr_type is null)
          continue;

        var type = Utils.ConvertObjectIdToGuid(dto.dgr_type);
        if (type != row.dgr_type)
          changes.Add((id, type));
      }

      // The type is written through from a copy, so this is also where a change made through one
      // is checked against the children that stand in the diagram.
      await EnsureChildrenKeepTheirRegionsAsync(changes);

      foreach (var dto in rows)
      {
        if (Utils.ConvertObjectIdToGuid(dto.id) is not { } id
            || !existing.TryGetValue(id, out var row))
          continue;

        if (dto.dgr_type is not null)
          row.dgr_type = Utils.ConvertObjectIdToGuid(dto.dgr_type);
        if (dto.file_id is not null)
          row.file_id = Utils.ConvertObjectIdToGuid(dto.file_id);
      }

      await _dbContext.SaveChangesAsync();
    }

    public async Task RemoveAsync(List<string> ids)
    {
      var guidIds = ids
          .Select(Utils.ConvertObjectIdToGuid)
          .Where(g => g.HasValue)
          .Select(g => g!.Value)
          .ToList();

      if (guidIds.Count == 0)
        return;

      var diagrams = await _dbContext.Diagrams
          .Where(x => guidIds.Contains(x.id))
          .ToListAsync();

      if (diagrams.Count > 0)
      {
        _dbContext.Diagrams.RemoveRange(diagrams);
        await _dbContext.SaveChangesAsync();
      }
    }

    /// <summary>
    /// A type names the regions a diagram's direct children stand in, so replacing it — or taking it
    /// away — must not leave a child naming a key the new one does not offer: it would sit in the
    /// tree with nothing to draw it and simply vanish from every copy. The type dictionary refuses
    /// to take a region away while a node stands there; this is the same rule seen from the
    /// diagram's side. Only a change is checked, so a row written before this rule existed does not
    /// block an edit that leaves its type alone.
    /// </summary>
    private async Task EnsureChildrenKeepTheirRegionsAsync(List<(Guid Id, Guid? Type)> changes)
    {
      if (changes.Count == 0)
        return;

      var ids = changes.Select(change => change.Id).ToList();
      var children = await _dbContext.Markers.AsNoTracking()
          .Where(m => m.parent_id != null && ids.Contains(m.parent_id!.Value))
          .Select(m => new { parent = m.parent_id!.Value, child = m.id })
          .ToListAsync();
      if (children.Count == 0)
        return;

      var childIds = children.Select(child => child.child).ToList();
      var placed = await _dbContext.Diagrams.AsNoTracking()
          .Where(d => childIds.Contains(d.id) && d.region_id != null)
          .Select(d => new { d.id, d.region_id })
          .ToDictionaryAsync(d => d.id, d => d.region_id!);
      if (placed.Count == 0)
        return;

      var typeIds = changes
          .Where(change => change.Type.HasValue)
          .Select(change => change.Type!.Value)
          .Distinct()
          .ToList();
      var offered = (await _dbContext.DiagramTypeRegions.AsNoTracking()
              .Where(r => typeIds.Contains(r.diagram_type_id))
              .Select(r => new { r.diagram_type_id, r.region_key })
              .ToListAsync())
          .Select(r => (r.diagram_type_id, r.region_key))
          .ToHashSet();

      foreach (var (id, type) in changes)
      {
        var orphan = children.FirstOrDefault(child =>
            child.parent == id
            && placed.TryGetValue(child.child, out var region)
            && (!type.HasValue || !offered.Contains((type.Value, region))));
        if (orphan is null)
          continue;

        var region = placed[orphan.child];
        throw new DiagramConfigException(type.HasValue
            ? $"The diagram \"{Utils.ConvertGuidToObjectId(id)}\" cannot be drawn from the new " +
              $"type: the node \"{Utils.ConvertGuidToObjectId(orphan.child)}\" stands in the " +
              $"region \"{region}\", which that type does not offer"
            : $"The diagram \"{Utils.ConvertGuidToObjectId(id)}\" cannot lose its type: the node " +
              $"\"{Utils.ConvertGuidToObjectId(orphan.child)}\" stands in the region \"{region}\"");
      }
    }

    /// <summary>
    /// A child stands in a region of its parent's diagram type: the region is the key, and the type
    /// is where the key is written down. A row naming a region its parent's type does not offer is
    /// refused rather than stored — the editor draws nothing for it and the next person to look
    /// would have to guess whether the node is missing or the picture is.
    /// </summary>
    private async Task EnsureRegionsBelongToParentsAsync(List<DBDiagram> rows)
    {
      var placed = rows
          .Where(r => !string.IsNullOrWhiteSpace(r.region_id) && r.id != Guid.Empty)
          .ToList();
      if (placed.Count == 0)
        return;

      var ids = placed.Select(r => r.id).ToList();
      var parents = await _dbContext.Markers.AsNoTracking()
          .Where(m => ids.Contains(m.id))
          .Select(m => new { m.id, m.parent_id })
          .ToDictionaryAsync(m => m.id, m => m.parent_id);

      var parentIds = parents.Values
          .Where(parent => parent.HasValue)
          .Select(parent => parent!.Value)
          .Distinct()
          .ToList();
      var parentTypes = await _dbContext.Diagrams.AsNoTracking()
          .Where(d => parentIds.Contains(d.id) && d.dgr_type != null)
          .Select(d => new { d.id, type = d.dgr_type!.Value })
          .ToDictionaryAsync(d => d.id, d => d.type);

      var typeIds = parentTypes.Values.Distinct().ToList();
      var keys = placed.Select(r => r.region_id!).Distinct().ToList();
      var known = await _dbContext.DiagramTypeRegions.AsNoTracking()
          .Where(r => typeIds.Contains(r.diagram_type_id) && keys.Contains(r.region_key))
          .Select(r => new { r.diagram_type_id, r.region_key })
          .ToListAsync();
      var offered = known
          .Select(r => (r.diagram_type_id, r.region_key))
          .ToHashSet();

      foreach (var row in placed)
      {
        var belongs = parents.TryGetValue(row.id, out var parent)
                      && parent is { } parentId
                      && parentTypes.TryGetValue(parentId, out var typeId)
                      && offered.Contains((typeId, row.region_id!));
        if (!belongs)
          throw new DiagramConfigException(
            $"The node \"{Utils.ConvertGuidToObjectId(row.id)}\" names the region " +
            $"\"{row.region_id}\", which is not a region of the type its parent is drawn from");
      }
    }

    private static DBDiagram ConvertDTO2DB(DiagramDTO dto)
    {
      return new DBDiagram
      {
        id = Utils.ConvertObjectIdToGuid(dto.id) ?? Guid.Empty,
        dgr_type = Utils.ConvertObjectIdToGuid(dto.dgr_type),
        region_id = dto.region_id,
        file_id = Utils.ConvertObjectIdToGuid(dto.file_id),
        // A region says where the node stands in its parent; the geometry is the node's own
        // canvas and is stored beside it, not instead of it.
        geometry = dto.geometry is null
          ? null
          : new DBDiagramCoord
          {
            height = dto.geometry.height,
            width = dto.geometry.width,
            left = dto.geometry.left,
            top = dto.geometry.top,
            rotation = dto.geometry.rotation
          }
      };
    }

    private static Dictionary<string, DiagramDTO> ConvertListDB2DTO(List<DBDiagram> dbos)
    {
      var newObjs = new Dictionary<string, DiagramDTO>();

      foreach (var dbObj in dbos)
      {
        var dto = ToDto(dbObj);
        newObjs.Add(dto.id!, dto);
      }

      return newObjs;
    }

    /// <summary>
    /// One stored row as the wire carries it. Internal because a diagram's replica reports its
    /// owner's row with its own place (see <c>DiagramRepresentations</c>), and the two paths must
    /// convert a row the same way.
    /// </summary>
    internal static DiagramDTO ToDto(DBDiagram dbObj)
    {
      var id = Utils.ConvertGuidToObjectId(dbObj.id);

      return new DiagramDTO
      {
        id = id,
        dgr_type = dbObj.dgr_type.HasValue
          ? Utils.ConvertGuidToObjectId(dbObj.dgr_type.Value)
          : null,
        file_id = dbObj.file_id.HasValue
          ? Utils.ConvertGuidToObjectId(dbObj.file_id.Value)
          : null,
        region_id = dbObj.region_id,
        geometry = ToDto(dbObj.geometry)
      };
    }

    internal static DiagramCoordDTO? ToDto(DBDiagramCoord? coord) => coord is null
      ? null
      : new DiagramCoordDTO
      {
        top = coord.top,
        left = coord.left,
        width = coord.width,
        height = coord.height,
        rotation = coord.rotation
      };
  }
}
