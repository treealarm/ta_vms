using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VmsMap.Domain;

namespace VmsMap.Db
{
  public class VisualService
  {
    private readonly VmsMapDbContext _db;

    public VisualService(VmsMapDbContext db)
    {
      _db = db;
    }

    public async Task UpdateListAsync(List<VisualDTO> visuals)
    {
      if (visuals.Count == 0)
      {
        return;
      }

      var ids = visuals
        .Select(v => Utils.ConvertObjectIdToGuid(v.object_id))
        .Where(g => g.HasValue)
        .Select(g => g!.Value)
        .ToList();

      var existing = await _db.Visuals
        .Where(v => ids.Contains(v.object_id))
        .ToDictionaryAsync(v => v.object_id);

      foreach (var dto in visuals)
      {
        var objectId = Utils.ConvertObjectIdToGuid(dto.object_id);
        if (objectId == null)
        {
          continue;
        }

        if (existing.TryGetValue(objectId.Value, out var row))
        {
          Apply(dto, row);
        }
        else
        {
          var visual = new DBVisual { object_id = objectId.Value };
          Apply(dto, visual);
          _db.Visuals.Add(visual);
        }
      }

      await _db.SaveChangesAsync();
    }

    public async Task<Dictionary<string, VisualDTO>> GetListByIdsAsync(List<string> ids)
    {
      var guids = ids
        .Select(Utils.ConvertObjectIdToGuid)
        .Where(g => g.HasValue)
        .Select(g => g!.Value)
        .ToList();

      var rows = await _db.Visuals
        .AsNoTracking()
        .Where(v => guids.Contains(v.object_id))
        .ToListAsync();

      return rows.ToDictionary(
        v => Utils.ConvertGuidToObjectId(v.object_id),
        v => new VisualDTO
        {
          object_id = Utils.ConvertGuidToObjectId(v.object_id),
          color = v.color,
          image = v.image,
          image_width = v.image_width,
          image_height = v.image_height,
          rotation_deg = v.rotation_deg,
          text = v.text
        });
    }

    private static void Apply(VisualDTO dto, DBVisual row)
    {
      row.color = dto.color;
      row.image = dto.image;
      row.image_width = dto.image_width;
      row.image_height = dto.image_height;
      row.rotation_deg = dto.rotation_deg;
      row.text = dto.text;
    }
  }
}
