using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VmsMap.Domain;

namespace VmsMap.Db
{
  // The write side of figures: a producer sends nodes with geometry, and this keeps the tree,
  // the properties and the drawn figure in step — optionally recording a track point for each.
  internal class TracksUpdateService : ITracksUpdateService
  {
    private readonly IMapService _mapService;
    private readonly ITrackService _tracksService;
    private readonly IGeoService _geoService;

    public TracksUpdateService(
      IMapService mapsService,
      ITrackService tracksService,
      IGeoService geoService)
    {
      _mapService = mapsService;
      _tracksService = tracksService;
      _geoService = geoService;
    }

    public async Task<List<string>> AddTracks(List<TrackPointDTO> movedMarkers)
    {
      var trackPointsInserted = await _tracksService.InsertManyAsync(movedMarkers);
      return trackPointsInserted.Select(t => t.id!).ToList();
    }

    public async Task<FiguresDTO> UpdateFigures(FiguresDTO statMarkers)
    {
      await _mapService.UpdateHierarchyAsync(statMarkers.figs);
      await _mapService.UpdatePropNotDeleteAsync(statMarkers.figs);
      var updatedFigs = await _geoService.CreateGeo(statMarkers.figs);

      if (statMarkers.add_tracks)
      {
        var trackPoints = new List<TrackPointDTO>();

        foreach (var figure in statMarkers.figs)
        {
          updatedFigs.TryGetValue(figure.id!, out var circle);

          trackPoints.Add(new TrackPointDTO
          {
            figure = circle,
            timestamp = DateTime.UtcNow,
            extra_props = figure.extra_props
          });
        }

        await AddTracks(trackPoints);
      }

      return statMarkers;
    }
  }
}
