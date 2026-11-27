using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VmsMap.Domain;

namespace VmsMap.Db
{
  /// A type the dictionary cannot hold: its regions are the names children stand at, so each one
  /// needs a key of its own.
  public sealed class DiagramTypeConfigException(string message) : Exception(message);

  internal class DiagramTypeService : IDiagramTypeService
  {
    private readonly VmsMapDbContext _dbContext;

    public DiagramTypeService(VmsMapDbContext context)
    {
      _dbContext = context;
    }

    public async Task DeleteAsync(List<string> ids)
    {
      if (ids == null || ids.Count == 0)
        return;

      // The client's order, not the query's: no ORDER BY promises one, and the refusal names the
      // first type in use, so which one that is must not change between two runs of the same call.
      var guidIds = ids
          .Select(Utils.ConvertObjectIdToGuid)
          .Where(g => g.HasValue)
          .Select(g => g!.Value)
          .Distinct()
          .ToList();

      if (guidIds.Count == 0)
        return;

      var byId = (await _dbContext.DiagramTypes
              .Where(d => guidIds.Contains(d.id))
              .ToListAsync())
          .ToDictionary(d => d.id);

      foreach (var id in guidIds)
      {
        if (byId.TryGetValue(id, out var type))
          await EnsureTypeIsUnusedAsync(type);
      }

      // One SaveChanges for the whole call: a request either deletes all of its types or none, so
      // an id that turns out to be in use cannot leave its siblings already gone.
      var toDelete = guidIds
          .Where(byId.ContainsKey)
          .Select(id => byId[id])
          .ToList();

      if (toDelete.Count > 0)
      {
        _dbContext.DiagramTypes.RemoveRange(toDelete);
        await _dbContext.SaveChangesAsync();
      }
    }

    /// <summary>
    /// Refuses to delete a type a diagram is drawn from or a device binding keeps. The type's
    /// regions are the keys children stand at and the template device parts are drawn from, so
    /// taking it away leaves those rows naming a dictionary entry that no longer exists: the
    /// editor draws nothing for them and nobody can tell a missing node from a broken picture.
    /// This is the same rule as <see cref="EnsureRegionsAreFreeAsync"/> and
    /// <c>EnsureChildrenKeepTheirRegionsAsync</c>, seen from the third side.
    /// </summary>
    private async Task EnsureTypeIsUnusedAsync(DBDiagramType type)
    {
      // The join is what the schema already says: a diagram row lives only as long as its node
      // (`diagrams.id` references `objects` with ON DELETE CASCADE). Counting rows whose node is
      // gone would refuse a deletion over a diagram nobody can see — and the in-memory provider
      // the tests use has no cascades, so such rows exist there and nowhere else.
      var drawn = await (from d in _dbContext.Diagrams.AsNoTracking()
                         join m in _dbContext.Markers.AsNoTracking() on d.id equals m.id
                         where d.dgr_type == type.id
                         select (Guid?)d.id)
          .FirstOrDefaultAsync();

      if (drawn is { } diagramId)
        throw new DiagramTypeConfigException(
          $"The diagram type \"{type.name}\" cannot be deleted: the diagram " +
          $"\"{Utils.ConvertGuidToObjectId(diagramId)}\" is drawn from it.");

      var bound = await (from d in _dbContext.DeviceDiagrams.AsNoTracking()
                         join m in _dbContext.Markers.AsNoTracking() on d.object_id equals m.id
                         where d.diagram_type_id == type.id
                         select new { d.source_kind, d.source_id })
          .FirstOrDefaultAsync();

      if (bound is not null)
        throw new DiagramTypeConfigException(
          $"The diagram type \"{type.name}\" cannot be deleted: the device " +
          $"\"{bound.source_kind}:{bound.source_id}\" keeps a diagram of it.");
    }

    public async Task<Dictionary<string, DiagramTypeDTO>> GetListByTypeNamesAsync(List<string> typeNames)
    {
      if (typeNames == null || typeNames.Count == 0)
        return new Dictionary<string, DiagramTypeDTO>();

      var obj = await _dbContext.DiagramTypes
          .Where(d => typeNames.Contains(d.name))
          .Include(d => d.regions)
          .Include(d => d.parts)
          .ToListAsync();

      return ConvertListDB2DTO(obj);
    }

    public async Task<Dictionary<string, DiagramTypeDTO>> GetListByTypeIdsAsync(List<string> ids)
    {
      if (ids == null || ids.Count == 0)
        return new Dictionary<string, DiagramTypeDTO>();

      var guidIds = ids
          .Select(Utils.ConvertObjectIdToGuid)
          .Where(g => g.HasValue)
          .Select(g => g!.Value)
          .ToList();

      if (guidIds.Count == 0)
        return new Dictionary<string, DiagramTypeDTO>();

      var obj = await _dbContext.DiagramTypes
          .Where(d => guidIds.Contains(d.id))
          .Include(d => d.regions)
          .Include(d => d.parts)
          .ToListAsync();

      return ConvertListDB2DTO(obj);
    }

