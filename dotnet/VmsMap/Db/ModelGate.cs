using NetTopologySuite;
using NetTopologySuite.Geometries;
using System;
using System.Collections.Generic;
using VmsMap.Domain;

namespace VmsMap.Db
{
  // Conversions between the stored geometries and the domain's GeoJSON-like DTOs. The DTO keeps
  // Leaflet's [lat, lon] order; NTS keeps X = lon, Y = lat.
  internal static class ModelGate
  {
    public static DBGeoObject ConvertDTO2DB(GeoObjectDTO dto)
    {
      return new DBGeoObject
      {
        id = Utils.ConvertObjectIdToGuid(dto.id) ?? Utils.NewGuid(),
        radius = dto.radius,
        zoom_level = dto.zoom_level,
        figure = ConvertGeoDTO2DB(dto.location)!
      };
    }

    public static List<DBGeoObject> ConvertListDTO2DB(List<GeoObjectDTO> dto)
    {
      var res = new List<DBGeoObject>();

      foreach (var item in dto)
      {
        res.Add(ConvertDTO2DB(item));
      }

      return res;
    }

    public static Dictionary<string, GeoObjectDTO> ConvertListDB2DTO(List<DBGeoObject>? dto)
    {
      var res = new Dictionary<string, GeoObjectDTO>();

      if (dto == null)
      {
        return res;
      }

      foreach (var item in dto)
      {
        res.Add(Utils.ConvertGuidToObjectId(item.id), ConvertDB2DTO(item));
      }

      return res;
    }

    public static Geometry? ConvertGeoDTO2DB(GeometryDTO? location)
    {
      if (location == null)
        return null;

      var geometryFactory = NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
      Geometry? ret = null;

      switch (location)
      {
        case GeometryCircleDTO point:
          ret = geometryFactory.CreatePoint(new Coordinate(point.coord!.Lon, point.coord.Lat));
          break;

        case GeometryPolygonDTO polygon:
          var coords = new Coordinate[polygon.coord.Count + 1];
          for (int i = 0; i < polygon.coord.Count; i++)
          {
            coords[i] = new Coordinate(polygon.coord[i].Lon, polygon.coord[i].Lat);
          }
          // Close the ring: NTS expects the first coordinate repeated at the end.
          coords[polygon.coord.Count] = new Coordinate(polygon.coord[0].Lon, polygon.coord[0].Lat);

          var linearRing = geometryFactory.CreateLinearRing(coords);
          ret = geometryFactory.CreatePolygon(linearRing);
          break;

        case GeometryPolylineDTO line:
          var lineCoords = new Coordinate[line.coord.Count];
          for (int i = 0; i < line.coord.Count; i++)
          {
            lineCoords[i] = new Coordinate(line.coord[i].Lon, line.coord[i].Lat);
          }
          ret = geometryFactory.CreateLineString(lineCoords);
          break;

        default:
          throw new NotSupportedException(
            $"GeometryDTO type {location.GetType().Name} not supported");
      }

      return ret;
    }

    public static GeoObjectDTO ConvertDB2DTO(DBGeoObject dbObj)
    {
      return new GeoObjectDTO
      {
        id = Utils.ConvertGuidToObjectId(dbObj.id),
        Version = dbObj.Version,
        radius = dbObj.radius,
        zoom_level = dbObj.zoom_level,
        location = ConvertGeoDB2DTO(dbObj.figure),
      };
    }

    private static GeometryDTO ConvertGeoDB2DTO(Geometry location)
    {
      GeometryDTO ret;

      switch (location)
      {
        case Point point:
          ret = new GeometryCircleDTO(new Geo2DCoordDTO { Y = point.Y, X = point.X });
          break;

        case Polygon polygon:
          var retPolygon = new GeometryPolygonDTO();
          var coords = polygon.ExteriorRing.Coordinates;
          foreach (var cur in coords)
          {
            retPolygon.coord.Add(new Geo2DCoordDTO { Y = cur.Y, X = cur.X });
          }
          // Drop the closing coordinate: the DTO's polygon is open, as GeoJSON's is.
          if (retPolygon.coord.Count > 3 &&
              retPolygon.coord[0].X == retPolygon.coord[^1].X &&
              retPolygon.coord[0].Y == retPolygon.coord[^1].Y)
          {
            retPolygon.coord.RemoveAt(retPolygon.coord.Count - 1);
          }
          ret = retPolygon;
          break;

        case LineString line:
          var retLine = new GeometryPolylineDTO();
          foreach (var cur in line.Coordinates)
          {
            retLine.coord.Add(new Geo2DCoordDTO { Y = cur.Y, X = cur.X });
          }
          ret = retLine;
          break;

        default:
          throw new NotSupportedException($"Geometry type {location.GeometryType} not supported");
      }

      ret.type = location.GeometryType;
      return ret;
    }
  }
}
