using System.Globalization;
using System.Text;

namespace VmsMap.Demo
{
  /// The demo's vector pictures, and the shares of them its regions occupy.
  ///
  /// A type's background and its regions are one drawing: every region is a feature of the picture
  /// underneath, and both are laid out from the same functions here, so a port cannot drift away
  /// from its socket. The pictures are SVG embedded in the assembly and scaled by the browser to
  /// whatever canvas the diagram has, so a rack opened from the room diagram stays sharp.
  public static class DemoArt
  {
    // Each picture's own paper, in pixels. A region keeps the aspect of the diagram it holds, so a
    // child drawn in it — a switch in a rack slot, a rack in the room — is not stretched.
    public const int SwitchWidth = 600;
    public const int SwitchHeight = 140;
    public const int RackWidth = 240;
    public const int RackHeight = 720;
    public const int RoomLayoutWidth = 800;
    public const int RoomLayoutHeight = 600;
    public const int PortWidth = 60;
    public const int PortHeight = 40;

    private const int PortCount = 8;
    private const int SlotCount = 6;

    private const double SlotLeft = 0.12;
    private const double SlotTop = 0.06;
    private const double SlotStep = 0.10;
    private const double SlotWidth = 0.76;

    private const double RoomRackWidth = 0.18;

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

    /// Where the feeder's readings go: two small regions at the foot of the rack.
    public static DemoRect RackTemperature => new(0.12, 0.78, 0.34, 0.05);

    public static DemoRect RackHumidity => new(0.54, 0.78, 0.34, 0.05);

    /// A cabinet in the room's schematic; `index` is 1-based. Its height is the rack's own aspect at
    /// its width, so the rack drawn in it is not stretched.
    public static DemoRect RoomRack(int index) => new(
      0.20 + (index - 1) * 0.42,
      0.16,
      RoomRackWidth,
      RoomRackWidth * RoomLayoutWidth * RackHeight / RackWidth / RoomLayoutHeight);

    /// A Catalyst's front panel: the sockets the ports take, numbered underneath them.
    public static byte[] Switch()
    {
      var svg = Open(SwitchWidth, SwitchHeight);
      svg.Append("<rect x=\"2\" y=\"2\" width=\"596\" height=\"136\" rx=\"8\" fill=\"#1a2530\" stroke=\"#38506a\" stroke-width=\"2\"/>");
      svg.Append("<text x=\"24\" y=\"34\" font-family=\"monospace\" font-size=\"18\" letter-spacing=\"2\" fill=\"#9fb6cb\">CISCO CATALYST 9300</text>");
      svg.Append("<circle cx=\"516\" cy=\"27\" r=\"6\" fill=\"#43d17c\"/>");
      svg.Append("<circle cx=\"538\" cy=\"27\" r=\"6\" fill=\"#31424f\"/>");
      svg.Append("<circle cx=\"560\" cy=\"27\" r=\"6\" fill=\"#31424f\"/>");

      for (var port = 1; port <= PortCount; port += 1)
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

    /// A rack cabinet: six slots, the top one taken by the switch, and the two readings' places at
    /// its foot.
    public static byte[] Rack()
    {
      var svg = Open(RackWidth, RackHeight);
      svg.Append("<rect x=\"6\" y=\"10\" width=\"228\" height=\"700\" rx=\"8\" fill=\"#1a2530\" stroke=\"#38506a\" stroke-width=\"2\"/>");
      svg.Append("<rect x=\"14\" y=\"22\" width=\"10\" height=\"676\" fill=\"#223141\"/>");
      svg.Append("<rect x=\"216\" y=\"22\" width=\"10\" height=\"676\" fill=\"#223141\"/>");
      svg.Append("<text x=\"120\" y=\"26\" text-anchor=\"middle\" font-family=\"monospace\" font-size=\"12\" fill=\"#8fa8bd\">42U</text>");

      for (var slot = 1; slot <= SlotCount; slot += 1)
      {
        var (x, y, w, h) = Pixels(RackSlot(slot), RackWidth, RackHeight);
        svg.Append($"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(w)}\" height=\"{N(h)}\" rx=\"2\" fill=\"#0f1a24\" stroke=\"#2c4154\" stroke-width=\"1.5\"/>");
        svg.Append($"<text x=\"{N(x + 6)}\" y=\"{N(y + 13)}\" font-family=\"monospace\" font-size=\"10\" fill=\"#6f8aa1\">U{slot}</text>");
      }

      var (_, divider, _, _) = Pixels(RackTemperature, RackWidth, RackHeight);
      svg.Append($"<line x1=\"28\" y1=\"{N(divider - 18)}\" x2=\"212\" y2=\"{N(divider - 18)}\" stroke=\"#2c4154\" stroke-width=\"1.5\"/>");

      svg.Append("<rect x=\"28\" y=\"706\" width=\"26\" height=\"8\" rx=\"2\" fill=\"#223141\"/>");
      svg.Append("<rect x=\"186\" y=\"706\" width=\"26\" height=\"8\" rx=\"2\" fill=\"#223141\"/>");
      return Close(svg);
    }

    /// A room in schematic: the floor, the door and the two cabinets its racks are drawn into.
    public static byte[] RoomLayout()
    {
      var svg = Open(RoomLayoutWidth, RoomLayoutHeight);
      svg.Append("<rect x=\"8\" y=\"8\" width=\"784\" height=\"584\" rx=\"6\" fill=\"#16222e\" stroke=\"#3b5a75\" stroke-width=\"3\"/>");
      svg.Append("<rect x=\"430\" y=\"584\" width=\"150\" height=\"16\" fill=\"#16222e\" stroke=\"#3b5a75\" stroke-width=\"3\"/>");
      svg.Append("<text x=\"64\" y=\"52\" font-family=\"monospace\" font-size=\"20\" letter-spacing=\"3\" fill=\"#7f9ab1\">ROOM 102 — EQUIPMENT</text>");

      for (var index = 1; index <= 2; index += 1)
      {
        var (x, y, w, h) = Pixels(RoomRack(index), RoomLayoutWidth, RoomLayoutHeight);
        svg.Append($"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(w)}\" height=\"{N(h)}\" rx=\"4\" fill=\"#0f1a24\" stroke=\"#4a6a86\" stroke-width=\"2\"/>");
        svg.Append($"<text x=\"{N(x + w / 2)}\" y=\"{N(y - 14)}\" text-anchor=\"middle\" font-family=\"monospace\" font-size=\"15\" fill=\"#9fb6cb\">RACK B-{index:00}</text>");
      }

      return Close(svg);
    }

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
