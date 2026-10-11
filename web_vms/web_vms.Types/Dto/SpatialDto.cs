namespace web_vms.Types.Dto;

// Maps & plans. A space is one world things are placed in: a real map, or a plan nested in another
// space. Every position is WGS84 plus a height in metres above the floor of its space; a space's
// ElevationM is that floor's height above the ground.

public sealed record GeoPointDto
{
  public double Lat { get; init; }
  public double Lon { get; init; }
}

/// <summary>Three corners of a plan image in its space — an affine placement.</summary>
public sealed record PlanCornersDto
{
  public GeoPointDto TopLeft { get; init; } = new();
  public GeoPointDto TopRight { get; init; } = new();
  public GeoPointDto BottomLeft { get; init; } = new();
}

/// <summary>Everything about a plan image except its bytes.</summary>
/// <param name="Etag">Changes whenever the image does; the client adds it to the image URL.</param>
public sealed record SpacePlanDto
{
  public PlanCornersDto Corners { get; init; } = new();
  public int WidthPx { get; init; }
  public int HeightPx { get; init; }
  public string Mime { get; init; } = string.Empty;
  public string Etag { get; init; } = string.Empty;
}

/// <summary>A rectangle, by its south-west and north-east corners.</summary>
public sealed record GeoBoundsDto
{
  public GeoPointDto SouthWest { get; init; } = new();
  public GeoPointDto NorthEast { get; init; } = new();
}

/// <param name="ParentId">Null for a root.</param>
/// <param name="Kind">"map" (a root over map tiles, real coordinates) or "plan" (its own picture).</param>
/// <param name="RealCoordinates">Spaces share geometry when they are the same one or both are real.</param>
/// <param name="Ordinal">Order among siblings: the floors of a building, bottom to top.</param>
/// <param name="View">What the space shows when it is opened.</param>
/// <param name="Footprint">Where it lies on its parent, in the parent's coordinates; empty when none is drawn.</param>
/// <param name="Plan">Read-only here: a plan is uploaded and aligned through its own endpoints.</param>
public sealed record SpaceDto
{
  public string? Id { get; init; }
  public string? ParentId { get; init; }
  public string Name { get; init; } = string.Empty;
  public string Kind { get; init; } = "plan";
  public bool RealCoordinates { get; init; }
  public int Ordinal { get; init; }
  public double ElevationM { get; init; }
  public GeoBoundsDto View { get; init; } = new();
  public IReadOnlyList<GeoPointDto> Footprint { get; init; } = [];
  public SpacePlanDto? Plan { get; init; }
}

/// <param name="IsReference">Where the camera really stands; aiming and calibration go by it. Its
/// other placements are only pictures of it.</param>
/// <param name="HeadingDeg">Compass bearing of the optical axis: 0 = north, clockwise.</param>
/// <param name="HfovDeg">Only draws the view cone, like RangeM.</param>
public sealed record CameraPlacementDto
{
  public string CameraId { get; init; } = string.Empty;
  public string SpaceId { get; init; } = string.Empty;
  public bool IsReference { get; init; }
  public double Lat { get; init; }
  public double Lon { get; init; }
  public double HeightM { get; init; }
  public double HeadingDeg { get; init; }
  public double HfovDeg { get; init; }
  public double RangeM { get; init; }
}

/// <summary>A named point with a known position, used to calibrate cameras.</summary>
public sealed record LandmarkDto
{
  public string? Id { get; init; }
  public string SpaceId { get; init; } = string.Empty;
  public string Name { get; init; } = string.Empty;
  public double Lat { get; init; }
  public double Lon { get; init; }
  public double HeightM { get; init; }
}
