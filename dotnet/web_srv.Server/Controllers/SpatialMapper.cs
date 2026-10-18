using Vms.Cfg;
using web_srv.Types.Dto;

namespace web_srv;

/// Proto ↔ DTO for maps & plans, shared by the controllers that serve them.
public static class SpatialMapper
{
  public static GeoPointDto ToDto(GeoPoint p) => new() { Lat = p.Lat, Lon = p.Lon };

  public static GeoPoint ToProto(GeoPointDto p) => new() { Lat = p.Lat, Lon = p.Lon };

  public static PlanCornersDto ToDto(PlanCorners c) => new()
  {
    TopLeft = ToDto(c.TopLeft),
    TopRight = ToDto(c.TopRight),
    BottomLeft = ToDto(c.BottomLeft),
  };

  public static PlanCorners ToProto(PlanCornersDto c) => new()
  {
    TopLeft = ToProto(c.TopLeft),
    TopRight = ToProto(c.TopRight),
    BottomLeft = ToProto(c.BottomLeft),
  };

  public static SpaceDto ToDto(Space s) => new()
  {
    Id = s.Id,
    ParentId = string.IsNullOrEmpty(s.ParentId) ? null : s.ParentId,
    Name = s.Name,
    Kind = s.Kind == SpaceKind.Map ? "map" : "plan",
    RealCoordinates = s.RealCoordinates,
    Ordinal = s.Ordinal,
    ElevationM = s.ElevationM,
    View = new GeoBoundsDto { SouthWest = ToDto(s.View.SouthWest), NorthEast = ToDto(s.View.NorthEast) },
    Footprint = s.Footprint.Select(ToDto).ToList(),
    Plan = s.Plan is null ? null : new SpacePlanDto
    {
      Corners = ToDto(s.Plan.Corners),
      WidthPx = (int)s.Plan.WidthPx,
      HeightPx = (int)s.Plan.HeightPx,
      Mime = s.Plan.Mime,
      Etag = s.Plan.Etag,
    },
  };

  public static Space ToProto(SpaceDto s)
  {
    var space = new Space
    {
      Id = s.Id ?? string.Empty,
      ParentId = s.ParentId ?? string.Empty,
      Name = s.Name,
      Kind = string.Equals(s.Kind, "map", StringComparison.OrdinalIgnoreCase) ? SpaceKind.Map : SpaceKind.Plan,
      RealCoordinates = s.RealCoordinates,
      Ordinal = s.Ordinal,
      ElevationM = s.ElevationM,
      View = new GeoBounds { SouthWest = ToProto(s.View.SouthWest), NorthEast = ToProto(s.View.NorthEast) },
    };
    space.Footprint.AddRange(s.Footprint.Select(ToProto));
    return space;
  }

  public static CameraPlacementDto ToDto(CameraPlacement p) => new()
  {
    CameraId = p.CameraId,
    SpaceId = p.SpaceId,
    IsReference = p.IsReference,
    Lat = p.Lat,
    Lon = p.Lon,
    HeightM = p.HeightM,
    HeadingDeg = p.HeadingDeg,
    HfovDeg = p.HfovDeg,
    RangeM = p.RangeM,
  };

  public static CameraPlacement ToProto(string cameraId, string spaceId, CameraPlacementDto p) => new()
  {
    CameraId = cameraId,
    SpaceId = spaceId,
    IsReference = p.IsReference,
    Lat = p.Lat,
    Lon = p.Lon,
    HeightM = p.HeightM,
    HeadingDeg = p.HeadingDeg,
    HfovDeg = p.HfovDeg,
    RangeM = p.RangeM,
  };

  public static LandmarkDto ToDto(Landmark m) => new()
  {
    Id = m.Id,
    SpaceId = m.SpaceId,
    Name = m.Name,
    Lat = m.Lat,
    Lon = m.Lon,
    HeightM = m.HeightM,
  };

  public static Landmark ToProto(LandmarkDto m) => new()
  {
    Id = m.Id ?? string.Empty,
    SpaceId = m.SpaceId,
    Name = m.Name,
    Lat = m.Lat,
    Lon = m.Lon,
    HeightM = m.HeightM,
  };
}
