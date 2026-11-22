using Microsoft.EntityFrameworkCore;
using System;
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

      // Save refuses a duplicate name, but rows written before that check may hold one. The whole
      // dictionary is read by name, so a duplicate must not take every scale layer down with it.
      return levels
        .GroupBy(level => level.zoom_level)
        .ToDictionary(group => group.Key, group => ToDto(group.First()));
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

    public async Task<List<LevelDTO>> UpdateAsync(List<LevelDTO> levels)
    {
      // The dictionary is keyed by name, so two rows with one name cannot both be read back: refuse
      // the save instead of writing a pair that breaks the list.
      var duplicate = levels
        .Where(level => !string.IsNullOrWhiteSpace(level.zoom_level))
        .GroupBy(level => level.zoom_level)
        .FirstOrDefault(group => group.Count() > 1);
      if (duplicate is not null)
        throw new InvalidOperationException($"The scale layer '{duplicate.Key}' is defined twice");

      var ids = levels
        .Select(level => Utils.ConvertObjectIdToGuid(level.id))
        .Where(id => id.HasValue)
        .Select(id => id!.Value)
        .ToList();

      var known = await _db.Levels
        .Where(level => ids.Contains(level.id))
        .ToDictionaryAsync(level => level.id);

      foreach (var dto in levels)
      {
        var id = Utils.ConvertObjectIdToGuid(dto.id);

        // A row the caller does not know (or a duplicate id in one batch) becomes a new level.
        if (id is null || !known.TryGetValue(id.Value, out var row))
        {
          row = new DBLevel { id = id ?? Utils.NewGuid() };
          _db.Levels.Add(row);
          known[row.id] = row;
        }

        row.zoom_level = dto.zoom_level ?? string.Empty;
        row.zoom_min = dto.zoom_min;
        row.zoom_max = dto.zoom_max;

        dto.id = Utils.ConvertGuidToObjectId(row.id);
      }

      await _db.SaveChangesAsync();
      return levels;
    }

    public async Task RemoveAsync(List<string> ids)
    {
      var guids = ids
        .Select(Utils.ConvertObjectIdToGuid)
        .Where(id => id.HasValue)
        .Select(id => id!.Value)
        .ToList();

      if (guids.Count == 0)
        return;

      // Loaded and removed rather than ExecuteDelete: the in-memory provider the tests use has
      // no bulk delete, and the dictionary is small.
      var rows = await _db.Levels.Where(level => guids.Contains(level.id)).ToListAsync();
      if (rows.Count == 0)
        return;

      _db.Levels.RemoveRange(rows);
      await _db.SaveChangesAsync();
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
