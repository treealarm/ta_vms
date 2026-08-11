using System.Globalization;
using System.Xml.Linq;

namespace VmsOnvif.Onvif;

// One object of a Scene Description frame, already converted into the overlay's coordinate space:
// 0..1 with the origin in the top-left corner, exactly like the CV detector's boxes.
public sealed record OnvifSceneObject(
  long TrackId,
  string Kind,
  float Confidence,
  float BboxX,
  float BboxY,
  float BboxWidth,
  float BboxHeight,
  // Plate text when the camera ran ANPR on this object (tt:LicensePlateInfo/tt:PlateNumber).
  string? PlateText = null,
  // The camera's own word for the class when it says more than Kind does — "Truck" rather than
  // just "vehicle", or "Bag" where Kind had to fall back to "unknown". Null when the camera only
  // offered one of ONVIF's coarse names, which would add nothing to the label.
  string? Label = null);

// Turns an ONVIF metadata document (tt:MetadataStream) into overlay-ready boxes.
//
// Pure and allocation-cheap on purpose: it is called once per document per camera, off the gRPC
// thread that received the batch, and must never throw at the caller — a firmware that lays the
// schema out its own way is expected, not exceptional.
public static class OnvifSceneParser
{
  // A camera that reports more than this in one frame is malfunctioning or describing something
  // the overlay cannot usefully show anyway.
  private const int MaxObjectsPerFrame = 64;

  // ONVIF's normalized space is [-1..1]. Values outside this (with a little slack for rounding)
  // mean the camera is emitting raw pixels and left the transformation out — Hikvision does this.
  private const double NormalizedLimit = 1.5;

  /// Parses one document. Returns an empty list for anything it cannot make sense of; never throws
  /// on malformed content, only on a caller passing null.
  public static IReadOnlyList<OnvifSceneObject> Parse(string xml, int frameWidth, int frameHeight)
  {
    XDocument doc;
    try
    {
      doc = XDocument.Parse(xml, LoadOptions.None);
    }
    catch (System.Xml.XmlException)
    {
      return [];
    }

    var root = doc.Root;
    if (root is null || LocalName(root) != "MetadataStream")
      return [];

    var results = new List<OnvifSceneObject>();

    // Matching on local names rather than the namespace: the schema URI is stable in theory, but
    // firmwares ship documents with no namespace at all, and those are otherwise well-formed.
    foreach (var frame in Descendants(root, "Frame"))
    {
      var frameTransform = ReadTransformation(frame);

      foreach (var obj in Descendants(frame, "Object"))
      {
        if (results.Count >= MaxObjectsPerFrame)
          return results;

        var parsed = ParseObject(obj, frameTransform, frameWidth, frameHeight);
        if (parsed is not null)
          results.Add(parsed);
      }
    }

    return results;
  }

  private static OnvifSceneObject? ParseObject(
    XElement obj, Transformation? frameTransform, int frameWidth, int frameHeight)
  {
    var box = Descendants(obj, "BoundingBox").FirstOrDefault();
    if (box is null)
      return null;

    if (!TryAttr(box, "left", out var left) || !TryAttr(box, "right", out var right) ||
        !TryAttr(box, "top", out var top) || !TryAttr(box, "bottom", out var bottom))
      return null;

    // An object may carry its own transformation; when it does it wins over the frame's.
    var transform = ReadObjectTransformation(obj) ?? frameTransform;

    double x1, y1, x2, y2;
    if (transform is not null)
    {
      x1 = left * transform.ScaleX + transform.TranslateX;
      x2 = right * transform.ScaleX + transform.TranslateX;
      y1 = top * transform.ScaleY + transform.TranslateY;
      y2 = bottom * transform.ScaleY + transform.TranslateY;
    }
    else if (WithinNormalizedRange(left, right, top, bottom))
    {
      x1 = left; x2 = right; y1 = top; y2 = bottom;
    }
    else if (frameWidth > 0 && frameHeight > 0)
    {
      // Raw pixels with no transformation to interpret them. The video track's own resolution is
      // the only thing that can, which is why media_server ships it alongside the document.
      return FromPixels(obj, left, right, top, bottom, frameWidth, frameHeight);
    }
    else
    {
      return null;
    }

    // Both axes are now in ONVIF's normalized frame: x runs -1..1 left to right, y runs -1..1
    // BOTTOM to top. The overlay wants 0..1 with the origin top-left, so y is flipped here.
    var nx1 = (x1 + 1.0) / 2.0;
    var nx2 = (x2 + 1.0) / 2.0;
    var ny1 = (1.0 - y1) / 2.0;
    var ny2 = (1.0 - y2) / 2.0;

    return Build(obj, nx1, nx2, ny1, ny2);
  }

