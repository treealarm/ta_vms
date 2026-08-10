namespace VmsAnalyticsSingleton;

// The single source of the kind<->string mapping inside VmsAnalyticsSingleton — both proto enums (the
// vendored video_a contract and vms_analytics.proto) serialize kinds to the same db/UI strings.
public static class DetectionKinds
{
  public static string ToDbString(Analytics.DetectionKind kind) => kind switch
  {
    Analytics.DetectionKind.Person => "person",
    Analytics.DetectionKind.Face => "face",
    Analytics.DetectionKind.Vehicle => "vehicle",
    Analytics.DetectionKind.LicensePlate => "license_plate",
    _ => "unknown",
  };

  // Same mapping for the other enum: ONVIF boxes arrive already classified, on vms_analytics.proto's
  // enum rather than the vendored video_a one.
  public static string ToDbString(Vms.Analytics.DetectionKind kind) => kind switch
  {
    Vms.Analytics.DetectionKind.Person => "person",
    Vms.Analytics.DetectionKind.Face => "face",
    Vms.Analytics.DetectionKind.Vehicle => "vehicle",
    Vms.Analytics.DetectionKind.LicensePlate => "license_plate",
    _ => "unknown",
  };

  public static Vms.Analytics.DetectionKind Parse(string kind) => kind switch
  {
    "person" => Vms.Analytics.DetectionKind.Person,
    "face" => Vms.Analytics.DetectionKind.Face,
    "vehicle" => Vms.Analytics.DetectionKind.Vehicle,
    "license_plate" => Vms.Analytics.DetectionKind.LicensePlate,
    _ => Vms.Analytics.DetectionKind.Unspecified,
  };
}
