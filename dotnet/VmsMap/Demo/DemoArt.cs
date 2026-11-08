using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VmsMap.Demo
{
  /// The demo's vector pictures, and the shares of them its regions occupy.
  ///
  /// A type's background and its regions are one drawing: every region is a feature of the picture
  /// underneath, and both are laid out from the same functions here, so a port cannot drift away
  /// from its socket. The pictures are SVG embedded in the assembly and scaled by the browser to
  /// whatever canvas the diagram has, so a rack opened from the floor's diagram stays sharp.
  public static class DemoArt
  {
    // Each picture's own paper, in pixels. A region keeps the aspect of the diagram it holds, so a
    // child drawn in it — a switch in a rack slot, a rack in the floor's schematic — is not
    // stretched.
    public const int SwitchWidth = 600;
    public const int SwitchHeight = 140;
    public const int RackWidth = 240;
    public const int RackHeight = 720;
    public const int FloorLayoutWidth = 800;
    public const int FloorLayoutHeight = 600;
    public const int PortWidth = 60;
    public const int PortHeight = 40;
    public const int TelemetryWidth = 360;
    public const int TelemetryHeight = 220;
    public const int CameraPanelWidth = 640;
    public const int CameraPanelHeight = 400;

    // A degree of a great circle, in metres: the WGS84 sphere the map's coordinates are laid out on.
    // A plan is drawn in degrees, so a size in metres comes from this, at the latitude it stands at.
    private const double EarthRadiusM = 6378137.0;

    // The four sockets of the rear panel, left to right.
    private static readonly (string Key, string Label)[] PanelSockets =
    [
      ("input", "INPUT"),
      ("lamp", "LAMP"),
      ("peer", "PEER"),
      ("loop", "LOOP"),
    ];

    // The terminal block the camera's cable ends in: one screw terminal per part, left to right.
    // The drawing and the regions come from the same numbers, so a replica sits on its terminal.
    private const double TerminalTop = 0.55;
    private const double TerminalWidth = 0.0625;
    private const double TerminalHeight = 0.12;
    private const double TerminalFirstLeft = 0.655;
    private const double TerminalStep = 0.0725;

    /// The terminal of the camera's rear panel a part is wired to; its region is the same
    /// rectangle, so a replica placed there sits exactly on the screw terminal.
    public static DemoRect CameraPanelSocket(string key)
    {
      var index = Array.FindIndex(PanelSockets, socket => socket.Key == key);
      if (index < 0)
        throw new ArgumentException($"No socket '{key}' on the camera's rear panel", nameof(key));

      return new DemoRect(
        TerminalFirstLeft + index * TerminalStep, TerminalTop, TerminalWidth, TerminalHeight);
    }

    // A switch's sockets, and the switch a slot holds: eight ports and six slots in a rack. Public
    // because the world builds a switch of every slot and a port of every socket.
    public const int PortsPerSwitch = 8;
    public const int RackSlots = 6;
    // The cabinets standing on the first floor: the same four the world shows as boxes in the hall.
    public const int RackCount = 4;

    private const double SlotLeft = 0.12;
    private const double SlotTop = 0.06;
    private const double SlotStep = 0.10;
    private const double SlotWidth = 0.76;

    private const double RackRegionWidth = 0.18;

    // A reading's label and the box its value is written into. Both come from TelemetryRows, so a
    // row cannot be labelled on thing and filled on another.
    private static readonly (string Key, string Label)[] TelemetryRows =
    [
      (DemoTelemetry.Lat, "LAT"),
      (DemoTelemetry.Lon, "LON"),
      (DemoTelemetry.Speed, "SPEED, km/h"),
      (DemoTelemetry.Relay, "RELAY"),
      (DemoTelemetry.Sensor, "SENSOR"),
    ];

    private const double TelemetryRowTop = 0.16;
    private const double TelemetryRowStep = 0.16;
    private const double TelemetryRowHeight = 0.11;
    private const double TelemetryValueLeft = 0.34;
    private const double TelemetryValueWidth = 0.60;

    /// A switch's port socket, left to right; `port` is 1-based.
    public static DemoRect SwitchPort(int port) =>
      new(0.025 + (port - 1) * 0.12, 0.36, 0.10, 0.28);

    /// A rack slot, top to bottom. Its height is the switch's own aspect at the slot's width, so the
    /// switch drawn inside it keeps its shape.
    public static DemoRect RackSlot(int slot) => new(
      SlotLeft,
      SlotTop + (slot - 1) * SlotStep,
      SlotWidth,
      SlotWidth * RackWidth * SwitchHeight / SwitchWidth / RackHeight);

    /// Where one reading of the demo's Device telemetry diagram is written: `key` is one of
    /// <see cref="DemoTelemetry.Readings"/>.
    public static DemoRect TelemetryReading(string key)
    {
      var index = Array.FindIndex(TelemetryRows, row => row.Key == key);
      return index < 0
        ? throw new ArgumentOutOfRangeException(nameof(key), key, "Not a telemetry reading")
        : new DemoRect(
            TelemetryValueLeft,
            TelemetryRowTop + index * TelemetryRowStep,
            TelemetryValueWidth,
            TelemetryRowHeight);
    }

    /// A cabinet in the first floor's schematic; `index` is 1-based. Four of them stand in a row,
    /// and the height is the rack's own aspect at the region's width, so the rack drawn in it is not
    /// stretched.
    public static DemoRect RackRegion(int index) => new(
      0.08 + (index - 1) * 0.22,
      0.16,
      RackRegionWidth,
      RackRegionWidth * FloorLayoutWidth * RackHeight / RackWidth / FloorLayoutHeight);

    /// A Catalyst's front panel: the sockets the ports take, numbered underneath them.
    public static byte[] Switch()
    {
      var svg = Open(SwitchWidth, SwitchHeight);
      svg.Append("<rect x=\"2\" y=\"2\" width=\"596\" height=\"136\" rx=\"8\" fill=\"#1a2530\" stroke=\"#38506a\" stroke-width=\"2\"/>");
      svg.Append("<text x=\"24\" y=\"34\" font-family=\"monospace\" font-size=\"18\" letter-spacing=\"2\" fill=\"#9fb6cb\">CISCO CATALYST 9300</text>");
      svg.Append("<circle cx=\"516\" cy=\"27\" r=\"6\" fill=\"#43d17c\"/>");
      svg.Append("<circle cx=\"538\" cy=\"27\" r=\"6\" fill=\"#31424f\"/>");
      svg.Append("<circle cx=\"560\" cy=\"27\" r=\"6\" fill=\"#31424f\"/>");

      for (var port = 1; port <= PortsPerSwitch; port += 1)
      {
        var (x, y, w, h) = Pixels(SwitchPort(port), SwitchWidth, SwitchHeight);
        svg.Append($"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(w)}\" height=\"{N(h)}\" rx=\"3\" fill=\"#0d151d\" stroke=\"#54718b\" stroke-width=\"1.5\"/>");
        svg.Append($"<text x=\"{N(x + w / 2)}\" y=\"{N(y + h + 14)}\" text-anchor=\"middle\" font-family=\"monospace\" font-size=\"12\" fill=\"#8fa8bd\">{port}</text>");
      }

      return Close(svg);
    }

    /// One RJ45 socket, the picture a port is drawn with inside its region of the switch.
    public static byte[] Port()
    {
      var svg = Open(PortWidth, PortHeight);
      svg.Append("<rect x=\"1\" y=\"1\" width=\"58\" height=\"38\" rx=\"4\" fill=\"#0d151d\" stroke=\"#54718b\" stroke-width=\"1.5\"/>");
      svg.Append("<rect x=\"13\" y=\"8\" width=\"34\" height=\"22\" rx=\"2\" fill=\"#223141\" stroke=\"#6c8aa4\" stroke-width=\"1\"/>");

      for (var pin = 0; pin < 8; pin += 1)
      {
        var x = 16 + pin * 4;
        svg.Append($"<line x1=\"{x}\" y1=\"12\" x2=\"{x}\" y2=\"26\" stroke=\"#9fb6cb\" stroke-width=\"1\"/>");
      }

      svg.Append("<rect x=\"22\" y=\"30\" width=\"16\" height=\"6\" rx=\"1\" fill=\"#223141\" stroke=\"#54718b\"/>");
      return Close(svg);
    }

    /// A rack cabinet: six equipment slots, the top one taken by the switch.
    public static byte[] Rack()
    {
      var svg = Open(RackWidth, RackHeight);
      svg.Append("<rect x=\"6\" y=\"10\" width=\"228\" height=\"700\" rx=\"8\" fill=\"#1a2530\" stroke=\"#38506a\" stroke-width=\"2\"/>");
      svg.Append("<rect x=\"14\" y=\"22\" width=\"10\" height=\"676\" fill=\"#223141\"/>");
      svg.Append("<rect x=\"216\" y=\"22\" width=\"10\" height=\"676\" fill=\"#223141\"/>");
      svg.Append("<text x=\"120\" y=\"26\" text-anchor=\"middle\" font-family=\"monospace\" font-size=\"12\" fill=\"#8fa8bd\">42U</text>");

      for (var slot = 1; slot <= RackSlots; slot += 1)
      {
        var (x, y, w, h) = Pixels(RackSlot(slot), RackWidth, RackHeight);
        svg.Append($"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(w)}\" height=\"{N(h)}\" rx=\"2\" fill=\"#0f1a24\" stroke=\"#2c4154\" stroke-width=\"1.5\"/>");
        svg.Append($"<text x=\"{N(x + 6)}\" y=\"{N(y + 13)}\" font-family=\"monospace\" font-size=\"10\" fill=\"#6f8aa1\">U{slot}</text>");
      }

      return Close(svg);
    }

    /// The camera's own readings as a panel: one labelled row per value
    /// (<see cref="TelemetryReading"/>). It is the demo's picture of what ONVIF reports — position,
    /// speed, a relay and a sensor — and the values are written into its boxes by the feeder.
    public static byte[] Telemetry()
    {
      var svg = Open(TelemetryWidth, TelemetryHeight);
      svg.Append($"<rect x=\"2\" y=\"2\" width=\"{TelemetryWidth - 4}\" height=\"{TelemetryHeight - 4}\" rx=\"8\" fill=\"#16222e\" stroke=\"#3b5a75\" stroke-width=\"2\"/>");
      svg.Append("<text x=\"18\" y=\"27\" font-family=\"monospace\" font-size=\"13\" letter-spacing=\"2\" fill=\"#7f9ab1\">DEVICE TELEMETRY</text>");

      foreach (var (key, label) in TelemetryRows)
      {
        var (x, y, w, h) = Pixels(TelemetryReading(key), TelemetryWidth, TelemetryHeight);
        svg.Append($"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(w)}\" height=\"{N(h)}\" rx=\"3\" fill=\"#0f1a24\" stroke=\"#2c4154\" stroke-width=\"1\"/>");
        svg.Append($"<text x=\"18\" y=\"{N(y + h / 2 + 4)}\" font-family=\"monospace\" font-size=\"11\" fill=\"#6f8aa1\">{label}</text>");
      }

      return Close(svg);
    }

    /// The camera as an installer documents it: the body with its lens and illuminator, the cable
    /// out of its back, and the terminal block it ends in. One screw terminal per configured part,
    /// so a contact on the picture is a contact on the camera.
    public static byte[] CameraRearPanel()
    {
      var svg = Open(CameraPanelWidth, CameraPanelHeight);
      svg.Append("<rect x=\"3\" y=\"3\" width=\"634\" height=\"394\" rx=\"12\" fill=\"#0f1a24\" stroke=\"#2c4154\" stroke-width=\"3\"/>");
      svg.Append("<text x=\"24\" y=\"38\" font-family=\"monospace\" font-size=\"15\" letter-spacing=\"3\" fill=\"#7f9ab1\">CAMERA — TERMINALS</text>");

      // The body: a sunshade over a barrel, the lens at its front, the illuminator and the PIR
      // around it — the shape of the camera in the field, not a schematic rectangle.
      svg.Append("<ellipse cx=\"230\" cy=\"306\" rx=\"150\" ry=\"12\" fill=\"#0a1018\"/>");
      svg.Append("<rect x=\"150\" y=\"118\" width=\"212\" height=\"168\" rx=\"16\" fill=\"#1a2530\" stroke=\"#38506a\" stroke-width=\"3\"/>");
      svg.Append("<rect x=\"134\" y=\"100\" width=\"244\" height=\"42\" rx=\"10\" fill=\"#223141\" stroke=\"#38506a\" stroke-width=\"2\"/>");
      svg.Append("<rect x=\"78\" y=\"146\" width=\"86\" height=\"152\" rx=\"16\" fill=\"#223141\" stroke=\"#38506a\" stroke-width=\"3\"/>");

      // The lens: three rings, and the ring of LEDs the picture shows as a crown.
      svg.Append("<circle cx=\"120\" cy=\"222\" r=\"42\" fill=\"#0d151d\" stroke=\"#54718b\" stroke-width=\"2\"/>");
      svg.Append("<circle cx=\"120\" cy=\"222\" r=\"27\" fill=\"#16222e\" stroke=\"#4a6a86\" stroke-width=\"2\"/>");
      svg.Append("<circle cx=\"120\" cy=\"222\" r=\"12\" fill=\"#05090d\" stroke=\"#6c8aa4\" stroke-width=\"1.5\"/>");
      for (var led = 0; led < 12; led += 1)
      {
        var angle = Math.PI * 2 * led / 12;
        var x = 120 + Math.Cos(angle) * 52;
        var y = 222 + Math.Sin(angle) * 52;
        svg.Append($"<circle cx=\"{N(x)}\" cy=\"{N(y)}\" r=\"3\" fill=\"#f2d06b\"/>");
      }

      // The PIR dome on the body's face, beside the lens.
      svg.Append("<circle cx=\"282\" cy=\"200\" r=\"14\" fill=\"#0d151d\" stroke=\"#54718b\" stroke-width=\"2\"/>");

      // The cable out of the back and into the terminal block.
      svg.Append("<path d=\"M 362 256 C 386 256, 386 240, 410 240\" fill=\"none\" stroke=\"#565c64\" stroke-width=\"6\" stroke-linecap=\"round\"/>");
      svg.Append("<path d=\"M 362 264 C 382 264, 384 252, 408 252\" fill=\"none\" stroke=\"#3d4450\" stroke-width=\"4\" stroke-linecap=\"round\"/>");

      // The terminal block and its screws: one terminal per part, drawn where its region is.
      svg.Append("<rect x=\"398\" y=\"190\" width=\"212\" height=\"118\" rx=\"8\" fill=\"#16222e\" stroke=\"#3b5a75\" stroke-width=\"3\"/>");
      svg.Append("<text x=\"404\" y=\"212\" font-family=\"monospace\" font-size=\"11\" letter-spacing=\"2\" fill=\"#5f7a91\">TERMINAL BLOCK</text>");

      foreach (var (key, label) in PanelSockets)
      {
        var (x, y, w, h) = Pixels(CameraPanelSocket(key), CameraPanelWidth, CameraPanelHeight);
        svg.Append($"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(w)}\" height=\"{N(h)}\" rx=\"3\" fill=\"#0d151d\" stroke=\"#54718b\" stroke-width=\"2\"/>");
        svg.Append($"<circle cx=\"{N(x + w / 2)}\" cy=\"{N(y + h / 2 - 4)}\" r=\"9\" fill=\"#223141\" stroke=\"#9fb6cb\" stroke-width=\"1.5\"/>");
        svg.Append($"<line x1=\"{N(x + w / 2)}\" y1=\"{N(y + h / 2 - 12)}\" x2=\"{N(x + w / 2)}\" y2=\"{N(y + h / 2 + 4)}\" stroke=\"#9fb6cb\" stroke-width=\"2\"/>");
        svg.Append($"<text x=\"{N(x + w / 2)}\" y=\"{N(y + h + 20)}\" text-anchor=\"middle\" font-family=\"monospace\" font-size=\"12\" fill=\"#9fb6cb\">{label}</text>");
      }

      return Close(svg);
    }

    /// The first floor in schematic: the floor, the door and the four cabinets the racks stand in.
    /// The floor plan shows the room; this picture is what is standing in it.
    public static byte[] FloorLayout()
    {
      var svg = Open(FloorLayoutWidth, FloorLayoutHeight);
      svg.Append("<rect x=\"8\" y=\"8\" width=\"784\" height=\"584\" rx=\"6\" fill=\"#16222e\" stroke=\"#3b5a75\" stroke-width=\"3\"/>");
      svg.Append("<rect x=\"430\" y=\"584\" width=\"150\" height=\"16\" fill=\"#16222e\" stroke=\"#3b5a75\" stroke-width=\"3\"/>");
      svg.Append("<text x=\"64\" y=\"52\" font-family=\"monospace\" font-size=\"20\" letter-spacing=\"3\" fill=\"#7f9ab1\">FIRST FLOOR — EQUIPMENT</text>");

      for (var index = 1; index <= RackCount; index += 1)
      {
        var (x, y, w, h) = Pixels(RackRegion(index), FloorLayoutWidth, FloorLayoutHeight);
        svg.Append($"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(w)}\" height=\"{N(h)}\" rx=\"4\" fill=\"#0f1a24\" stroke=\"#4a6a86\" stroke-width=\"2\"/>");
        svg.Append($"<text x=\"{N(x + w / 2)}\" y=\"{N(y - 14)}\" text-anchor=\"middle\" font-family=\"monospace\" font-size=\"15\" fill=\"#9fb6cb\">RACK A-{index:00}</text>");
      }

      return Close(svg);
    }

    // The plans' own paper, in pixels. The server stretches a plan between its footprint's corners,
    // so it keeps the aspect of the ground it covers: the building 54.8 × 31.4 m.
    public const int FloorPlanWidth = 1745;
    public const int FloorPlanHeight = 1000;

    /// The first floor of the demo's building, drawn over its real footprint: the walls and the
    /// doorway. Transparent outside the outline, so on the map the plan lies in the building's own
    /// silhouette instead of a rectangle, and decided by the same constants the simulator builds its
    /// prism from: `outline` and the doorway are the world's, not a second version of it.
    public static byte[] FloorPlan(IReadOnlyList<DemoPoint> outline, DemoPoint door, double doorWidthM)
    {
      var south = outline.Min(p => p.Lat);
      var north = outline.Max(p => p.Lat);
      var west = outline.Min(p => p.Lon);
      var east = outline.Max(p => p.Lon);
      var perPixelX = (east - west) / FloorPlanWidth;
      var perPixelY = (north - south) / FloorPlanHeight;
      (double X, double Y) Px(DemoPoint p) => ((p.Lon - west) / perPixelX, (north - p.Lat) / perPixelY);

      var svg = Open(FloorPlanWidth, FloorPlanHeight);
      svg.Append(
        $"<polygon points=\"{Points(outline.Select(Px))}\" fill=\"#e8eaed\" stroke=\"#565c64\" stroke-width=\"9\" stroke-linejoin=\"round\"/>");

      // The doorway out to the parking lot: the wall's own gap, as wide as the world's. The picture
      // is laid out in degrees, so a width in metres becomes one at the door's own latitude — the
      // same conversion the simulator's WorldGeo makes.
      var (dx, dy) = Px(door);
      var metresPerDegreeLon = EarthRadiusM * Math.Cos(door.Lat * Math.PI / 180) * Math.PI / 180;
      var half = doorWidthM / metresPerDegreeLon / perPixelX / 2;
      svg.Append(
        $"<line x1=\"{N(dx - half)}\" y1=\"{N(dy)}\" x2=\"{N(dx + half)}\" y2=\"{N(dy)}\" stroke=\"#e8eaed\" stroke-width=\"12\"/>");
      return Close(svg);
    }

    private static string Points(IEnumerable<(double X, double Y)> points) =>
      string.Join(' ', points.Select(p => $"{N(p.X)},{N(p.Y)}"));

    private static StringBuilder Open(int width, int height) =>
      new StringBuilder()
        .Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");

    private static byte[] Close(StringBuilder svg) =>
      Encoding.UTF8.GetBytes(svg.Append("</svg>").ToString());

    /// A share rectangle as pixels of its picture: x, y, width, height.
    private static (double X, double Y, double Width, double Height) Pixels(
      DemoRect rect, int width, int height) =>
      (rect.Left * width, rect.Top * height, rect.Width * width, rect.Height * height);

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
  }
}
