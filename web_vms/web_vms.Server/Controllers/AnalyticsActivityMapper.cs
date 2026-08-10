using Vms.Analytics;
using web_vms.Types.Dto;

namespace web_vms;

// Shared mapping from the proto AnalyticsActivityEntry to the wire DTO, used by both the debug
// activity feed (AnalyticsWatchController, with crop) and the live SSE overlay stream
// (AnalyticsLiveController, without crop). Keeping it in one place stops the two feeds from drifting
// as fields are added.
public static class AnalyticsActivityMapper
{
  public static AnalyticsActivityEntryDto ToDto(AnalyticsActivityEntry e, bool includeCrop) => new()
  {
    CameraId       = e.CameraId,
    TrackId        = e.TrackId,
    Kind           = AnalyticsKindMapper.ToStringId(e.Kind),
    Confidence     = e.Confidence,
    BboxX          = e.Bbox?.X ?? 0,
    BboxY          = e.Bbox?.Y ?? 0,
    BboxWidth      = e.Bbox?.Width ?? 0,
    BboxHeight     = e.Bbox?.Height ?? 0,
    DetectedAt     = e.DetectedAt.ToDateTime(),
    PlateText      = e.HasPlateText ? e.PlateText : null,
    CropJpegBase64 = includeCrop && !e.CropJpeg.IsEmpty ? Convert.ToBase64String(e.CropJpeg.ToByteArray()) : null,
    Persisted      = e.Persisted,
    ObjectId       = e.HasObjectId ? e.ObjectId : null,
    Source         = string.IsNullOrEmpty(e.Source) ? "cv" : e.Source,
  };
}