    public async Task<Dictionary<string, DiagramTypeDTO>> GetDiagramTypesByFilter(
        GetDiagramTypesByFilterDTO filterDto)
    {
      if (filterDto == null)
        return new Dictionary<string, DiagramTypeDTO>();

      var query = _dbContext.DiagramTypes.AsQueryable();

      // The id is the cursor: the filter pages by it, so the order is fixed to it as well.
      if (!string.IsNullOrEmpty(filterDto.start_id))
      {
        var startGuid = Utils.ConvertObjectIdToGuid(filterDto.start_id);
        if (startGuid.HasValue)
        {
          query = filterDto.forward
            ? query.Where(x => x.id.CompareTo(startGuid.Value) > 0)
            : query.Where(x => x.id.CompareTo(startGuid.Value) < 0);
        }
      }

      if (!string.IsNullOrEmpty(filterDto.filter))
      {
        query = query.Where(x => EF.Functions.ILike(x.name, $"%{filterDto.filter}%"));
      }

      query = filterDto.forward
        ? query.OrderBy(x => x.id)
        : query.OrderByDescending(x => x.id);

      query = query.Take(filterDto.count).Include(x => x.regions).Include(x => x.parts);

      var obj = await query.ToListAsync();

      if (!filterDto.forward)
      {
        obj.Reverse();
      }

      return ConvertListDB2DTO(obj);
    }

    public async Task UpdateListAsync(List<DiagramTypeDTO> newObjs)
    {
      if (newObjs == null || newObjs.Count == 0)
        return;

      NormalizeKeys(newObjs);

      var dtoIdPairs = newObjs
          .Select(dto => (Id: Utils.ConvertObjectIdToGuid(dto.id) ?? Utils.NewGuid(), Dto: dto))
          .ToList();

      var ids = dtoIdPairs.Select(x => x.Id).ToList();

      var existing = await _dbContext.DiagramTypes
          .Include(d => d.regions)
          .Include(d => d.parts)
          .Where(d => ids.Contains(d.id))
          .ToListAsync();

      var existingDict = existing.ToDictionary(x => x.id, x => x);

      var toAdd = new List<DBDiagramType>();

      foreach (var (id, dto) in dtoIdPairs)
      {
        var incomingRegions = dto.regions ?? new List<DiagramTypeRegionDTO>();

        if (existingDict.TryGetValue(id, out var dbObj))
        {
          dbObj.name = dto.name ?? string.Empty;
          dbObj.file_id = Utils.ConvertObjectIdToGuid(dto.file_id);
          ApplyTemplate(dbObj, dto);

          var incomingKeys = incomingRegions.Select(r => r.region_key).ToHashSet();
          var toRemove = dbObj.regions
              .Where(r => !incomingKeys.Contains(r.region_key))
              .ToList();

          // A region is a slot: the nodes standing in it are placed by the key it has. Dropping it
          // — or renaming it, which is the same change from the template's side — would leave them
          // in the tree with nowhere to stand and no way to say why.
          await EnsureRegionsAreFreeAsync(dbObj, toRemove.Select(r => r.region_key).ToList());

          foreach (var r in toRemove)
          {
            dbObj.regions.Remove(r);
            _dbContext.DiagramTypeRegions.Remove(r);
          }

          // A region's binding dies with the region: the row names it by key, so leaving it would
          // bind a region that no longer exists the moment another one takes that key.
          var removedKeys = toRemove.Select(r => r.region_key).ToHashSet(StringComparer.Ordinal);
          foreach (var stale in dbObj.parts.Where(p => removedKeys.Contains(p.region_key)).ToList())
            _dbContext.DiagramTypeParts.Remove(stale);

          foreach (var r in incomingRegions)
          {
            var existingRegion = dbObj.regions
                .FirstOrDefault(x => x.region_key == r.region_key);

            if (existingRegion != null)
            {
              existingRegion.geometry = ToDbCoord(r.geometry);
              existingRegion.styles = CopyStyles(r.styles);
            }
            else
            {
              var newRegion = new DBDiagramTypeRegion
              {
                diagram_type_id = dbObj.id,
                region_key = r.region_key ?? string.Empty,
                geometry = ToDbCoord(r.geometry),
                styles = CopyStyles(r.styles)
              };

              _dbContext.DiagramTypeRegions.Add(newRegion);
              dbObj.regions.Add(newRegion);
            }

            ApplyPart(dbObj, r);
          }
        }
        else
        {
          var newDbObj = new DBDiagramType
          {
            id = id,
            name = dto.name ?? string.Empty,
            file_id = Utils.ConvertObjectIdToGuid(dto.file_id),
            regions = incomingRegions.Select(r => new DBDiagramTypeRegion
            {
              diagram_type_id = id,
              region_key = r.region_key ?? string.Empty,
              geometry = ToDbCoord(r.geometry),
              styles = CopyStyles(r.styles)
            }).ToList()
          };

          ApplyTemplate(newDbObj, dto);
          foreach (var r in incomingRegions)
            ApplyPart(newDbObj, r);

          toAdd.Add(newDbObj);
        }

        // The response carries the id assigned to a new row.
        dto.id = Utils.ConvertGuidToObjectId(id);
      }

      if (toAdd.Count > 0)
        await _dbContext.DiagramTypes.AddRangeAsync(toAdd);

      await _dbContext.SaveChangesAsync();
    }

