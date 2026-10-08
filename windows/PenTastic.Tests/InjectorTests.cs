using System.Runtime.InteropServices;
using PenTastic;
using static PenTastic.Native;

public class InjectorTests
{
    record Frame(uint Flags, int Change, int X, int Y, uint Pressure, uint PenFlags, uint Mask);

    static (PenInjector pen, List<Frame> frames) Make(Rectangle? target = null, Point origin = default)
    {
        var frames = new List<Frame>();
        var pen = new PenInjector(target ?? new Rectangle(0, 0, 1001, 501), (in POINTER_TYPE_INFO i) =>
        {
            var p = i.u.penInfo;
            frames.Add(new(p.pointerInfo.pointerFlags, p.pointerInfo.ButtonChangeType,
                p.pointerInfo.ptPixelLocation.X, p.pointerInfo.ptPixelLocation.Y, p.pressure, p.penFlags, p.penMask));
            return true;
        }, () => origin);
        return (pen, frames);
    }

    [Fact]
    public void PositionsAreRelativeToTheWholeDesktopsTopLeft()
    {
        var (pen, f) = Make(new Rectangle(-1920, 0, 1921, 1081), new Point(-1920, 0));
        pen.Handle(PenKind.Hover, 0.25, 0.75, 0, 0, 0, 0);
        Assert.Equal((480, 810), (f[0].X, f[0].Y));
    }

    const uint Hover = POINTER_FLAG_INRANGE | POINTER_FLAG_UPDATE;
    const uint Down = POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_FIRSTBUTTON | POINTER_FLAG_DOWN;
    const uint Move = POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_FIRSTBUTTON | POINTER_FLAG_UPDATE;
    const uint Up = POINTER_FLAG_INRANGE | POINTER_FLAG_UP;
    const uint OutOfRange = POINTER_FLAG_UPDATE;

    [Fact]
    public void StructSizesMatchWin32OnX64()
    {
        Assert.True(Environment.Is64BitProcess);
        Assert.Equal(96, Marshal.SizeOf<POINTER_INFO>());
        Assert.Equal(120, Marshal.SizeOf<POINTER_PEN_INFO>());
        Assert.Equal(144, Marshal.SizeOf<POINTER_TOUCH_INFO>());
        Assert.Equal(152, Marshal.SizeOf<POINTER_TYPE_INFO>());
    }

    [Fact]
    public void FullStroke()
    {
        var (pen, f) = Make();
        pen.Handle(PenKind.Hover, 0, 0, 0, 0, 0, 0);
        pen.Handle(PenKind.Down, 0.5, 0.5, 0.5, 0, 0, 0);
        pen.Handle(PenKind.Move, 1, 1, 1, 0, 0, 0);
        pen.Handle(PenKind.Up, 1, 1, 0, 0, 0, 0);
        pen.Handle(PenKind.HoverExit, 1, 1, 0, 0, 0, 0);

        Assert.Equal(new[] { Hover, Down, Move, Up, OutOfRange }, f.Select(x => x.Flags));
        Assert.Equal(new[] { 0, 1, 0, 2, 0 }, f.Select(x => x.Change));
        Assert.Equal(new uint[] { 0, 512, 1024, 0, 0 }, f.Select(x => x.Pressure));
        Assert.Equal((500, 250), (f[1].X, f[1].Y));
        Assert.Equal((1000, 500), (f[2].X, f[2].Y));
    }

    [Fact]
    public void ContactWithoutHoverSendsHoverFrameFirst()
    {
        var (pen, f) = Make();
        pen.Handle(PenKind.Move, 0.5, 0.5, 0.5, 0, 0, 0);
        Assert.Equal(new[] { Hover, Down }, f.Select(x => x.Flags));
    }

    [Fact]
    public void HoverWhileInContactLiftsFirst()
    {
        var (pen, f) = Make();
        pen.Handle(PenKind.Down, 0.5, 0.5, 0.5, 0, 0, 0);
        f.Clear();
        pen.Handle(PenKind.Hover, 0.5, 0.5, 0, 0, 0, 0);
        Assert.Equal(new[] { Up, Hover }, f.Select(x => x.Flags));
    }

    [Fact]
    public void ReleaseMidStrokeLiftsThenLeavesRangeOnce()
    {
        var (pen, f) = Make();
        pen.Handle(PenKind.Down, 0.25, 0.25, 0.5, 0, 0, 0);
        f.Clear();
        pen.Release();
        pen.Release();
        Assert.Equal(new[] { Up, OutOfRange }, f.Select(x => x.Flags));
        Assert.All(f, x => Assert.Equal((250, 125), (x.X, x.Y)));
    }

    [Fact]
    public void CancelReleases()
    {
        var (pen, f) = Make();
        pen.Handle(PenKind.Down, 0.5, 0.5, 0.5, 0, 0, 0);
        f.Clear();
        pen.Handle(PenKind.Cancel, 0.5, 0.5, 0, 0, 0, 0);
        Assert.Equal(new[] { Up, OutOfRange }, f.Select(x => x.Flags));
    }

    [Fact]
    public void ButtonsAndTiltMask()
    {
        var (pen, f) = Make();
        pen.Handle(PenKind.Hover, 0, 0, 0, 0, 0, PenFlags.Barrel);
        pen.Handle(PenKind.Hover, 0, 0, 0, 30, -100, PenFlags.Eraser | PenFlags.TiltValid);
        Assert.Equal(PEN_FLAG_BARREL, f[0].PenFlags);
        Assert.Equal(PEN_MASK_PRESSURE, f[0].Mask);
        Assert.Equal(PEN_FLAG_ERASER | PEN_FLAG_INVERTED, f[1].PenFlags);
        Assert.Equal(PEN_MASK_PRESSURE | PEN_MASK_TILT_X | PEN_MASK_TILT_Y, f[1].Mask);
    }

    [Theory]
    [InlineData(0, 0, -1920, 0)]
    [InlineData(1, 1, -1, 1079)]
    [InlineData(0.5, 0.5, -960, 540)]
    [InlineData(-3, 7, -1920, 1079)]
    public void MapsToMonitorLeftOfPrimary(double nx, double ny, int x, int y)
    {
        Assert.Equal(new Point(x, y), Mapping.ToPixel(new Rectangle(-1920, 0, 1920, 1080), nx, ny));
    }
}
