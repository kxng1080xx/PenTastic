using System.ComponentModel;
using System.Runtime.InteropServices;
using static PenTastic.Native;

using System.Drawing;
using System.Windows.Forms;

namespace PenTastic;

enum PenKind : byte { Hover = 0, Down = 1, Move = 2, Up = 3, HoverExit = 4, Cancel = 5 }

[Flags]
enum PenFlags : byte { None = 0, Barrel = 1, Eraser = 2, TiltValid = 4 }

sealed class PenInjector : IDisposable
{
    public delegate bool Injector(in POINTER_TYPE_INFO info);

    readonly Injector _inject;
    readonly Func<Point> _origin;
    readonly IntPtr _device;
    readonly object _gate = new();
    bool _inRange, _inContact;
    Point _last;

    public Rectangle Target { get; set; }

    public PenInjector(Rectangle target, Injector? inject = null, Func<Point>? origin = null)
    {
        Target = target;
        _origin = origin ?? (() => SystemInformation.VirtualScreen.Location);
        if (inject != null)
        {
            _inject = inject;
            return;
        }
        _device = CreateSyntheticPointerDevice(PT_PEN, 1, POINTER_FEEDBACK_DEFAULT);
        if (_device == IntPtr.Zero) throw new Win32Exception();
        _inject = (in POINTER_TYPE_INFO i) => InjectSyntheticPointerInput(_device, in i, 1);
    }

    public void Handle(PenKind kind, double nx, double ny, double pressure, int tiltX, int tiltY, PenFlags flags)
    {
        lock (_gate)
        {
            var p = _last = Mapping.ToPixel(Target, nx, ny);

            if (kind is PenKind.HoverExit or PenKind.Cancel)
            {
                Release();
                return;
            }

            if (kind == PenKind.Hover || (kind == PenKind.Up && !_inContact))
            {
                if (_inContact)
                {
                    Send(p, POINTER_FLAG_INRANGE | POINTER_FLAG_UP, POINTER_CHANGE_FIRSTBUTTON_UP, 0, tiltX, tiltY, flags);
                    _inContact = false;
                }
                Send(p, POINTER_FLAG_INRANGE | POINTER_FLAG_UPDATE, POINTER_CHANGE_NONE, 0, tiltX, tiltY, flags);
                _inRange = true;
                return;
            }

            if (!_inContact)
            {
                if (!_inRange)
                    Send(p, POINTER_FLAG_INRANGE | POINTER_FLAG_UPDATE, POINTER_CHANGE_NONE, 0, tiltX, tiltY, flags);
                Send(p, POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_FIRSTBUTTON | POINTER_FLAG_DOWN,
                    POINTER_CHANGE_FIRSTBUTTON_DOWN, pressure, tiltX, tiltY, flags);
                _inRange = _inContact = true;
                return;
            }

            if (kind == PenKind.Up)
            {
                Send(p, POINTER_FLAG_INRANGE | POINTER_FLAG_UP, POINTER_CHANGE_FIRSTBUTTON_UP, 0, tiltX, tiltY, flags);
                _inContact = false;
                return;
            }

            Send(p, POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT | POINTER_FLAG_FIRSTBUTTON | POINTER_FLAG_UPDATE,
                POINTER_CHANGE_NONE, pressure, tiltX, tiltY, flags);
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (_inContact)
            {
                Send(_last, POINTER_FLAG_INRANGE | POINTER_FLAG_UP, POINTER_CHANGE_FIRSTBUTTON_UP);
                _inContact = false;
            }
            if (_inRange)
            {
                Send(_last, POINTER_FLAG_UPDATE, POINTER_CHANGE_NONE);
                _inRange = false;
            }
        }
    }

    void Send(Point p, uint pointerFlags, int change, double pressure = 0, int tiltX = 0, int tiltY = 0, PenFlags flags = 0)
    {
        var info = new POINTER_TYPE_INFO { type = PT_PEN };
        ref var pen = ref info.u.penInfo;
        pen.pointerInfo.pointerType = PT_PEN;
        pen.pointerInfo.pointerFlags = pointerFlags;
        pen.pointerInfo.ButtonChangeType = change;
        var origin = _origin();
        pen.pointerInfo.ptPixelLocation = new POINT { X = p.X - origin.X, Y = p.Y - origin.Y };
        pen.penMask = PEN_MASK_PRESSURE;
        pen.pressure = (uint)Math.Clamp((int)Math.Round(pressure * 1024), 0, 1024);
        if (flags.HasFlag(PenFlags.TiltValid))
        {
            pen.penMask |= PEN_MASK_TILT_X | PEN_MASK_TILT_Y;
            pen.tiltX = Math.Clamp(tiltX, -90, 90);
            pen.tiltY = Math.Clamp(tiltY, -90, 90);
        }
        if (flags.HasFlag(PenFlags.Barrel)) pen.penFlags |= PEN_FLAG_BARREL;
        if (flags.HasFlag(PenFlags.Eraser)) pen.penFlags |= PEN_FLAG_ERASER | PEN_FLAG_INVERTED;

        if (!_inject(in info))
            Log.Write($"inject failed (error {Marshal.GetLastPInvokeError()}) flags=0x{pointerFlags:x}");
    }

    public void Dispose()
    {
        Release();
        if (_device != IntPtr.Zero) DestroySyntheticPointerDevice(_device);
    }
}
