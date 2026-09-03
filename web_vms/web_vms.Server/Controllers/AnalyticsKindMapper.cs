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
    DetectionKind.Animal => "animal",
    _ => "unknown",
  };

  // The inverse, for the one direction that starts outside: a target the operator picked in the
  // browser arrives named the way the overlay named it. Kept beside ToStringId so the two cannot
  // drift apart, and so no other service has to grow a second copy of this table.
  public static DetectionKind FromStringId(string? kind) => kind switch
  {
    "person" => DetectionKind.Person,
    "face" => DetectionKind.Face,
    "vehicle" => DetectionKind.Vehicle,
    "license_plate" => DetectionKind.LicensePlate,
    "animal" => DetectionKind.Animal,
    _ => DetectionKind.Unspecified,
  };
}
