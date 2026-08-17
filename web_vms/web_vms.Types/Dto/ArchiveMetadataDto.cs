namespace web_vms.Types.Dto;

/// <summary>One object as its producer saw it, normalized 0..1 from the top-left corner.</summary>
public sealed record ArchiveMetadataBoxDto(
  long TrackId,
  string Kind,
  float Confidence,
  float BboxX,
  float BboxY,
  float BboxWidth,
  float BboxHeight,
  string? Label,
  string? PlateText);

/// <summary>Everything one producer saw at one instant, on the archive's wall clock.</summary>
public sealed record ArchiveMetadataFrameDto(
  DateTime Ts,
  List<ArchiveMetadataBoxDto> Boxes);

/// <summary>
/// Frames from one producer, ascending by time. Kept apart from the other producer's: the two
/// number their tracks independently, so merging them would collide track ids, and their
/// timestamps come off different clocks.
/// </summary>
public sealed record ArchiveMetadataSourceDto(
  string Source,
  List<ArchiveMetadataFrameDto> Frames);

public sealed record ArchiveMetadataResponseDto(
  List<ArchiveMetadataSourceDto> Sources);

/// <summary>
/// Whether the requested window has any recorded geometry at all. Separate from the samples call
/// because it is asked on every archive window, whereas the samples are fetched only once the
/// operator turns the overlay on.
/// </summary>
public sealed record ArchiveMetadataCoverageDto(
  bool HasCv,
  bool HasOnvif);