    /// A region is addressed by its key — a child names it in `region_id` — so a key has to be
    /// there and to be the only one of its name. The update below matches regions by key; two of
    /// them would be written into the first row twice and the second would silently vanish.
    ///
    /// The same pass normalises and refuses what a template must never store: a part kind or a way
    /// of matching that nothing understands, an empty match value, an index that is not a number,
    /// and two regions claiming the same part by the same rule. Refusing here is what keeps a
    /// device from being drawn from a template whose meaning nobody can predict.
    /// <summary>
    /// Refuses to take away regions a node stands in. The children of every diagram drawn from this
    /// type are read: a direct child whose row names one of the disappearing regions is what the
    /// template would orphan. A key left out of an update is both "deleted" and, when another key
    /// takes its place, "renamed" — neither is allowed while something stands there.
    /// </summary>
    private async Task EnsureRegionsAreFreeAsync(DBDiagramType type, List<string> removedKeys)
    {
      if (removedKeys.Count == 0)
        return;

      var drawn = _dbContext.Diagrams.AsNoTracking()
          .Where(d => d.dgr_type == type.id)
          .Select(d => d.id);

      var taken = await (from m in _dbContext.Markers.AsNoTracking()
                         join d in _dbContext.Diagrams.AsNoTracking() on m.id equals d.id
                         where m.parent_id != null
                               && drawn.Contains(m.parent_id.Value)
                               && d.region_id != null
                               && removedKeys.Contains(d.region_id)
                         select d.region_id)
          .FirstOrDefaultAsync();

      if (taken is not null)
        throw new DiagramTypeConfigException(
          $"The diagram type \"{type.name}\" cannot lose the region \"{taken}\": a node of a " +
          "diagram drawn from it stands there.");
    }

    private static void NormalizeKeys(List<DiagramTypeDTO> types)
    {
      foreach (var type in types)
      {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var claimed = new HashSet<(string Kind, string By, string Value)>();

        type.source_kind = NullIfEmpty(type.source_kind);
        if (type.source_kind is not null && type.source_kind != DeviceObjectId.CameraSource)
          throw new DiagramTypeConfigException(
              $"The diagram type \"{type.name}\" names source kind \"{type.source_kind}\"; " +
              $"\"{DeviceObjectId.CameraSource}\" is the only one there is.");

        type.device_manufacturer = type.device_manufacturer?.Trim() ?? string.Empty;
        type.device_model = type.device_model?.Trim() ?? string.Empty;

        foreach (var region in type.regions ?? [])
        {
          var key = region.region_key?.Trim();
          if (string.IsNullOrEmpty(key))
            throw new DiagramTypeConfigException(
              $"The diagram type \"{type.name}\" has a region without a key.");

          if (!seen.Add(key))
            throw new DiagramTypeConfigException(
              $"The diagram type \"{type.name}\" has two regions keyed \"{key}\".");

          region.region_key = key;

          if (region.part is not null)
            NormalizePart(type, region, claimed);
        }
      }
    }

