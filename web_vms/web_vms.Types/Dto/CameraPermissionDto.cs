namespace web_vms.Types.Dto;

/// <summary>What a user may do with a camera. All false = no access at all.</summary>
public record CameraPermissionFlagsDto
{
  public bool ViewLive { get; init; }
  public bool ViewArchive { get; init; }
  public bool Ptz { get; init; }
  /// <summary>Arming, recording overrides, manual retention, relay outputs.</summary>
  public bool Control { get; init; }
  /// <summary>Downloading a clip as a file.</summary>
  public bool Export { get; init; }
}

/// <summary>
/// One user's grants. The effective permission on a camera is the OR of <see cref="AllCameras"/>
/// and the matching <see cref="Cameras"/> entry, so a blanket grant needs no per-camera copies.
/// </summary>
public record UserCameraPermissionsDto
{
  public required string Username { get; init; }
  /// <summary>null = no blanket grant. Covers cameras added later, too.</summary>
  public CameraPermissionFlagsDto? AllCameras { get; init; }
  public IReadOnlyDictionary<string, CameraPermissionFlagsDto> Cameras { get; init; } =
    new Dictionary<string, CameraPermissionFlagsDto>();
}

/// <summary>
/// "open" keeps the pre-ACL behaviour (every authenticated user reaches every camera); "enforced"
/// means the grants decide. Existing installations upgrade into "open" so nothing goes dark.
/// </summary>
public record CameraAclModeDto
{
  public required bool Enforced { get; init; }
}

/// <summary>
/// Answer to "what can I do", for the SPA: lets it hide affordances instead of letting them 403.
/// Never the authority — the server checks every request regardless.
/// </summary>
public record MyPermissionsDto
{
  public required string Username { get; init; }
  public required bool IsAdmin { get; init; }
  /// <summary>False when the ACL is off (mode "open" or authentication disabled entirely).</summary>
  public required bool AclEnforced { get; init; }
  public CameraPermissionFlagsDto? AllCameras { get; init; }
  public IReadOnlyDictionary<string, CameraPermissionFlagsDto> Cameras { get; init; } =
    new Dictionary<string, CameraPermissionFlagsDto>();
}
