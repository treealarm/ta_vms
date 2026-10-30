using System;
using System.Collections.Generic;
using System.Linq;

namespace VmsMap.Domain
{
  // Square's answer to a box holding more figures than a browser can draw: keep the most detailed
  // zoom layer as it is and replace the rest with a few circles, each standing for the figures
  // around it. The coordinates are the canvas's, as the figures' own are.
  public static class KMeans
  {
    public static FiguresDTO GetCentroids(List<FigureGeoDTO> figures, int number)
    {
      var result = new FiguresDTO();
      if (figures.Count == 0 || number <= 0)
        return result;

      var centroids = GetInitialCentroids(figures, number);
      if (centroids.Count == 0)
        return result;

      // Three passes were enough in Square; a fourth changed no assignment in its data.
      for (var iteration = 3; iteration >= 0; iteration--)
      {
        foreach (var figure in figures)
        {
          var point = figure.geometry?.GetCentroid();
          if (point is null)
            continue;

          var nearest = centroids
            .OrderBy(c => c.Distance(figure))
            .FirstOrDefault();
          nearest?.AddFigure(figure);
        }

        foreach (var centroid in centroids)
        {
          centroid.UpdateCenterAndRadius();
          if (iteration > 0)
            centroid.ClearFigures();
        }
      }

      foreach (var centroid in centroids)
      {
        if (centroid.Count == 0 || centroid.Center is null)
          continue;

        result.figs.Add(new FigureGeoDTO
        {
          name = $"Centroid #{centroid.Count}",
          id = centroids.IndexOf(centroid).ToString().PadLeft(24, '0'),
          geometry = new GeometryCircleDTO(centroid.Center),
          radius = centroid.Radius * 2,
          extra_props =
          [
            new ObjExtraPropertyDTO
            {
              prop_name = "obj_num",
              str_val = centroid.Count.ToString(),
            },
          ],
        });
      }

      return result;
    }

    private static List<Centroid> GetInitialCentroids(List<FigureGeoDTO> figures, int number)
    {
      var step = Math.Max(1, figures.Count / number);
      var result = new List<Centroid>();

      for (var index = 0; index < figures.Count; index += step)
      {
        var point = figures[index].geometry?.GetCentroid();
        if (point is not null)
          result.Add(new Centroid(point));
      }

      return result;
    }

    private sealed class Centroid
    {
      private readonly List<FigureGeoDTO> _figures = [];

      public Centroid(Geo2DCoordDTO center)
      {
        Center = new Geo2DCoordDTO { Lat = center.Lat, Lon = center.Lon };
      }

      public Geo2DCoordDTO? Center { get; }
      public double Radius { get; private set; }
      public int Count => _figures.Count;

      public void AddFigure(FigureGeoDTO figure) => _figures.Add(figure);

      public void ClearFigures() => _figures.Clear();

      public double Distance(FigureGeoDTO figure)
      {
        var point = figure.geometry?.GetCentroid();
        if (point is null || Center is null)
          return double.MaxValue;

        var dx = point.X - Center.X;
        var dy = point.Y - Center.Y;
        return Math.Sqrt(dx * dx + dy * dy);
      }

      public void UpdateCenterAndRadius()
      {
        if (_figures.Count == 0)
        {
          Radius = 0;
          return;
        }

        double lat = 0, lon = 0, radius = 0;
        foreach (var figure in _figures)
        {
          var point = figure.geometry?.GetCentroid();
          if (point is null)
            continue;

          lat += point.Lat;
          lon += point.Lon;
          radius += figure.GetRadius() ?? 0;
        }

        Center!.Lat = lat / _figures.Count;
        Center.Lon = lon / _figures.Count;
        Radius = radius / _figures.Count;
      }
    }
  }
}