    private static void NormalizePart(
      DiagramTypeDTO type, DiagramTypeRegionDTO region, HashSet<(string, string, string)> claimed)
    {
      var part = region.part!;

      part.part_kind = part.part_kind?.Trim().ToLowerInvariant();
      if (part.part_kind is not (DeviceObjectId.DigitalInputPart or DeviceObjectId.RelayPart))
        throw new DiagramTypeConfigException(
          $"The region \"{region.region_key}\" of \"{type.name}\" names part kind " +
          $"\"{part.part_kind}\"; only \"{DeviceObjectId.DigitalInputPart}\" and " +
          $"\"{DeviceObjectId.RelayPart}\" exist.");

      part.match_by = part.match_by?.Trim().ToLowerInvariant();
      if (!DiagramPartMatch.IsKnown(part.match_by))
        throw new DiagramTypeConfigException(
          $"The region \"{region.region_key}\" of \"{type.name}\" matches by " +
          $"\"{part.match_by}\"; the ways are \"{DiagramPartMatch.Token}\", " +
          $"\"{DiagramPartMatch.Name}\" and \"{DiagramPartMatch.Index}\".");

      part.match_value = part.match_value?.Trim() ?? string.Empty;
      if (part.match_value.Length == 0)
        throw new DiagramTypeConfigException(
          $"The region \"{region.region_key}\" of \"{type.name}\" binds a part but says nothing " +
          "to find it by.");

      if (part.match_by == DiagramPartMatch.Index
          && (!int.TryParse(part.match_value, out var index) || index < 1))
        throw new DiagramTypeConfigException(
          $"The region \"{region.region_key}\" of \"{type.name}\" matches by index " +
          $"\"{part.match_value}\"; an index counts from 1.");

      // Two regions cannot claim the same part of the same kind by the same rule: which one the
      // part would be drawn in is decided by the order of a list nobody reads as an order.
      if (!claimed.Add((part.part_kind!, part.match_by!, part.match_value)))
        throw new DiagramTypeConfigException(
          $"Two regions of \"{type.name}\" claim the {part.part_kind} found by " +
          $"\"{part.match_by} = {part.match_value}\".");
    }

    private static void ApplyTemplate(DBDiagramType dbObj, DiagramTypeDTO dto)
    {
      dbObj.source_kind = dto.source_kind;
      dbObj.device_manufacturer = dto.device_manufacturer ?? string.Empty;
      dbObj.device_model = dto.device_model ?? string.Empty;
      dbObj.auto_attach = dto.auto_attach;
    }

    /// One binding per region: added, replaced, or removed when the region stops naming a part.
    private void ApplyPart(DBDiagramType dbObj, DiagramTypeRegionDTO region)
    {
      var key = region.region_key ?? string.Empty;
      var existing = dbObj.parts.FirstOrDefault(p => p.region_key == key);

      if (region.part is null)
      {
        if (existing is not null)
        {
          dbObj.parts.Remove(existing);
          _dbContext.DiagramTypeParts.Remove(existing);
        }
        return;
      }

      if (existing is null)
      {
        dbObj.parts.Add(new DBDiagramTypePart
        {
          diagram_type_id = dbObj.id,
          region_key = key,
          part_kind = region.part.part_kind!,
          match_by = region.part.match_by!,
          match_value = region.part.match_value!,
        });
        return;
      }

      existing.part_kind = region.part.part_kind!;
      existing.match_by = region.part.match_by!;
      existing.match_value = region.part.match_value!;
    }

    private static string? NullIfEmpty(string? value) =>
      string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // The rotation is part of the region: Square's update dropped it and a rotated region came
    // back upright after every save.
    private static DBDiagramCoord ToDbCoord(DiagramCoordDTO? coord)    {
      var c = coord ?? new DiagramCoordDTO();

      return new DBDiagramCoord
      {
        top = c.top,
        left = c.left,
        width = c.width,
        height = c.height,
        rotation = c.rotation
      };
    }

    private static Dictionary<string, string>? CopyStyles(Dictionary<string, string>? styles) =>
      styles == null ? null : new Dictionary<string, string>(styles);

    private static Dictionary<string, DiagramTypeDTO> ConvertListDB2DTO(List<DBDiagramType> dbo)
    {
      var newObjs = new Dictionary<string, DiagramTypeDTO>();

      foreach (var dbObj in dbo)
      {
        var id = Utils.ConvertGuidToObjectId(dbObj.id);

        var dto = new DiagramTypeDTO
        {
          id = id,
          name = dbObj.name,
          file_id = dbObj.file_id.HasValue
            ? Utils.ConvertGuidToObjectId(dbObj.file_id.Value)
            : null,
          source_kind = dbObj.source_kind,
          device_manufacturer = dbObj.device_manufacturer,
          device_model = dbObj.device_model,
          auto_attach = dbObj.auto_attach,
          regions = new List<DiagramTypeRegionDTO>()
        };

        var parts = dbObj.parts.ToDictionary(p => p.region_key, StringComparer.Ordinal);

        foreach (var item in dbObj.regions)
        {
          dto.regions.Add(new DiagramTypeRegionDTO
          {
            region_key = item.region_key,
            geometry = new DiagramCoordDTO
            {
              top = item.geometry.top,
              left = item.geometry.left,
              width = item.geometry.width,
              height = item.geometry.height,
              rotation = item.geometry.rotation
            },
            styles = item.styles == null
              ? null
              : new Dictionary<string, string>(item.styles),
            part = parts.TryGetValue(item.region_key, out var bound)
              ? new DiagramTypePartDTO
              {
                part_kind = bound.part_kind,
                match_by = bound.match_by,
                match_value = bound.match_value,
              }
              : null,
          });
        }

        newObjs.Add(id, dto);
      }

      return newObjs;
    }
  }
}