  private static OnvifSceneObject? FromPixels(
    XElement obj, double left, double right, double top, double bottom, int width, int height)
  {
    // Pixel coordinates are already top-left origin, so unlike the normalized path there is no
    // flip — dividing through by the frame size is the whole conversion.
    return Build(obj, left / width, right / width, top / height, bottom / height);
  }

  private static OnvifSceneObject? Build(XElement obj, double x1, double x2, double y1, double y2)
  {
    var x = Math.Clamp(Math.Min(x1, x2), 0.0, 1.0);
    var y = Math.Clamp(Math.Min(y1, y2), 0.0, 1.0);
    var w = Math.Clamp(Math.Abs(x2 - x1), 0.0, 1.0 - x);
    var h = Math.Clamp(Math.Abs(y2 - y1), 0.0, 1.0 - y);

    if (w <= 0.0 || h <= 0.0)
      return null;

    var (kind, confidence, label) = ReadClass(obj);

    return new OnvifSceneObject(
      ReadTrackId(obj), kind, confidence, (float)x, (float)y, (float)w, (float)h,
      ReadPlateText(obj), label);
  }

  private static bool WithinNormalizedRange(params double[] values) =>
    values.All(v => Math.Abs(v) <= NormalizedLimit);

  private static long ReadTrackId(XElement obj)
  {
    var raw = (string?)obj.Attribute("ObjectId");
    if (string.IsNullOrWhiteSpace(raw))
      return 0;
    if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
      return id;

    // Non-numeric ids exist (some firmwares use a GUID). The overlay only needs the value to be
    // stable per object, so a hash serves; it is positive so it reads like an id in the label.
    return Math.Abs((long)raw.GetHashCode());
  }

  /// <summary>
  /// Works out what the object is. Two independent sources, in order of authority:
  /// <list type="number">
  /// <item>tt:Class — tt:ClassCandidate carries a value from tt:ClassType, whose whole vocabulary is
  /// Animal/Face/Human/Vehical/Other (yes, "Vehical" — that misspelling is in the ONVIF schema
  /// itself). Because that list is far too coarse to be useful, the standard also allows finer,
  /// free-form names under tt:Extension/tt:OtherTypes, which is where "Car", "Truck", "Bicycle" and
  /// the like actually turn up. Both are ranked together by likelihood and the best one that maps
  /// to a kind we can draw wins — so a candidate list of {Other: 0.9, Human: 0.4} still yields a
  /// person rather than falling through to unknown.</item>
  /// <item>The structure of tt:Appearance itself. A camera that reports tt:LicensePlateInfo or
  /// tt:HumanFace has told us what the object is just as plainly as a class name would, and some
  /// firmwares fill those in while leaving tt:Class off entirely.</item>
  /// </list>
  /// </summary>
  private static (string Kind, float Confidence, string? Label) ReadClass(XElement obj)
  {
    var appearance = obj.Elements().FirstOrDefault(e => LocalName(e) == "Appearance");
    var classDescriptor = appearance?.Elements().FirstOrDefault(e => LocalName(e) == "Class");

    if (classDescriptor is not null)
    {
      // tt:ClassCandidate and tt:Extension/tt:OtherTypes have the same {Type, Likelihood} shape.
      var candidates = Descendants(classDescriptor, "ClassCandidate")
        .Concat(Descendants(classDescriptor, "OtherTypes"))
        .Select(c => (
          Type: Descendants(c, "Type").FirstOrDefault()?.Value,
          Likelihood: ParseFloat(Descendants(c, "Likelihood").FirstOrDefault()?.Value)))
        .Where(c => !string.IsNullOrWhiteSpace(c.Type))
        .OrderByDescending(c => c.Likelihood ?? 0f)
        .ToList();

      foreach (var candidate in candidates)
      {
        var kind = MapKind(candidate.Type);
        if (kind != "unknown")
          return (kind, candidate.Likelihood ?? 1f, Label(candidate.Type));
      }

      // Some firmwares put a bare tt:Type straight under tt:Class instead of wrapping it.
      var bare = classDescriptor.Elements().FirstOrDefault(e => LocalName(e) == "Type")?.Value;
      if (MapKind(bare) is var bareKind && bareKind != "unknown")
        return (bareKind, 1f, Label(bare));

      // Nothing mapped, but the camera did name the object — keep its word so the operator sees
      // "Bag" instead of a bare "unknown".
      if (candidates.Count > 0)
        return ("unknown", candidates[0].Likelihood ?? 1f, Label(candidates[0].Type));
    }

    return (KindFromAppearance(appearance), 1f, null);
  }

