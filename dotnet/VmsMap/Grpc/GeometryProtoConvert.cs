using System.Collections.Generic;
using System.Linq;
using VmsMap.Domain;
using MapProto = Vms.Map;

namespace VmsMap.Grpc
{
  // Between the proto geometry (Leaflet's [lat, lon] list plus a shape name) and the domain DTOs.
  // Square mapped a LineString to a polygon here, losing the shape; the type decides now.
  internal static class GeometryProtoConvert
  {
    public static GeometryDTO? FromProto(MapProto.Geometry? geometry)
    {
      if (geometry == null || geometry.Coord.Count == 0)
      {
        return null;
      }

      var coords = geometry.Coord
        .Select(c => new Geo2DCoordDTO { Lat = c.Lat, Lon = c.Lon })
        .ToList();

      switch (geometry.Type)
      {
        case "Point":
          return new GeometryCircleDTO(coords[0]);

        case "Polygon":
          var polygon = new GeometryPolygonDTO();
          polygon.coord.AddRange(coords);
          return polygon;

        case "LineString":
          var line = new GeometryPolylineDTO();
          line.coord.AddRange(coords);
          return line;

        default:
          return null;
      }
    }

    public static MapProto.Geometry ToProto(GeometryDTO? geometry)
    {
      var result = new MapProto.Geometry();

      if (geometry == null)
      {
        return result;
      }

      var coords = geometry.GetCoordArray() ?? new List<Geo2DCoordDTO>();
      foreach (var c in coords)
      {
        result.Coord.Add(new MapProto.Coordinate { Lat = c.Lat, Lon = c.Lon });
      }

      result.Type = geometry.GetFigureType();

      // GeoJSON's polygon ring is closed: the first coordinate repeated at the end.
      if (geometry is GeometryPolygonDTO && result.Coord.Count > 0)
      {
        result.Coord.Add(result.Coord[0]);
      }

      return result;
    }
  }
}
