using VmsMap.Domain;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VmsMap.Db
{
  internal class GeoService : IGeoService
  {
    private readonly ILevelService _levelService;
    private readonly VmsMapDbContext _dbContext;
    public GeoService(
      VmsMapDbContext dbContext,
      ILevelService levelService
    )
    {
      _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
      _levelService = levelService ?? throw new ArgumentNullException(nameof(levelService));
    }

    public async Task<Dictionary<string, GeoObjectDTO>> GetGeoAsync(BoxDTO box)
    {
      int limit = box.count.HasValue && box.count.Value > 0 ? box.count.Value : 10000;
      IQueryable<DBGeoObject> query = _dbContext.GeoObjects;
      var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

      // geo filter
      if (box.zone != null && box.zone.Count > 0)
      {
        var polygons = box.zone
            .Select(z => ModelGate.ConvertGeoDTO2DB(z))
            .Where(g => g != null)
            .ToList();

        query = box.not_in_zone
            ? query.Where(t => polygons.All(p => !t.figure.Intersects(p)))
            : query.Where(t => polygons.Any(p => t.figure.Intersects(p)));
      }
      else
      {
        var rect = geometryFactory.CreatePolygon(new[]
        {
            new Coordinate(box.wn[0], box.wn[1]),
            new Coordinate(box.es[0], box.wn[1]),
            new Coordinate(box.es[0], box.es[1]),
            new Coordinate(box.wn[0], box.es[1]),
            new Coordinate(box.wn[0], box.wn[1])
        });

        query = box.not_in_zone
            ? query.Where(t => !t.figure.Intersects(rect))
            : query.Where(t => t.figure.Intersects(rect));
      }

      // zoom_level filter
      if (box.zoom != null)
      {
        var levels = await _levelService.GetLevelsByZoom(box.zoom);
        levels.Add(null);
        levels.Add(string.Empty);
        query = query.Where(p => levels.Contains(p.zoom_level));
      }

      // id filter (strings to Guid)
      if (box.ids != null && box.ids.Count > 0)
      {
        var guidIds = box.ids
            .Select(Utils.ConvertObjectIdToGuid)
            .Where(g => g.HasValue)
            .Select(g => g.Value)
            .ToList();

        query = query.Where(p => guidIds.Contains(p.id));
      }

      // run the query
      var list = await query.Take(limit).ToListAsync();
      return ModelGate.ConvertListDB2DTO(list);
    }


    public async Task<Dictionary<string, GeoObjectDTO>> GetGeoObjectsAsync(List<string> ids)
    {
      if (ids == null || ids.Count == 0)
        return new Dictionary<string, GeoObjectDTO>();

      // the strings to Guids
      var guidIds = ids
          .Select(Utils.ConvertObjectIdToGuid)
          .Where(g => g.HasValue)
          .Select(g => g.Value)
          .ToList();

      if (guidIds.Count == 0)
        return new Dictionary<string, GeoObjectDTO>();

      List<DBGeoObject> obj = null;

      try
      {
        obj = await _dbContext.GeoObjects
            .Where(x => guidIds.Contains(x.id))
            .ToListAsync();
      }
      catch (Exception)
      {
        // log and skip
      }

      return ModelGate.ConvertListDB2DTO(obj);
    }


    public async Task<GeoObjectDTO> GetGeoObjectAsync(string id)
    {
      if (string.IsNullOrEmpty(id))
        return null;

      Guid? guidId = Utils.ConvertObjectIdToGuid(id);
      if (!guidId.HasValue)
        return null;

      DBGeoObject obj = null;

      try
      {
        obj = await _dbContext.GeoObjects
            .FirstOrDefaultAsync(x => x.id == guidId.Value);
      }
      catch (Exception)
      {
        // log the error
      }

      return ModelGate.ConvertDB2DTO(obj);
    }


    public async Task<long> RemoveAsync(List<string> ids)
    {
      if (ids == null || ids.Count == 0)
        return 0;

      var guidIds = ids
          .Select(Utils.ConvertObjectIdToGuid)
          .Where(g => g.HasValue)
          .Select(g => g.Value)
          .ToList();

      if (guidIds.Count == 0)
        return 0;

      var entities = await _dbContext.GeoObjects
          .Where(x => guidIds.Contains(x.id))
          .ToListAsync();

      if (entities.Count == 0)
        return 0;

      _dbContext.GeoObjects.RemoveRange(entities);
      await _dbContext.SaveChangesAsync();

      return entities.Count;
    }


    public async Task<Dictionary<string, GeoObjectDTO>> CreateGeo(IEnumerable<FigureGeoDTO> figures)
    {
      var retVal = new Dictionary<string, GeoObjectDTO>();
      if (figures == null || !figures.Any())
        return retVal;

      var dbUpdated = new Dictionary<FigureGeoDTO, DBGeoObject>();

      // DTO to DB
      var dbObjects = figures.Select(f =>
      {
        var dbObj = new DBGeoObject
        {
          zoom_level = f.zoom_level,
          radius = f.radius,
          figure = ModelGate.ConvertGeoDTO2DB(f.geometry),
          id = Utils.ConvertObjectIdToGuid(f.id) ?? Guid.Empty
        };
        dbUpdated[f] = dbObj;
        return dbObj;
      }).ToList();

      // the ids that are not empty
      var ids = dbObjects.Where(o => o.id != Guid.Empty).Select(o => o.id).ToList();

      // the rows that already exist
      var existing = await _dbContext.GeoObjects
          .Where(g => ids.Contains(g.id))
          .ToListAsync();
      var existingDict = existing.ToDictionary(e => e.id);

      foreach (var obj in dbObjects.Where(o => o.id == Guid.Empty))
        obj.id = Utils.NewGuid();

      // The canvas each figure is drawn on: the nearest ancestor that is a space or a diagram.
      var canvasIds = await CanvasResolver.ResolveForAsync(
        _dbContext, dbObjects.Select(o => o.id).ToList());

      foreach (var obj in dbObjects)
      {
        obj.canvas_id = canvasIds[obj.id];

        if (!existingDict.TryGetValue(obj.id, out var existingObj))
        {
          _dbContext.GeoObjects.Add(obj);
        }
        else
        {
          // existing row: update its fields
          existingObj.figure = obj.figure;
          existingObj.radius = obj.radius;
          existingObj.zoom_level = obj.zoom_level;
          existingObj.canvas_id = obj.canvas_id;
        }
      }

      // save
      await _dbContext.SaveChangesAsync();

      // write the ids back into the DTOs and build the result
      foreach (var pair in dbUpdated)
      {
        pair.Key.id = pair.Value.id.ToString();
        retVal[pair.Value.id.ToString()] = ModelGate.ConvertDB2DTO(pair.Value);
      }

      return retVal;
    }
  }
}