  /// <summary>
  /// The camera's own class name, or null when it adds nothing. ONVIF's coarse vocabulary is
  /// already what <c>kind</c> expresses, so echoing "Human" next to "person" would be noise —
  /// only a narrower or unrecognised word is worth carrying.
  /// </summary>
  private static string? Label(string? type)
  {
    var trimmed = type?.Trim();
    if (string.IsNullOrEmpty(trimmed))
      return null;

    return trimmed.ToLowerInvariant() switch
    {
      "human" or "humanoid" or "person" or "pedestrian" or "face" or "vehical" or "vehicle"
        or "animal" or "licenseplate" or "license_plate" or "plate" or "other" => null,
      _ => trimmed,
    };
  }

  private static string KindFromAppearance(XElement? appearance)
  {
    if (appearance is null)
      return "unknown";

    // Ordered narrowest-first: a plate belongs to a vehicle and a face to a body, so the more
    // specific structure has to win when a camera reports both on one object.
    foreach (var (element, kind) in new[]
             {
               ("LicensePlateInfo", "license_plate"),
               ("HumanFace", "face"),
               ("HumanBody", "person"),
               ("VehicleInfo", "vehicle"),
             })
    {
      if (appearance.Elements().Any(e => LocalName(e) == element))
        return kind;
    }

    return "unknown";
  }

  /// <summary>Plate text, when the camera ran ANPR on this object.</summary>
  private static string? ReadPlateText(XElement obj)
  {
    var plate = Descendants(obj, "LicensePlateInfo")
      .SelectMany(i => Descendants(i, "PlateNumber"))
      .FirstOrDefault()?.Value?.Trim();
    return string.IsNullOrEmpty(plate) ? null : plate;
  }

  /// <summary>
  /// ONVIF class names onto the kinds the overlay can draw. Covers both the tt:ClassType
  /// vocabulary and the free-form names that show up in tt:OtherTypes. Anything narrower folds
  /// into its nearest kind and keeps its own wording in Label; anything unrecognised stays
  /// "unknown", which the frontend draws all the same.
  /// </summary>
  private static string MapKind(string? type) => type?.Trim().ToLowerInvariant() switch
  {
    "human" or "humanoid" or "person" or "pedestrian" => "person",
    "animal" => "animal",
    "face" => "face",
    // "vehical" is not a typo here — it is the spelling in the tt:ClassType enumeration.
    "vehical" or "vehicle" or "car" or "truck" or "bus" or "van" or "motorcycle" or "motorbike"
      or "bike" or "bicycle" or "twowheelvehicle" => "vehicle",
    "licenseplate" or "license_plate" or "plate" => "license_plate",
    _ => "unknown",
  };

  private sealed record Transformation(
    double TranslateX, double TranslateY, double ScaleX, double ScaleY);

  /// An object's own transformation. In the schema it sits under tt:Appearance, not directly on
  /// tt:Object, so both spots are checked — and only those two, never descendants, so the frame's
  /// transformation cannot be mistaken for the object's and applied twice.
  private static Transformation? ReadObjectTransformation(XElement obj)
  {
    var own = ReadTransformation(obj);
    if (own is not null)
      return own;

    var appearance = obj.Elements().FirstOrDefault(e => LocalName(e) == "Appearance");
    return appearance is null ? null : ReadTransformation(appearance);
  }

  /// Reads a tt:Transformation that is a DIRECT child of `parent`.
  private static Transformation? ReadTransformation(XElement parent)
  {
    var t = parent.Elements().FirstOrDefault(e => LocalName(e) == "Transformation");
    if (t is null)
      return null;

    var translate = t.Elements().FirstOrDefault(e => LocalName(e) == "Translate");
    var scale = t.Elements().FirstOrDefault(e => LocalName(e) == "Scale");

    // Either half may be absent; the missing one is the identity for that operation.
    var tx = translate is not null && TryAttr(translate, "x", out var v1) ? v1 : 0.0;
    var ty = translate is not null && TryAttr(translate, "y", out var v2) ? v2 : 0.0;
    var sx = scale is not null && TryAttr(scale, "x", out var v3) ? v3 : 1.0;
    var sy = scale is not null && TryAttr(scale, "y", out var v4) ? v4 : 1.0;

    if (sx == 0.0 || sy == 0.0)
      return null;

    return new Transformation(tx, ty, sx, sy);
  }

  private static bool TryAttr(XElement element, string name, out double value)
  {
    value = 0;
    var raw = (string?)element.Attribute(name);
    return raw is not null
      && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
  }

  private static float? ParseFloat(string? raw) =>
    float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

  private static IEnumerable<XElement> Descendants(XElement root, string localName) =>
    root.Descendants().Where(e => LocalName(e) == localName);

  private static string LocalName(XElement e) => e.Name.LocalName;
}
