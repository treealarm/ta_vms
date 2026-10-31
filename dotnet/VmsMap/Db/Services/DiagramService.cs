using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VmsMap.Domain;

namespace VmsMap.Db
{
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

      var existingDict = existing.ToDictionary(e => e.id);

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

    private static DBDiagram ConvertDTO2DB(DiagramDTO dto)
    {
      return new DBDiagram
      {
        id = Utils.ConvertObjectIdToGuid(dto.id) ?? Guid.Empty,
        dgr_type = Utils.ConvertObjectIdToGuid(dto.dgr_type),
        region_id = dto.region_id,
        file_id = Utils.ConvertObjectIdToGuid(dto.file_id),
        // A node placed in a region has no geometry of its own; storing zeros would make it look
        // like it stands in the corner of its parent instead.
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
        var id = Utils.ConvertGuidToObjectId(dbObj.id);

        newObjs.Add(id, new DiagramDTO
        {
          id = id,
          dgr_type = dbObj.dgr_type.HasValue
            ? Utils.ConvertGuidToObjectId(dbObj.dgr_type.Value)
            : null,
          file_id = dbObj.file_id.HasValue
            ? Utils.ConvertGuidToObjectId(dbObj.file_id.Value)
            : null,
          region_id = dbObj.region_id,
          geometry = dbObj.geometry is null
            ? null
            : new DiagramCoordDTO
            {
              top = dbObj.geometry.top,
              left = dbObj.geometry.left,
              width = dbObj.geometry.width,
              height = dbObj.geometry.height,
              rotation = dbObj.geometry.rotation
            }
        });
      }

      return newObjs;
    }
  }
}
