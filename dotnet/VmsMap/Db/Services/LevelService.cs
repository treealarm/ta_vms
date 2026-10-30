using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VmsMap.Domain;

namespace VmsMap.Db
{
  // The zoom-level dictionary. Rows are seeded by the schema migration; the table is 190 rows, so
  // every call reads it — a cache shared between requests was the thing that went stale in Square.
  internal class LevelService : ILevelService
  {
    private readonly VmsMapDbContext _db;

    public LevelService(VmsMapDbContext db)
    {
      _db = db;
    }

    public async Task<LevelDTO?> GetByZoomLevel(string name)
    {
      var result = await _db.Levels.AsNoTracking()
        .FirstOrDefaultAsync(l => l.zoom_level == name);

      if (result == null)
        return null;

      return ToDto(result);
    }

    public async Task<Dictionary<string, LevelDTO>> GetAllZooms()
    {
      var levels = await _db.Levels.AsNoTracking().ToListAsync();

      return levels.ToDictionary(c => c.zoom_level, ToDto);
    }

    public async Task<List<string>> GetLevelsByZoom(double? zoom)
    {
      if (zoom == null)
        return new List<string>();

      return await _db.Levels.AsNoTracking()
        .Where(l => zoom <= l.zoom_max && zoom >= l.zoom_min)
        .Select(l => l.zoom_level)
        .ToListAsync();
    }

    private static LevelDTO ToDto(DBLevel level) => new()
    {
      id = Utils.ConvertGuidToObjectId(level.id),
      zoom_level = level.zoom_level,
      zoom_min = level.zoom_min,
      zoom_max = level.zoom_max
    };
  }
}
