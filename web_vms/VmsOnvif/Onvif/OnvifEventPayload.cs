using System.Xml.Linq;

namespace VmsOnvif.Onvif;

/// <summary>
/// What one ONVIF event payload says, read in a single pass.
/// </summary>
/// <param name="Active">
/// Whether the condition is starting or clearing. ONVIF state events carry it as a boolean
/// SimpleItem under tt:Data — the item's name varies by rule ("IsMotion", "IsTamper", "State"), so
/// the value is matched on, not the name. Events with no boolean are momentary and count as active.
/// </param>
/// <param name="SourceKey">
/// Which source the event is about, from the tt:Source items. On a multi-channel encoder two
/// channels report the same topic and only these tell their alarms apart. Empty when the event
/// names no source, which is correct: it is then about the device as a whole.
/// </param>
public readonly record struct OnvifEventPayload(bool Active, string SourceKey)
{
  private static readonly OnvifEventPayload Momentary = new(true, string.Empty);

  /// <summary>
  /// Reads both facts with one XDocument.Parse. They used to be read by separate helpers, which
  /// meant parsing the same payload two or three times per event — the kind of per-message cost
  /// that decides whether a chatty camera is free or expensive.
  /// </summary>
  private static IEnumerable<XElement> SimpleItemsUnder(XDocument doc, string parentLocalName)
  {
    var parent = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == parentLocalName);
    return parent is null
      ? []
      : parent.Descendants().Where(e => e.Name.LocalName == "SimpleItem");
  }

  public static OnvifEventPayload Parse(string? payloadXml)
  {
    if (string.IsNullOrWhiteSpace(payloadXml))
      return Momentary;

    XDocument doc;
    try
    {
      doc = XDocument.Parse(payloadXml);
    }
    catch (System.Xml.XmlException)
    {
      // Malformed payload — treat as a momentary event rather than losing it.
      return Momentary;
    }

    // Read from the two elements by name rather than by "everything except Source". A message also
    // carries tt:Key, and a boolean there — or in any element a vendor adds later — would otherwise
    // be taken for the condition: one stray `false` would make every event look like a clearing
    // edge and silently switch that camera's retention off.
    var active = true;
    foreach (var item in SimpleItemsUnder(doc, "Data"))
    {
      if (bool.TryParse((string?)item.Attribute("Value"), out var parsed))
      {
        active = parsed;
        break;
      }
    }

    var sourceItems = SimpleItemsUnder(doc, "Source")
      .Select(e => $"{(string?)e.Attribute("Name")}={(string?)e.Attribute("Value")}")
      .ToList();
    sourceItems.Sort(StringComparer.Ordinal);

    return new OnvifEventPayload(active, string.Join(",", sourceItems));
  }
}
