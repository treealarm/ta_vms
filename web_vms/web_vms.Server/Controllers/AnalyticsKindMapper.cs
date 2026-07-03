using Vms.Analytics;

namespace web_vms;

// Single kind->string mapping for the analytics REST layer (used by both the detections
// gallery and the debug activity endpoints).
public static class AnalyticsKindMapper
{
  public static string ToStringId(DetectionKind kind) => kind switch
  {
    DetectionKind.Person => "person",
    DetectionKind.Face => "face",
    DetectionKind.Vehicle => "vehicle",
    DetectionKind.LicensePlate => "license_plate",
    _ => "unknown",
  };
}
