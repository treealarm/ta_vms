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

    var active = true;
    var sourceItems = new List<string>();
    var source = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Source");
    var activeFound = false;

    foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "SimpleItem"))
    {
      var isSourceItem = source is not null && item.Ancestors().Contains(source);
      if (isSourceItem)
      {
        sourceItems.Add($"{(string?)item.Attribute("Name")}={(string?)item.Attribute("Value")}");
        continue;
      }

      // The state lives in tt:Data, never in tt:Source — a source token that happens to parse as a
      // boolean must not be mistaken for the condition.
      if (!activeFound && bool.TryParse((string?)item.Attribute("Value"), out var parsed))
      {
        active = parsed;
        activeFound = true;
      }
    }

    sourceItems.Sort(StringComparer.Ordinal);
    return new OnvifEventPayload(active, string.Join(",", sourceItems));
  }
}
