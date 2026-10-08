using System.Drawing;
using System.Windows.Forms;

namespace PenTastic;

static class Mapping
{
    public static Point ToPixel(Rectangle r, double nx, double ny) => new(
        r.Left + (int)Math.Round(Math.Clamp(nx, 0, 1) * (r.Width - 1)),
        r.Top + (int)Math.Round(Math.Clamp(ny, 0, 1) * (r.Height - 1)));

    public static TargetInfo Describe(Rectangle target)
    {
        var screens = Screen.AllScreens;
        int selected = Array.FindIndex(screens, s => s.Bounds == target);
        return new TargetInfo(target.Width, target.Height, selected < 0 ? (byte)0xFF : (byte)selected,
            screens.Select(s => new MonitorInfo(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height, (byte)(s.Primary ? 1 : 0))).ToArray());
    }
}
