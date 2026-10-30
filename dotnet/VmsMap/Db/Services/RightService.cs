using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VmsMap.Domain;

namespace VmsMap.Db
{
  internal class RightService : IRightServiceInternal
  {
    private readonly VmsMapDbContext _db;

    public RightService(VmsMapDbContext db)
    {
      _db = db;
    }

    public async Task<Dictionary<string, List<RightDTO>>> GetListByIdsAsync(List<string> ids)
    {
      var guids = ids
        .Select(Utils.ConvertObjectIdToGuid)
        .Where(g => g.HasValue)
        .Select(g => g!.Value)
        .ToList();

      var rights = await _db.Rights
        .Where(r => guids.Contains(r.object_id))
        .ToListAsync();

      return rights
        .GroupBy(r => Utils.ConvertGuidToObjectId(r.object_id))
        .ToDictionary(
          g => g.Key,
          g => g.Select(r => new RightDTO
          {
            object_id = g.Key,
            subject = r.subject,
            action = r.action
          }).ToList());
    }

    public async Task<List<RightDTO>> GetAllAsync()
    {
      var rights = await _db.Rights.AsNoTracking().ToListAsync();

      return rights.Select(r => new RightDTO
      {
        object_id = Utils.ConvertGuidToObjectId(r.object_id),
        subject = r.subject,
        action = r.action
      }).ToList();
    }

    // Deletes exactly the named rows: an object keeps the rights the caller did not name.
    public async Task<long> Delete(List<RightDTO> keys)
    {
      long rows = 0;

      foreach (var key in keys)
      {
        var objectId = Utils.ConvertObjectIdToGuid(key.object_id);
        if (objectId == null || string.IsNullOrEmpty(key.subject) || string.IsNullOrEmpty(key.action))
        {
          continue;
        }

        rows += await _db.Rights
          .Where(r => r.object_id == objectId
                   && r.subject == key.subject
                   && r.action == key.action)
          .ExecuteDeleteAsync();
      }

      return rows;
    }

    // Adds the rows that do not exist yet; an existing (object, subject, action) is a no-op.
    // Removing a right is Delete's job, so a repeated update cannot silently drop the others.
    public async Task<List<RightDTO>> Update(List<RightDTO> newObjs)
    {
      foreach (var dto in newObjs)
      {
        var objectId = Utils.ConvertObjectIdToGuid(dto.object_id);
        if (objectId == null || string.IsNullOrEmpty(dto.subject) || string.IsNullOrEmpty(dto.action))
        {
          continue;
        }

        var exists = await _db.Rights.AnyAsync(r =>
          r.object_id == objectId && r.subject == dto.subject && r.action == dto.action);

        if (!exists)
        {
          _db.Rights.Add(new DBRight
          {
            object_id = objectId.Value,
            subject = dto.subject,
            action = dto.action
          });
        }
      }

      await _db.SaveChangesAsync();
      return newObjs;
    }
  }
}
