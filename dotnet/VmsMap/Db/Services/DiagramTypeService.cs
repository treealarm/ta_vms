using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VmsMap.Domain;

namespace VmsMap.Db
{
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

      var guidIds = ids
          .Select(Utils.ConvertObjectIdToGuid)
          .Where(g => g.HasValue)
          .Select(g => g!.Value)
          .ToList();

      if (guidIds.Count == 0)
        return;

      var toDelete = await _dbContext.DiagramTypes
          .Where(d => guidIds.Contains(d.id))
          .ToListAsync();

      if (toDelete.Count > 0)
      {
        _dbContext.DiagramTypes.RemoveRange(toDelete);
        await _dbContext.SaveChangesAsync();
      }
    }

    public async Task<Dictionary<string, DiagramTypeDTO>> GetListByTypeNamesAsync(List<string> typeNames)
    {
      if (typeNames == null || typeNames.Count == 0)
        return new Dictionary<string, DiagramTypeDTO>();

      var obj = await _dbContext.DiagramTypes
          .Where(d => typeNames.Contains(d.name))
          .Include(d => d.regions)
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

      query = query.Take(filterDto.count).Include(x => x.regions);

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

      var dtoIdPairs = newObjs
          .Select(dto => (Id: Utils.ConvertObjectIdToGuid(dto.id) ?? Utils.NewGuid(), Dto: dto))
          .ToList();

      var ids = dtoIdPairs.Select(x => x.Id).ToList();

      var existing = await _dbContext.DiagramTypes
          .Include(d => d.regions)
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

          var incomingKeys = incomingRegions.Select(r => r.region_key).ToHashSet();
          var toRemove = dbObj.regions
              .Where(r => !incomingKeys.Contains(r.region_key))
              .ToList();

          foreach (var r in toRemove)
          {
            dbObj.regions.Remove(r);
            _dbContext.DiagramTypeRegions.Remove(r);
          }

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

          toAdd.Add(newDbObj);
        }

        // The response carries the id assigned to a new row.
        dto.id = Utils.ConvertGuidToObjectId(id);
      }

      if (toAdd.Count > 0)
        await _dbContext.DiagramTypes.AddRangeAsync(toAdd);

      await _dbContext.SaveChangesAsync();
    }

    // The rotation is part of the region: Square's update dropped it and a rotated region came
    // back upright after every save.
    private static DBDiagramCoord ToDbCoord(DiagramCoordDTO? coord)
    {
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
          regions = new List<DiagramTypeRegionDTO>()
        };

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
              : new Dictionary<string, string>(item.styles)
          });
        }

        newObjs.Add(id, dto);
      }

      return newObjs;
    }
  }
}
