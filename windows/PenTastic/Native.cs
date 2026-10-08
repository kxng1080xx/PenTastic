using System.Runtime.InteropServices;

using System.Drawing;
using System.Windows.Forms;

namespace PenTastic;

[StructLayout(LayoutKind.Sequential)]
struct POINT { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
struct RECT { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
struct POINTER_INFO
{
    public uint pointerType;
    public uint pointerId;
    public uint frameId;
    public uint pointerFlags;
    public IntPtr sourceDevice;
    public IntPtr hwndTarget;
    public POINT ptPixelLocation;
    public POINT ptHimetricLocation;
    public POINT ptPixelLocationRaw;
    public POINT ptHimetricLocationRaw;
    public uint dwTime;
    public uint historyCount;
    public int InputData;
    public uint dwKeyStates;
    public ulong PerformanceCount;
    public int ButtonChangeType;
}

[StructLayout(LayoutKind.Sequential)]
struct POINTER_PEN_INFO
{
    public POINTER_INFO pointerInfo;
    public uint penFlags;
    public uint penMask;
    public uint pressure;
    public uint rotation;
    public int tiltX;
    public int tiltY;
}

[StructLayout(LayoutKind.Sequential)]
struct POINTER_TOUCH_INFO
{
    public POINTER_INFO pointerInfo;
    public uint touchFlags;
    public uint touchMask;
    public RECT rcContact;
    public RECT rcContactRaw;
    public uint orientation;
    public uint pressure;
}

[StructLayout(LayoutKind.Explicit)]
struct POINTER_UNION
{
    [FieldOffset(0)] public POINTER_TOUCH_INFO touchInfo;
    [FieldOffset(0)] public POINTER_PEN_INFO penInfo;
}

[StructLayout(LayoutKind.Sequential)]
struct POINTER_TYPE_INFO
{
    public uint type;
    public POINTER_UNION u;
}

static class Native
{
    public const uint PT_PEN = 3;
    public const uint POINTER_FEEDBACK_DEFAULT = 1;

    public const uint POINTER_FLAG_INRANGE = 0x00000002;
    public const uint POINTER_FLAG_INCONTACT = 0x00000004;
    public const uint POINTER_FLAG_FIRSTBUTTON = 0x00000010;
    public const uint POINTER_FLAG_DOWN = 0x00010000;
    public const uint POINTER_FLAG_UPDATE = 0x00020000;
    public const uint POINTER_FLAG_UP = 0x00040000;

    public const int POINTER_CHANGE_NONE = 0;
    public const int POINTER_CHANGE_FIRSTBUTTON_DOWN = 1;
    public const int POINTER_CHANGE_FIRSTBUTTON_UP = 2;

    public const uint PEN_FLAG_BARREL = 0x1;
    public const uint PEN_FLAG_INVERTED = 0x2;
    public const uint PEN_FLAG_ERASER = 0x4;

    public const uint PEN_MASK_PRESSURE = 0x1;
    public const uint PEN_MASK_TILT_X = 0x4;
    public const uint PEN_MASK_TILT_Y = 0x8;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint mode);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool InjectSyntheticPointerInput(IntPtr device, in POINTER_TYPE_INFO pointerInfo, uint count);

    [DllImport("user32.dll")]
    public static extern void DestroySyntheticPointerDevice(IntPtr device);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetPointerType(uint pointerId, out uint pointerType);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetPointerPenInfo(uint pointerId, out POINTER_PEN_INFO penInfo);
}
